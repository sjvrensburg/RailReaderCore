using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Which annotations <see cref="PdfReplyLinker"/> must touch. <see cref="PdfAnnotationWriter"/> tags
/// every annotation involved (child, parent, or one to promote) with a throw-away marker key before
/// saving, then hands the saved bytes and this plan to the linker.
/// </summary>
internal sealed class ReplyLinkPlan
{
    // Unique per save, so the marker can never collide with page content or another producer's keys.
    private readonly string _salt = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, int> _markerByNm = new(StringComparer.Ordinal);

    /// <summary>(child marker, parent marker or -1 to just clear a stale link, the /RT to write).</summary>
    public List<(int Child, int Parent, AnnotationReplyType Type)> Links { get; } = [];

    /// <summary>
    /// Carets to (re)draw, by marker. PDFium cannot create a /Caret, so the writer creates a /Square
    /// carrying the caret's /Rect, colour and metadata; the linker changes its subtype, drops what only
    /// a square has, and gives it an appearance stream. With <see cref="CaretRetype.Recolor"/> the
    /// annotation is already a caret (another producer's, which PDFium won't recolour over its /AP):
    /// only its /C, /CA and appearance are replaced.
    /// </summary>
    public Dictionary<int, CaretRetype> Retype { get; } = [];

    /// <summary>The password the document was opened with, needed to encrypt a caret's appearance
    /// stream into an AES-256 file.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Links found while reconciling a page, whose parent has no /NM: (child /NM, the parent's marker —
    /// already set on it —, the /RT the old link had). Turned into <see cref="Links"/> once the child,
    /// which is recreated, can be marked.
    /// </summary>
    public List<(string Child, int ParentMarker, AnnotationReplyType Type)> Carried { get; } = [];

    /// <summary>Markers whose annotation must become an indirect object if it is a direct dictionary,
    /// whether or not it takes part in a link (ISO 32000 requires <c>/Annots</c> to hold references).</summary>
    public List<int> Promote { get; } = [];

    // A marker handed out has been set on an annotation, and only the linker strips it.
    public bool IsEmpty => Links.Count == 0 && Promote.Count == 0 && Retype.Count == 0 && MarkerCount == 0;

    public string KeyFor(int marker) => $"RRM{_salt}x{marker}";

    /// <summary>Marker index for the annotation with this /NM, allocating one on first use.</summary>
    public int MarkerFor(string nm)
    {
        if (!_markerByNm.TryGetValue(nm, out var i)) _markerByNm[nm] = i = _markerByNm.Count;
        return i;
    }

    /// <summary>The marker of <paramref name="nm"/> when it was ever requested, else -1.</summary>
    public int TryGetMarker(string nm) => _markerByNm.TryGetValue(nm, out var i) ? i : -1;

    public int MarkerCount => _markerByNm.Count;

    /// <summary>Pages outside the model that planning loaded to find a reply's parent (diagnostics).</summary>
    public int ExtraPagesSearched { get; set; }
}

/// <summary>
/// A caret's appearance: its /Rect size in PDF user space (unrotated), its fill colour (each 0..1),
/// the page's /Rotate in clockwise quarter-turns — the "^" is drawn so it points up as the page is
/// displayed — and its opacity. <paramref name="Recolor"/>: the annotation is already a caret, whose
/// colour and appearance are replaced but which is otherwise left as it is.
/// </summary>
internal readonly record struct CaretRetype(float Width, float Height, float R, float G, float B,
    int Rotation = 0, bool Recolor = false, float Alpha = 1f);

