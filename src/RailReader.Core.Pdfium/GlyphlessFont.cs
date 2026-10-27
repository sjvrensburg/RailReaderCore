using System.Buffers.Binary;
using System.Text;

namespace RailReader.Core.Services;

/// <summary>
/// Synthesises a minimal glyphless TrueType font for the invisible OCR text layer
/// (issue #155): every glyph carries the same trivial outline and text written
/// with it is put in render mode 3 (invisible), so it can never paint.
/// This is the <c>ocrmypdf</c>/Tesseract "GlyphLessFont" approach — the standard-14
/// fonts are WinAnsi-only, so a real embedded CID font with a <c>/ToUnicode</c>
/// CMap is the only way CJK and other non-Latin OCR packs round-trip.
///
/// <para>Glyphs are assigned <b>densely</b>: the caller passes the exact set of
/// codepoints used, glyph <c>i + 1</c> serves the <c>i</c>-th codepoint, and the
/// <c>/ToUnicode</c> CMap (<see cref="BuildToUnicodeCmap"/>) maps those CIDs back
/// to Unicode. The font therefore costs a few hundred bytes per distinct
/// character actually present — not per codepoint in the BMP range spanned —
/// however high the highest character sits. Characters outside the BMP cannot
/// be represented by a two-byte CID and are replaced with U+FFFD by the writer
/// before they reach this font.</para>
///
/// <para>The font is generated in memory (AOT-safe, no embedded assets, no file
/// IO). <c>unitsPerEm</c> is 1000 and every glyph advances 1000 units, which
/// makes the natural advance of an <c>n</c>-character string exactly
/// <c>n × fontSize</c> — the writer's horizontal scaling then reduces to one
/// division per text unit.</para>
/// </summary>
internal static class GlyphlessFont
{
    public const string FontName = OcrProvenance.FontName;
    public const int UnitsPerEm = 1000;

    /// <summary>
    /// Builds the <c>/ToUnicode</c> CMap for a dense CID assignment:
    /// <paramref name="cidToUnicode"/> maps each assigned CID to its Unicode
    /// codepoint (BMP only). Entries are chunked into <c>bfchar</c> blocks of
    /// at most 100, as the CMap spec requires.
    /// </summary>
    public static string BuildToUnicodeCmap(IReadOnlyDictionary<int, int> cidToUnicode)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = cidToUnicode.OrderBy(kv => kv.Key).ToList();
        for (int i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append($"{chunk.Count} beginbfchar\n");
            foreach (var (cid, unicode) in chunk)
                sb.Append($"<{cid:X4}> <{unicode:X4}>\n");
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    /// <summary>Identity CIDToGIDMap over the dense glyph range: CID <c>n</c> maps to glyph <c>n</c>.</summary>
    public static byte[] BuildCidToGidMap(int numGlyphs)
    {
        var map = new byte[numGlyphs * 2];
        for (int i = 0; i < numGlyphs; i++)
            BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(i * 2), (ushort)i);
        return map;
    }

    /// <summary>
    /// Builds the TTF with exactly <paramref name="numGlyphs"/> glyphs.
    /// <paramref name="codeToGlyph"/> is the font <c>cmap</c>: Unicode codepoint
    /// → glyph index (the dense CID assignment); null maps the whole glyph
    /// range identically. Every glyph is the same 1000×1000 rectangle.
    /// </summary>
    public static byte[] Build(int numGlyphs, IReadOnlyDictionary<int, int>? codeToGlyph = null)
    {
        numGlyphs = Math.Clamp(numGlyphs, 2, 0x10000);

        var glyph = BuildGlyph();
        var glyf = new byte[glyph.Length * numGlyphs];
        for (int i = 0; i < numGlyphs; i++) glyph.CopyTo(glyf.AsSpan(i * glyph.Length));
        var loca = new byte[(numGlyphs + 1) * 4];
        for (int i = 0; i <= numGlyphs; i++)
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4), (uint)(i * glyph.Length));

        int lastChar = codeToGlyph is { Count: > 0 } ? codeToGlyph.Keys.Max() : numGlyphs - 1;
        var tables = new List<(string Tag, byte[] Data)>
        {
            ("OS/2", BuildOs2(lastChar)),
            ("cmap", BuildCmap(codeToGlyph, numGlyphs)),
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
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(42), UnitsPerEm); // yMax (x/yMin stay 0)
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(40), UnitsPerEm); // xMax
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
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(16), UnitsPerEm); // xMaxExtent
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

    private static byte[] BuildCmap(IReadOnlyDictionary<int, int>? codeToGlyph, int numGlyphs)
    {
        // Format 12: one group per codepoint (dense assignment is not a range),
        // or a single identity group when no explicit mapping was supplied.
        var groups = new List<(int Start, int End, int Glyph)>();
        if (codeToGlyph is { Count: > 0 })
        {
            foreach (var (code, glyph) in codeToGlyph.OrderBy(kv => kv.Key))
                if (glyph >= 0 && glyph < numGlyphs)
                    groups.Add((code, code, glyph));
        }
        else
        {
            groups.Add((0, numGlyphs - 1, 0));
        }
        if (groups.Count == 0) groups.Add((0, 0, 0));

        int length = 16 + groups.Count * 12;
        var c = new byte[12 + length];
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(0), 0);   // version
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(2), 1);   // numTables
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(6), 10);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(12), 12); // format
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(16), (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(24), (uint)groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            int at = 28 + i * 12;
            BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(at), (uint)groups[i].Start);
            BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(at + 4), (uint)groups[i].End);
            BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(at + 8), (uint)groups[i].Glyph);
        }
        return c;
    }

    private static byte[] BuildOs2(int lastChar)
    {
        var b = new byte[78];
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2), UnitsPerEm); // xAvgCharWidth
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 400);       // usWeightClass
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), 5);         // usWidthClass
        byte[] vend = Encoding.ASCII.GetBytes("RR  ");
        vend.CopyTo(b.AsSpan(58));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(62), 0x40);    // fsSelection: REGULAR
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(66), (ushort)Math.Clamp(lastChar, 0, 0xFFFF)); // usLastCharIndex
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
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), 1);         // isFixedPitch
        return b;
    }
}
