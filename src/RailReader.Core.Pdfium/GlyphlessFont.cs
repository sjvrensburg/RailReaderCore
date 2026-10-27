using System.Buffers.Binary;
using System.Text;

namespace RailReader.Core.Services;

/// <summary>
/// Synthesises a minimal glyphless TrueType font for the invisible OCR text layer
/// (issue #155): every glyph carries the same trivial outline and text written
/// with it is put in render mode 3 (invisible), so it can never paint, and every
/// codepoint up to a caller-chosen maximum exists as a glyph.
/// This is the <c>ocrmypdf</c>/Tesseract "GlyphLessFont" approach — the standard-14
/// fonts are WinAnsi-only, so a real embedded CID font with an identity
/// <c>/ToUnicode</c> is the only way CJK and other non-Latin OCR packs round-trip.
///
/// <para>The font is generated in memory (AOT-safe, no embedded assets, no file IO):
/// glyph count adapts to the largest codepoint actually used, so a Latin document
/// embeds a few KB and only a full-CJK document pays for a large glyph table.
/// <c>unitsPerEm</c> is 1000 and every glyph advances 1000 units, which makes the
/// natural advance of an <c>n</c>-character string exactly <c>n × fontSize</c> — the
/// writer's horizontal scaling then reduces to one division per text unit.</para>
/// </summary>
internal static class GlyphlessFont
{
    public const string FontName = "RailReaderGlyphless";
    public const int UnitsPerEm = 1000;

    /// <summary>The identity <c>/ToUnicode</c> CMap handed to PDFium for the CID font.</summary>
    public const string ToUnicodeCmap =
        "/CIDInit /ProcSet findresource begin\n" +
        "12 dict begin\n" +
        "begincmap\n" +
        "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
        "/CMapName /Adobe-Identity-UCS def\n" +
        "/CMapType 2 def\n" +
        "1 begincodespacerange\n" +
        "<0000> <FFFF>\n" +
        "endcodespacerange\n" +
        "1 beginbfrange\n" +
        "<0000> <FFFF> <0000>\n" +
        "endbfrange\n" +
        "endcmap\n" +
        "CMapName currentdict /CMap defineresource pop\n" +
        "end\n" +
        "end\n";