/// <summary>
/// Writes reply linkage (<c>/IRT</c> + <c>/RT</c>) into a PDF that PDFium has just saved, and moves the
/// annotations PDFium wrote as direct <c>/Annots</c> dictionaries into indirect objects.
///
/// <para>PDFium can create and edit annotation dictionaries but exposes no way to store an
/// <i>indirect reference</i> in them, which is what <c>/IRT</c> is — and the annotations it creates
/// are <i>direct</i> dictionaries inside the page's <c>/Annots</c> array, so there is no object to
/// point at. This is the minimal raw-PDF post-pass the gap needs. It finds the marked annotation
/// dictionaries in the saved bytes, <b>promotes</b> a direct parent to an indirect object (the
/// array element becomes a reference to it), adds <c>/IRT</c> and <c>/RT /R</c> to each reply, and
/// strips the markers. The rewritten objects are appended as a standard <b>incremental update</b> with
/// a classic cross-reference table — the bytes PDFium produced are never modified (so its superseded
/// copies, markers included, stay in the file until the next PDFium save compacts them).</para>
///
/// <para><b>Encryption.</b> The trailer's <c>/Encrypt</c> and <c>/ID</c> are repeated, and objects are
/// copied byte-for-byte, so an encrypted source stays encrypted and its strings stay readable. The
/// one thing that cannot be done blind is moving a direct dictionary into a new object: with
/// RC4/AES-128 (<c>/V</c> &lt; 5) each object's strings are keyed by its number, so promotion is
/// skipped there and the reply is left unlinked. AES-256 (<c>/V</c> 5) uses one key for the whole
/// file, so promotion is safe — and with the password (<see cref="ReplyLinkPlan.Password"/>) the
/// file key is recovered, so a caret's appearance stream is written encrypted
/// (<see cref="PdfAes256Security"/>).</para>
///
/// <para><b>Promotion</b> is not only for parents. ISO 32000-1 (Table 30) says <c>/Annots</c> "shall
/// contain indirect references", and MuPDF (so PyMuPDF) does not see a direct dictionary at all, so every
/// annotation in <see cref="ReplyLinkPlan.Promote"/> that is direct is promoted too. Under RC4/AES-128
/// it stays direct, as above.</para>
///
/// <para>The input is PDFium's own full rewrite (classic table, no object streams, not linearised).
/// Anything outside that — an unrecognised trailer, an annotation inside a stream — makes the linker back off: it returns
/// the input unchanged and logs, so the worst case is a missing link, never a damaged file.</para>
/// </summary>
internal static class PdfReplyLinker
{
    private static readonly Regex ObjectHeader = new(@"(\d+)[ \t\r\n]+(\d+)[ \t\r\n]+obj\b", RegexOptions.Compiled);

    /// <summary>
    /// Returns the PDF with the plan's links written and every marker key stripped, or null when it
    /// could not do both. Null means the markers are still in <paramref name="pdf"/>, so the caller must
    /// not keep those bytes (see <c>PdfAnnotationWriter.WriteReconciled</c>).
    /// </summary>
    public static byte[]? Apply(byte[] pdf, ReplyLinkPlan plan)
    {
        try
        {
            return ApplyCore(pdf, plan);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[PdfReplyLinker] Could not write reply links; saving without them", ex);
            return null;
        }
    }

    // --- one rewrite pass ---

    private sealed class Container(int number, int generation, int bodyStart, int bodyEnd)
    {
        public int Number { get; } = number;
        public int Generation { get; } = generation;
        public int BodyStart { get; } = bodyStart;
        public int BodyEnd { get; } = bodyEnd;
        public List<Tok> Tokens { get; set; } = [];
        public List<DictNode> Dicts { get; set; } = [];
        public DictNode? Top { get; set; }
    }

    /// <summary>An annotation dictionary carrying a marker, and what to do to it.</summary>
    private sealed class Owner(Container container, DictNode dict)
    {
        public Container Container { get; } = container;
        public DictNode Dict { get; } = dict;
        public bool IsIndirect => ReferenceEquals(Dict, Container.Top);
        public List<string> MarkerKeys { get; } = [];
        public bool Touched { get; set; }
        public string? ParentRef { get; set; }
        public AnnotationReplyType ReplyType { get; set; }
        public int PromotedAs { get; set; } = -1;
        public CaretRetype? Retype { get; set; }
        /// <summary>Object number of the caret's appearance stream, or -1 for none.</summary>
        public int AppearanceAs { get; set; } = -1;
    }

