using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RailReader.Core.Services;

/// <summary>
/// Which annotations <see cref="PdfReplyLinker"/> must touch. <see cref="PdfAnnotationWriter"/> tags
/// every annotation involved (child or parent) with a throw-away marker key before saving, then
/// hands the saved bytes and this plan to the linker.
/// </summary>
internal sealed class ReplyLinkPlan
{
    // Unique per save, so the marker can never collide with page content or another producer's keys.
    private readonly string _salt = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, int> _markerByNm = new(StringComparer.Ordinal);

    /// <summary>(child marker, parent marker or -1 to just clear a stale link).</summary>
    public List<(int Child, int Parent)> Links { get; } = [];

    public bool IsEmpty => Links.Count == 0;

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
}

/// <summary>
/// Writes reply linkage (<c>/IRT</c> + <c>/RT</c>) into a PDF that PDFium has just saved.
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
/// file, so promotion is safe.</para>
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
        public int PromotedAs { get; set; } = -1;
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
        bool canPromote = PromotionAllowed(text, trailer);

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
        foreach (var (child, parent) in plan.Links)
        {
            if (!ownerOfMarker.TryGetValue(child, out var childOwner)) continue;
            childOwner.Touched = true;
            childOwner.ParentRef = null;
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
                if (o.PromotedAs >= 0)
                {
                    added[o.PromotedAs] = "\n" + ApplyEdits(text, o.Dict.Start, o.Dict.End, inner) + "\nendobj\n";
                    outer.Add(new Edit(o.Dict.Start, o.Dict.End, $"{o.PromotedAs} 0 R"));
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

        foreach (var e in o.Dict.Entries)
        {
            var name = TokenText(text, toks[e.Name]);
            if (o.MarkerKeys.Any(k => name == "/" + k) || (o.Touched && name is "/IRT" or "/RT"))
                edits.Add(Remove(e));
        }
        if (o.Touched && o.ParentRef is { } parent)
            edits.Add(new Edit(toks[o.Dict.CloseTok].Start, toks[o.Dict.CloseTok].Start, $" /IRT {parent} /RT /R "));
        return edits;
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
            // A stream's body is binary; only a plain dictionary object can be rewritten safely.
            if (c.Tokens.Count == 0 || c.Tokens[0].Kind != K.DictOpen
                || c.Tokens.Any(t => t.Kind == K.Word && text.AsSpan(t.Start, t.End - t.Start).SequenceEqual("stream")))
                return null;
            int i = 0;
            c.Top = ParseDict(text, c, ref i, null);
            return c;
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

    private static void SkipValue(string text, Container c, ref int i, DictNode owner)
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

    /// <summary>
    /// Whether a direct annotation dictionary may be moved into a new indirect object. Always true for
    /// an unencrypted file; for an encrypted one only when the whole file shares one key (/V 5, AES-256) —
    /// earlier handlers key each object's strings by its number, which we cannot redo without the password.
    /// </summary>
    private static bool PromotionAllowed(string text, Trailer t)
    {
        if (t.Encrypt is null) return true;
        var r = Regex.Match(t.Encrypt, @"(\d+)\s+(\d+)\s+R");
        var header = new Regex($@"(?<![0-9]){r.Groups[1].Value}[ \t\r\n]+{r.Groups[2].Value}[ \t\r\n]+obj\b");
        Match? last = null;
        for (var m = header.Match(text); m.Success; m = m.NextMatch()) last = m;
        if (last is null) return false;
        int end = text.IndexOf("endobj", last.Index, StringComparison.Ordinal);
        if (end < 0) return false;
        var v = Regex.Match(text.Substring(last.Index, end - last.Index), @"/V\s+(\d+)");
        return v.Success && int.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture) >= 5;
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