    /// <summary>Identity CIDToGIDMap: CID <c>n</c> maps to glyph <c>n</c>.</summary>
    public static byte[] BuildCidToGidMap(int numGlyphs)
    {
        var map = new byte[numGlyphs * 2];
        for (int i = 0; i < numGlyphs; i++)
            BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(i * 2), (ushort)i);
        return map;
    }

    /// <summary>
    /// Builds the TTF for codepoints <c>0..maxCodepoint</c> (clamped to the BMP).
    /// Glyphs are all empty: <c>glyf</c> is zero-length and every <c>loca</c> offset is 0.
    /// </summary>
    public static byte[] Build(int maxCodepoint)
    {
        int numGlyphs = Math.Clamp(maxCodepoint + 1, 2, 0x10000);

        var glyph = BuildGlyph();
        var glyf = new byte[glyph.Length * numGlyphs];
        for (int i = 0; i < numGlyphs; i++) glyph.CopyTo(glyf.AsSpan(i * glyph.Length));
        var loca = new byte[(numGlyphs + 1) * 4];
        for (int i = 0; i <= numGlyphs; i++)
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4), (uint)(i * glyph.Length));

        var tables = new List<(string Tag, byte[] Data)>
        {
            ("OS/2", BuildOs2()),
            ("cmap", BuildCmap()),
            ("glyf", glyf),
            ("head", BuildHead()),
            ("hhea", BuildHhea()),
            ("hmtx", BuildHmtx(numGlyphs)),
            ("loca", loca),
            ("maxp", BuildMaxp(numGlyphs)),
            ("name", BuildName()),
            ("post", BuildPost()),
        };
        tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

        int numTables = tables.Count;
        int searchRange = 1;
        while (searchRange * 2 <= numTables) searchRange *= 2;
        searchRange *= 16;
        int entrySelector = 0;
        for (int v = numTables; v > 1; v >>= 1) entrySelector++;
        int rangeShift = numTables * 16 - searchRange;

        using var ms = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void Tag(string t) { foreach (char c in t) ms.WriteByte((byte)c); }

        U32(0x00010000);
        U16(numTables); U16(searchRange); U16(entrySelector); U16(rangeShift);

        int headerSize = 12 + numTables * 16;
        int offset = headerSize;
        var offsets = new int[numTables];
        for (int i = 0; i < numTables; i++)
        {
            offsets[i] = offset;
            offset += (tables[i].Data.Length + 3) & ~3;
        }

        for (int i = 0; i < numTables; i++)
        {
            Tag(tables[i].Tag);
            U32(Checksum(tables[i].Data));
            U32((uint)offsets[i]);
            U32((uint)tables[i].Data.Length);
        }
        for (int i = 0; i < numTables; i++)
        {
            ms.Write(tables[i].Data);
            for (int pad = tables[i].Data.Length; pad % 4 != 0; pad++) ms.WriteByte(0);
        }

        var font = ms.ToArray();
        // checkSumAdjustment = 0xB1B0AFBA - checksum(whole font with the field zeroed).
        int headIndex = tables.FindIndex(t => t.Tag == "head");
        uint adjustment = 0xB1B0AFBAu - Checksum(font);
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(offsets[headIndex] + 8), adjustment);
        return font;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint v = 0;
            for (int j = 0; j < 4; j++)
                v = (v << 8) | (i + j < data.Length ? data[i + j] : (byte)0);
            sum += v;
        }
        return sum;
    }

    private static byte[] BuildHead()
    {
        var b = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), UnitsPerEm);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(46), 8);   // lowestRecPPEM
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(48), 2);   // fontDirectionHint
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(50), 1);   // indexToLocFormat: long
        return b;
    }

    /// <summary>
    /// One glyph: a 1000×1000 rectangle. The outline must exist (an empty glyph
    /// yields a degenerate char box on read-back), but it can never paint —
    /// the writer puts every text object in render mode 3 (invisible).
    /// </summary>
    private static byte[] BuildGlyph()
    {
        var b = new byte[36];
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(0), 1);        // numberOfContours
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6), UnitsPerEm);  // xMax
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(8), UnitsPerEm);  // yMax
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10), 3);      // endPtsOfContours[0]
        // instructionLength @12 = 0; flags @14..17 = on-curve ×4
        for (int i = 0; i < 4; i++) b[14 + i] = 0x01;
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(20), UnitsPerEm);  // x: 0, +1000, 0, -1000
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(24), -UnitsPerEm);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(30), UnitsPerEm);  // y: 0, 0, +1000, 0
        return b;
    }

    private static byte[] BuildHhea()
    {
        var b = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0), 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(4), UnitsPerEm); // ascender
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10), UnitsPerEm); // advanceWidthMax
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(34), 1);  // numberOfHMetrics
        return b;
    }

    private static byte[] BuildMaxp(int numGlyphs)
    {
        var b = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)numGlyphs);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), 4);   // maxPoints
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), 1);   // maxContours
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(14), 1);  // maxZones
        return b;
    }

    private static byte[] BuildHmtx(int numGlyphs)
    {
        // One shared metric (advance 1000, lsb 0), then a left-side bearing per remaining glyph.
        var b = new byte[4 + (numGlyphs - 1) * 2];
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0), UnitsPerEm);
        return b;
    }

    private static byte[] BuildCmap()
    {
        var c = new byte[12 + 28];
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(0), 0);   // version
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(2), 1);   // numTables
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(6), 10);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(12), 12); // format
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(16), 28); // length
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(24), 1);  // nGroups
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(28), 0);  // startCharCode
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(32), 0xFFFF); // endCharCode
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(36), 0);  // startGlyphID
        return c;
    }

    private static byte[] BuildOs2()
    {
        var b = new byte[78];
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2), UnitsPerEm); // xAvgCharWidth
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 400);       // usWeightClass
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), 5);         // usWidthClass
        byte[] vend = Encoding.ASCII.GetBytes("RR  ");
        vend.CopyTo(b.AsSpan(58));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(62), 0x40);    // fsSelection: REGULAR
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(66), 0xFFFF);  // usLastCharIndex
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(68), UnitsPerEm); // sTypoAscender
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(74), UnitsPerEm); // usWinAscent
        return b;
    }

    private static byte[] BuildName()
    {
        var text = Encoding.BigEndianUnicode.GetBytes(FontName);
        // Three records (family, full, PostScript), all platform 3 / encoding 1 / en-US.
        int count = 3;
        int storage = 6 + count * 12;
        var b = new byte[storage + text.Length * count];
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)storage);
        ushort[] ids = [1, 4, 6];
        for (int i = 0; i < count; i++)
        {
            int at = 6 + i * 12;
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), 3);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at + 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at + 4), 0x409);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at + 6), ids[i]);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at + 8), (ushort)text.Length);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at + 10), (ushort)(i * text.Length));
            text.CopyTo(b.AsSpan(storage + i * text.Length));
        }
        return b;
    }

    private static byte[] BuildPost()
    {
        var b = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0), 0x00030000); // format 3: no glyph names
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), 1);         // isFixedPitch
        return b;
    }
}