    private static byte[]? ApplyCore(byte[] pdf, ReplyLinkPlan plan)
    {
        // Latin-1 maps each byte to one char, so string offsets are byte offsets.
        var text = Encoding.Latin1.GetString(pdf);

        var trailer = ReadTrailer(text);
        if (trailer is null)
        {
            RailReaderLogging.Logger.Warn("[PdfReplyLinker] Unrecognised trailer; reply links not written");
            return null;
        }
        var security = trailer.Encrypt is null ? null : EncryptionOf(text, trailer.Encrypt);
        bool canPromote = trailer.Encrypt is null || security is { V: >= 5 };

        // 1. Find each marker's dictionary.
        var containers = new Dictionary<int, Container?>();
        var owners = new Dictionary<(int Container, int Open), Owner>();
        var ownerOfMarker = new Dictionary<int, Owner>();
        for (int m = 0; m < plan.MarkerCount; m++)
        {
            var key = plan.KeyFor(m);
            if (Locate(text, key) is not { } located) continue;
            if (!containers.TryGetValue(located.Number, out var c))
                containers[located.Number] = c = Parse(text, located);
            if (c is null) continue;

            var dict = c.Dicts.FirstOrDefault(d => d.Entries.Any(e => TokenText(text, c.Tokens[e.Name]) == "/" + key));
            if (dict is null || !LooksLikeAnnotation(text, c, dict)) continue;

            if (!owners.TryGetValue((c.Number, dict.OpenTok), out var owner))
                owners[(c.Number, dict.OpenTok)] = owner = new Owner(c, dict);
            owner.MarkerKeys.Add(key);
            ownerOfMarker[m] = owner;
        }
        // A marker we could not find or rewrite would stay in the file for good.
        if (ownerOfMarker.Count != plan.MarkerCount)
        {
            RailReaderLogging.Logger.Warn("[PdfReplyLinker] Could not locate every annotation; reply links not written");
            return null;
        }

        // 2. Resolve links. A parent that is missing, or direct and unpromotable, is an orphan: the
        //    child's stale link is cleared and nothing dangling is emitted.
        int nextNumber = trailer.Size;
        foreach (var (child, parent, type) in plan.Links)
        {
            if (!ownerOfMarker.TryGetValue(child, out var childOwner)) continue;
            childOwner.Touched = true;
            childOwner.ParentRef = null;
            childOwner.ReplyType = type;
            if (parent < 0 || !ownerOfMarker.TryGetValue(parent, out var parentOwner)) continue;

            if (parentOwner.IsIndirect)
            {
                childOwner.ParentRef = $"{parentOwner.Container.Number} {parentOwner.Container.Generation} R";
            }
            else if (canPromote)
            {
                if (parentOwner.PromotedAs < 0) parentOwner.PromotedAs = nextNumber++;
                childOwner.ParentRef = $"{parentOwner.PromotedAs} 0 R";
            }
            else
            {
                RailReaderLogging.Logger.Debug("[PdfReplyLinker] Parent annotation is direct in a pre-AES-256 encrypted file; reply left unlinked");
            }
        }

        //    Then give every annotation that was written its own object (ISO 32000 Table 30).
        if (canPromote)
            foreach (var m in plan.Promote)
                if (ownerOfMarker.TryGetValue(m, out var o) && !o.IsIndirect && o.PromotedAs < 0)
                    o.PromotedAs = nextNumber++;

        //    Carets. The retype itself is an edit of the dictionary wherever it lives; the appearance
        //    is a new stream object. In an encrypted file that stream must be encrypted, which needs
        //    the file key: AES-256 only (earlier handlers key each object separately, by its number).
        StreamCipher? cipher = null;
        if (plan.Retype.Count > 0 && security is not null)
        {
            cipher = CipherFor(security, plan.Password);
            if (cipher is null)
            {
                // The writer only plans carets where they can be drawn (unencrypted or AES-256); an
                // encryption dictionary we can't read (V 0) or a key we can't recover from the password
                // PDFium accepted means the dictionary was misread. Back off rather than leave a caret
                // no viewer may draw.
                RailReaderLogging.Logger.Warn("[PdfReplyLinker] Could not recover the AES-256 file key; carets not written");
                return null;
            }
        }
        // Every planned caret can be drawn by now: unencrypted, or the cipher was recovered above.
        foreach (var (m, retype) in plan.Retype)
        {
            if (!ownerOfMarker.TryGetValue(m, out var o)) continue;
            o.Retype = retype;
            o.AppearanceAs = nextNumber++;
        }

        // 3. Build the replacement text of every touched container and every promoted dictionary.
        var rewritten = new SortedDictionary<int, (int Generation, string Body)>();
        var added = new SortedDictionary<int, string>();
        foreach (var byContainer in owners.Values.GroupBy(o => o.Container))
        {
            var c = byContainer.Key;
            var outer = new List<Edit>();
            foreach (var o in byContainer)
            {
                var inner = EditsFor(text, o);
                if (o.AppearanceAs >= 0) added[o.AppearanceAs] = CaretAppearanceObject(o.Retype!.Value, InnerMargins(text, o), cipher);
                if (o.PromotedAs >= 0)
                {
                    added[o.PromotedAs] = "\n" + ApplyEdits(text, o.Dict.Start, o.Dict.End, inner) + "\nendobj\n";
                    // Padded: PDFium writes neighbouring dictionaries with no separator (<<…>><<…>>),
                    // and "17 0 R16 0 R" is not two references.
                    outer.Add(new Edit(o.Dict.Start, o.Dict.End, $" {o.PromotedAs} 0 R "));
                }
                else
                {
                    outer.AddRange(inner);
                }
            }
            rewritten[c.Number] = (c.Generation, ApplyEdits(text, c.BodyStart, c.BodyEnd, outer));
        }

        // 4. Append everything as an incremental update.
        using var ms = new MemoryStream(pdf.Length + 4096);
        ms.Write(pdf);
        if (pdf.Length == 0 || pdf[^1] is not ((byte)'\n' or (byte)'\r')) ms.WriteByte((byte)'\n');

        var offsets = new SortedDictionary<int, (long Offset, int Gen)>();
        foreach (var (number, (gen, body)) in rewritten)
        {
            offsets[number] = (ms.Position, gen);
            Append(ms, $"{number} {gen} obj{body}endobj\n");
        }
        foreach (var (number, body) in added)
        {
            offsets[number] = (ms.Position, 0);
            Append(ms, $"{number} 0 obj{body}");
        }

        int size = Math.Max(trailer.Size, nextNumber);
        long xrefOffset = ms.Position;
        WriteXRefTable(ms, trailer, size, offsets);
        Append(ms, $"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    // --- edits ---

    private readonly record struct Edit(int Start, int End, string Replacement);

    private static List<Edit> EditsFor(string text, Owner o)
    {
        var edits = new List<Edit>();
        var toks = o.Container.Tokens;

        // The entry's text span: its name through the end of its value.
        Edit Remove((int Name, int ValStart, int ValEnd) e) => new(toks[e.Name].Start, toks[e.ValEnd - 1].End, "");

        var retype = o.Retype;
        bool recolor = retype is { Recolor: true };
        foreach (var e in o.Dict.Entries)
        {
            var name = TokenText(text, toks[e.Name]);
            if (o.MarkerKeys.Any(k => name == "/" + k) || (o.Touched && name is "/IRT" or "/RT"))
                edits.Add(Remove(e));
            else if (retype is not null && !recolor && name == "/Subtype")
                edits.Add(new Edit(toks[e.ValStart].Start, toks[e.ValEnd - 1].End, "/Caret"));
            // What only the square placeholder has: its fill, border style and PDFium's appearance.
            else if (retype is not null && !recolor && name is "/IC" or "/BS" or "/BE" or "/Border" or "/AP" or "/AS")
                edits.Add(Remove(e));
            // A recoloured caret keeps every key but the ones the new colour and appearance replace.
            else if (recolor && name is "/C" or "/CA" or "/AP" or "/AS")
                edits.Add(Remove(e));
        }
        var close = toks[o.Dict.CloseTok].Start;
        if (o.Touched && o.ParentRef is { } parent)
            edits.Add(new Edit(close, close, $" /IRT {parent} /RT /{(o.ReplyType == AnnotationReplyType.Group ? "Group" : "R")} "));
        if (retype is { } r)
        {
            var sb = new StringBuilder(" ");
            if (recolor)
            {
                sb.Append($"/C [{Num(r.R)} {Num(r.G)} {Num(r.B)}] ");
                if (r.Alpha < 0.999f) sb.Append($"/CA {Num(r.Alpha)} ");
            }
            else
            {
                sb.Append("/Sy /None ");
            }
            if (o.AppearanceAs >= 0) sb.Append($"/AP << /N {o.AppearanceAs} 0 R >> ");
            edits.Add(new Edit(close, close, sb.ToString()));
        }
        return edits;
    }

    private static string Num(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// A caret's normal appearance content: a filled "^" with a notch, filling its box (less the
    /// <paramref name="margins"/> of a /RD, left-bottom-right-top), in its colour. Needs no font, so it
    /// renders the same in every viewer — and viewers that synthesise a caret from /C (most do not) are
    /// not relied on.
    /// <para>The appearance turns with the page, so on a page with /Rotate the "^" is drawn turned the
    /// other way, to point up as the page is displayed. The /Rect is in unrotated user space, so a
    /// caret that is tall on screen is wide there.</para>
    /// </summary>
    internal static string CaretAppearance(CaretRetype c, float[]? margins = null)
    {
        float w = MathF.Max(c.Width, 1f), h = MathF.Max(c.Height, 1f);
        float l = 0, b = 0, r = 0, t = 0;
        if (margins is { Length: 4 } && margins.All(m => m >= 0)
            && margins[0] + margins[2] < w - 1 && margins[1] + margins[3] < h - 1)
            (l, b, r, t) = (margins[0], margins[1], margins[2], margins[3]);
        float iw = w - l - r, ih = h - b - t;

        // Drawn in the displayed frame (u right, v up; dw × dh), then mapped into the form's unrotated space.
        int rot = ((c.Rotation % 4) + 4) % 4;
        float dw = (rot & 1) == 0 ? iw : ih, dh = (rot & 1) == 0 ? ih : iw;
        string P(float u, float v)
        {
            var (x, y) = rot switch
            {
                1 => (dh - v, u),
                2 => (dw - u, dh - v),
                3 => (v, dw - u),
                _ => (u, v),
            };
            return $"{Num(l + x)} {Num(b + y)}";
        }
        return $"{Num(c.R)} {Num(c.G)} {Num(c.B)} rg {P(0, 0)} m {P(dw / 2, dh)} l {P(dw, 0)} l {P(dw / 2, dh * 0.35f)} l h f";
    }

    /// <summary>The appearance as an object body (after "N 0 obj"), its stream encrypted when <paramref name="cipher"/> is set.</summary>
    private static string CaretAppearanceObject(CaretRetype c, float[]? margins, StreamCipher? cipher)
    {
        float w = MathF.Max(c.Width, 1f), h = MathF.Max(c.Height, 1f);
        var data = Encoding.Latin1.GetBytes(CaretAppearance(c, margins));
        if (cipher is not null) data = cipher(data);
        return $"\n<< /Type /XObject /Subtype /Form /BBox [0 0 {Num(w)} {Num(h)}] /Resources << >> /Length {data.Length} >>\n"
               + $"stream\n{Encoding.Latin1.GetString(data)}\nendstream\nendobj\n";
    }

    /// <summary>The numbers of a recoloured caret's /RD, which insets its drawing from its /Rect.</summary>
    private static float[]? InnerMargins(string text, Owner o)
    {
        if (o.Retype is not { Recolor: true }) return null;
        var toks = o.Container.Tokens;
        foreach (var e in o.Dict.Entries)
        {
            if (TokenText(text, toks[e.Name]) != "/RD") continue;
            var values = new List<float>();
            for (int i = e.ValStart; i < e.ValEnd; i++)
                if (toks[i].Kind == K.Word && float.TryParse(TokenText(text, toks[i]), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    values.Add(v);
            return values.Count == 4 ? [.. values] : null;
        }
        return null;
    }

    /// <summary>Applies <paramref name="edits"/> (absolute offsets, non-overlapping) to text[start..end).</summary>
    private static string ApplyEdits(string text, int start, int end, List<Edit> edits)
    {
        var sb = new StringBuilder(text, start, end - start, end - start + 64);
        // Descending, so earlier offsets stay valid; at a shared offset the later-listed edit lands first.
        foreach (var e in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
            sb.Remove(e.Start - start, e.End - e.Start).Insert(e.Start - start, e.Replacement);
        return sb.ToString();
    }

    // --- locating and parsing objects ---

    private readonly record struct Located(int Number, int Generation, int BodyStart, int BodyEnd);

    private static Located? Locate(string text, string key)
    {
        // The key must end at a delimiter, or "…x1" would also match inside "…x10".
        int at = text.Length;
        do
        {
            at = at == 0 ? -1 : text.LastIndexOf("/" + key, at - 1, StringComparison.Ordinal);
        }
        while (at >= 0 && at + key.Length + 1 < text.Length && char.IsAsciiLetterOrDigit(text[at + key.Length + 1]));
        if (at < 0) return null;

        int from = Math.Max(0, text.LastIndexOf("endobj", at, StringComparison.Ordinal));
        Match? header = null;
        for (var m = ObjectHeader.Match(text, from, at - from); m.Success; m = m.NextMatch()) header = m;
        if (header is null) return null;

        int end = text.IndexOf("endobj", at, StringComparison.Ordinal);
        if (end < 0) return null;
        return new Located(
            int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture),
            header.Index + header.Length, end);
    }

    private static Container? Parse(string text, Located l)
    {
        var c = new Container(l.Number, l.Generation, l.BodyStart, l.BodyEnd);
        try
        {
            c.Tokens = Tokenize(text, l.BodyStart, l.BodyEnd);
            // A stream's body is binary; only a plain dictionary object, or an array (an indirect /Annots,
            // whose direct annotations are its elements), can be rewritten safely.
            if (c.Tokens.Count == 0 || c.Tokens[0].Kind is not (K.DictOpen or K.ArrOpen)
                || c.Tokens.Any(t => t.Kind == K.Word && text.AsSpan(t.Start, t.End - t.Start).SequenceEqual("stream")))
                return null;
            int i = 0;
            if (c.Tokens[0].Kind == K.DictOpen)
            {
                c.Top = ParseDict(text, c, ref i, null);
                return c;
            }
            SkipValue(text, c, ref i, null); // no Top: every dictionary in the array is direct
            return i == c.Tokens.Count ? c : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>True for a dictionary that is an annotation, not the page (or page tree node) holding it.</summary>
    private static bool LooksLikeAnnotation(string text, Container c, DictNode d)
    {
        bool Has(string name) => d.Entries.Any(e => TokenText(text, c.Tokens[e.Name]) == name);
        if (!Has("/Subtype")) return false;
        if (Has("/Kids")) return false;
        var type = d.Entries.FirstOrDefault(e => TokenText(text, c.Tokens[e.Name]) == "/Type");
        return type.ValStart == 0 || TokenText(text, c.Tokens[type.ValStart]) is not ("/Page" or "/Pages");
    }

    private static string TokenText(string text, Tok t) => text.Substring(t.Start, t.End - t.Start);

    // --- minimal PDF tokenizer / dictionary parser ---

    private enum K { DictOpen, DictClose, ArrOpen, ArrClose, Name, Str, Word }

    private readonly record struct Tok(K Kind, int Start, int End);

    private sealed class DictNode
    {
        public int OpenTok { get; init; }
        public int CloseTok { get; set; }
        public int Start { get; init; }
        public int End { get; set; }
        public DictNode? Parent { get; init; }
        public List<(int Name, int ValStart, int ValEnd)> Entries { get; } = [];
    }

    private static bool IsWhite(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0';
    private static bool IsDelim(char c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    private static List<Tok> Tokenize(string t, int from, int to)
    {
        var list = new List<Tok>();
        int i = from;
        while (i < to)
        {
            char c = t[i];
            if (IsWhite(c)) { i++; continue; }
            switch (c)
            {
                case '%':
                    while (i < to && t[i] is not ('\n' or '\r')) i++;
                    break;
                case '<' when i + 1 < to && t[i + 1] == '<':
                    list.Add(new Tok(K.DictOpen, i, i + 2)); i += 2;
                    break;
                case '<':
                {
                    int e = t.IndexOf('>', i, to - i);
                    if (e < 0) throw new FormatException("unterminated hex string");
                    list.Add(new Tok(K.Str, i, e + 1)); i = e + 1;
                    break;
                }
                case '>' when i + 1 < to && t[i + 1] == '>':
                    list.Add(new Tok(K.DictClose, i, i + 2)); i += 2;
                    break;
                case '[': list.Add(new Tok(K.ArrOpen, i, i + 1)); i++; break;
                case ']': list.Add(new Tok(K.ArrClose, i, i + 1)); i++; break;
                case '(':
                {
                    int depth = 1, j = i + 1;
                    while (j < to && depth > 0)
                    {
                        if (t[j] == '\\') j += 2;
                        else { if (t[j] == '(') depth++; else if (t[j] == ')') depth--; j++; }
                    }
                    if (depth != 0) throw new FormatException("unterminated string");
                    list.Add(new Tok(K.Str, i, j)); i = j;
                    break;
                }
                case '/':
                {
                    int j = i + 1;
                    while (j < to && !IsWhite(t[j]) && !IsDelim(t[j])) j++;
                    list.Add(new Tok(K.Name, i, j)); i = j;
                    break;
                }
                default:
                {
                    int j = i;
                    while (j < to && !IsWhite(t[j]) && !IsDelim(t[j])) j++;
                    if (j == i) j++; // stray delimiter such as ")" or "}"
                    list.Add(new Tok(K.Word, i, j)); i = j;
                    break;
                }
            }
        }
        return list;
    }

    private static DictNode ParseDict(string text, Container c, ref int i, DictNode? parent)
    {
        var toks = c.Tokens;
        var node = new DictNode { OpenTok = i, Start = toks[i].Start, Parent = parent };
        c.Dicts.Add(node);
        i++;
        while (true)
        {
            if (i >= toks.Count) throw new FormatException("unterminated dictionary");
            if (toks[i].Kind == K.DictClose) break;
            if (toks[i].Kind != K.Name) throw new FormatException("expected a key");
            int name = i++;
            int valStart = i;
            SkipValue(text, c, ref i, node);
            node.Entries.Add((name, valStart, i));
        }
        node.CloseTok = i;
        node.End = toks[i].End;
        i++;
        return node;
    }

    private static void SkipValue(string text, Container c, ref int i, DictNode? owner)
    {
        var toks = c.Tokens;
        if (i >= toks.Count) throw new FormatException("missing value");
        switch (toks[i].Kind)
        {
            case K.DictOpen:
                ParseDict(text, c, ref i, owner);
                break;
            case K.ArrOpen:
                i++;
                while (i < toks.Count && toks[i].Kind != K.ArrClose) SkipValue(text, c, ref i, owner);
                if (i >= toks.Count) throw new FormatException("unterminated array");
                i++;
                break;
            case K.Word when i + 2 < toks.Count && IsInteger(text, toks[i]) && IsInteger(text, toks[i + 1])
                             && toks[i + 2].Kind == K.Word && text[toks[i + 2].Start] == 'R' && toks[i + 2].End - toks[i + 2].Start == 1:
                i += 3; // N G R
                break;
            case K.DictClose or K.ArrClose:
                throw new FormatException("unexpected close");
            default:
                i++;
                break;
        }
    }

    private static bool IsInteger(string text, Tok t)
    {
        if (t.Kind != K.Word) return false;
        for (int i = t.Start; i < t.End; i++) if (!char.IsAsciiDigit(text[i])) return false;
        return true;
    }

    // --- trailer / cross-reference ---

    private sealed record Trailer(long Prev, int Size, string Root, string? Info,
        string? Encrypt, string? Id);

    private static Trailer? ReadTrailer(string text)
    {
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (sx < 0) return null;
        var m = Regex.Match(text[(sx + 9)..], @"^\s*(\d+)");
        if (!m.Success) return null;
        long prev = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (prev <= 0 || prev >= text.Length) return null;

        // PDFium's full rewrite always ends in a classic table (even for an xref-stream source). Any other
        // form is not ours to extend, so the caller backs off.
        if (string.CompareOrdinal(text, (int)prev, "xref", 0, 4) != 0) return null;
        int t = text.IndexOf("trailer", (int)prev, StringComparison.Ordinal);
        if (t < 0 || t > sx) return null;
        var dict = text[(t + 7)..sx];

        var size = Regex.Match(dict, @"/Size\s+(\d+)");
        var root = Regex.Match(dict, @"/Root\s+(\d+\s+\d+\s+R)");
        if (!size.Success || !root.Success) return null;

        string? Ref(string name)
        {
            var r = Regex.Match(dict, $@"/{name}\s+(\d+\s+\d+\s+R)");
            return r.Success ? r.Groups[1].Value : null;
        }
        var encrypt = Ref("Encrypt");
        if (encrypt is null && Regex.IsMatch(dict, @"/Encrypt\s*<<")) return null; // direct dict: can't repeat safely
        var id = Regex.Match(dict, @"/ID\s*\[[^\]]*\]");
        return new Trailer(prev, int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture),
            root.Groups[1].Value, Ref("Info"), encrypt, id.Success ? id.Value : null);
    }

    /// <summary>What the linker needs of the encryption dictionary. <see cref="V"/> 0 when it could not be read.</summary>
    internal sealed record Encryption(int V, int R, string? StmF, string? StmCfm, byte[] O, byte[] U, byte[] OE, byte[] UE);

    internal delegate byte[] StreamCipher(byte[] data);

    /// <summary>
    /// Reads the encryption dictionary the trailer points at. Its version decides whether a direct
    /// annotation dictionary may be moved into a new indirect object: always for an unencrypted file,
    /// for an encrypted one only when the whole file shares one key (/V 5, AES-256) — earlier handlers
    /// key each object's strings by its number, which we cannot redo.
    /// </summary>
    internal static Encryption EncryptionOf(string text, string encryptRef)
    {
        var none = new Encryption(0, 0, null, null, [], [], [], []);
        var r = Regex.Match(encryptRef, @"(\d+)\s+(\d+)\s+R");
        var header = new Regex($@"(?<![0-9]){r.Groups[1].Value}[ \t\r\n]+{r.Groups[2].Value}[ \t\r\n]+obj\b");
        Match? last = null;
        for (var m = header.Match(text); m.Success; m = m.NextMatch()) last = m;
        if (last is null) return none;
        int end = text.IndexOf("endobj", last.Index, StringComparison.Ordinal);
        if (end < 0) return none;

        // An encryption dictionary may not sit in an object stream, and its strings are never encrypted.
        var c = new Container(0, 0, last.Index + last.Length, end);
        try
        {
            c.Tokens = Tokenize(text, c.BodyStart, c.BodyEnd);
            if (c.Tokens.Count == 0 || c.Tokens[0].Kind != K.DictOpen) return none;
            int i = 0;
            c.Top = ParseDict(text, c, ref i, null);
        }
        catch (FormatException)
        {
            return none;
        }
        (int Name, int ValStart, int ValEnd) Entry(DictNode d, string key)
            => d.Entries.FirstOrDefault(x => TokenText(text, c.Tokens[x.Name]) == key);
        string? ValueIn(DictNode d, string key)
            => Entry(d, key) is var e && e.ValEnd > e.ValStart ? TokenText(text, c.Tokens[e.ValStart]) : null;
        DictNode? DictIn(DictNode? d, string key)
            => d is not null && Entry(d, key) is var e && e.ValEnd > e.ValStart
                ? c.Dicts.FirstOrDefault(x => x.OpenTok == e.ValStart) : null;
        string? Value(string key) => ValueIn(c.Top, key);
        int Int(string key) => int.TryParse(Value(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        byte[] Str(string key) => Value(key) is { } v && v.Length > 0 && v[0] is '(' or '<' ? DecodeString(v) : [];
        // The stream crypt filter's method: /CF << /StdCF << /CFM /AESV3 … >> >>, keyed by /StmF's name.
        var stmF = Value("/StmF");
        var filter = stmF is { Length: > 1 } && stmF[0] == '/' ? DictIn(DictIn(c.Top, "/CF"), stmF) : null;
        var cfm = filter is null ? null : ValueIn(filter, "/CFM");
        return new Encryption(Int("/V"), Int("/R"), stmF, cfm, Str("/O"), Str("/U"), Str("/OE"), Str("/UE"));
    }

    /// <summary>
    /// How to encrypt a new stream in this file, or null when it can't be done: anything before
    /// AES-256, or a password that recovers no key. A stream crypt filter of /Identity — also the
    /// default when /StmF is absent (ISO 32000-1 Table 20) — means streams are stored in the clear.
    /// </summary>
    internal static StreamCipher? CipherFor(Encryption e, string? password)
    {
        if (e.V < 5) return null;
        // So does a crypt filter whose method is /None.
        if (e.StmF is null or "/Identity" || e.StmCfm == "/None") return data => data;
        if (PdfAes256Security.FileKey(e.R, e.O, e.U, e.OE, e.UE, password) is not { } key) return null;
        return data => PdfAes256Security.EncryptStream(key, data);
    }

    /// <summary>The bytes of a PDF literal <c>(…)</c> or hex <c>&lt;…&gt;</c> string token.</summary>
    internal static byte[] DecodeString(string token)
    {
        var bytes = new List<byte>();
        if (token[0] == '<')
        {
            var hex = new StringBuilder();
            foreach (var ch in token.AsSpan(1, token.Length - 2)) if (char.IsAsciiHexDigit(ch)) hex.Append(ch);
            if (hex.Length % 2 == 1) hex.Append('0');
            return Convert.FromHexString(hex.ToString());
        }
        for (int i = 1; i < token.Length - 1; i++)
        {
            char ch = token[i];
            // An unescaped end-of-line (CR, LF or CRLF) in a literal string is one LF (ISO 32000 7.3.4.2).
            if (ch == '\r')
            {
                if (i + 1 < token.Length - 1 && token[i + 1] == '\n') i++;
                bytes.Add(10);
                continue;
            }
            if (ch != '\\') { bytes.Add((byte)ch); continue; }
            if (++i >= token.Length - 1) break;
            ch = token[i];
            switch (ch)
            {
                case 'n': bytes.Add(10); break;
                case 'r': bytes.Add(13); break;
                case 't': bytes.Add(9); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case '\r':
                    if (i + 1 < token.Length - 1 && token[i + 1] == '\n') i++;
                    break; // line continuation
                case '\n': break;
                case >= '0' and <= '7':
                {
                    int v = 0, n = 0;
                    while (n < 3 && i < token.Length - 1 && token[i] is >= '0' and <= '7') { v = v * 8 + (token[i] - '0'); i++; n++; }
                    i--;
                    bytes.Add((byte)v);
                    break;
                }
                default: bytes.Add((byte)ch); break; // \( \) \\ and any other escaped character
            }
        }
        return [.. bytes];
    }

    private static string TrailerEntries(Trailer t)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"/Root {t.Root} /Prev {t.Prev} ");
        if (t.Info is not null) sb.Append($"/Info {t.Info} ");
        if (t.Encrypt is not null) sb.Append($"/Encrypt {t.Encrypt} ");
        if (t.Id is not null) sb.Append(t.Id).Append(' ');
        return sb.ToString();
    }

    private static void WriteXRefTable(MemoryStream ms, Trailer t, int size,
        SortedDictionary<int, (long Offset, int Gen)> objects)
    {
        Append(ms, "xref\n");
        var numbers = objects.Keys.ToList();
        for (int i = 0; i < numbers.Count;)
        {
            int j = i;
            while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1) j++;
            Append(ms, $"{numbers[i]} {j - i + 1}\n");
            for (int k = i; k <= j; k++)
            {
                var (offset, gen) = objects[numbers[k]];
                // Each entry is exactly 20 bytes, "oooooooooo ggggg n \n".
                Append(ms, string.Create(CultureInfo.InvariantCulture, $"{offset:D10} {gen:D5} n \n"));
            }
            i = j + 1;
        }
        Append(ms, string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} {TrailerEntries(t)}>>\n"));
    }

    private static void Append(MemoryStream ms, string s) => ms.Write(Encoding.Latin1.GetBytes(s));
}
