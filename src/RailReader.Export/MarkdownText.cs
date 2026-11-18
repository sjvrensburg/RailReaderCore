namespace RailReader.Export;

/// <summary>
/// Sanitises text on its way from the extraction layer into user-facing Markdown.
/// </summary>
/// <remarks>
/// PDFium's text layer carries in-band markers that are meaningful to a text-extraction
/// consumer but meaningless — and invisible — in rendered Markdown. The one that bites is
/// U+0002, which PDFium emits where a producer split a word across a line break with a
/// hyphen ("inter-" + "pretable" comes back as "interpretable"): the export *looks*
/// like it says "interpretable" while containing no such string, so neither grep nor a
/// reader's Ctrl-F finds it, and anything that quotes text back out of the export carries
/// the marker into the quote (issue #101).
///
/// The text-service contract keeps the marker — a consumer that wants the distinction can
/// act on it. Markdown is the wrong place for it, so it is dropped here, along with the
/// rest of the invisible-control class it belongs to: C0/C1 controls other than tab, LF
/// and CR (which carry real layout), DEL, and the soft hyphen U+00AD, which is the same
/// bug wearing a different code point.
/// </remarks>
internal static class MarkdownText
{
    /// <summary>
    /// Strips invisible control characters. Returns the input instance unchanged when it
    /// holds none — the overwhelmingly common case, so no allocation for clean text.
    /// </summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsInvisible(text[i])) { first = i; break; }
        }
        if (first < 0)
            return text;

        var buf = new char[text.Length];
        text.AsSpan(0, first).CopyTo(buf);
        int n = first;
        for (int i = first; i < text.Length; i++)
        {
            char c = text[i];
            if (!IsInvisible(c))
                buf[n++] = c;
        }
        return new string(buf, 0, n);
    }

    /// <summary>Null-tolerant <see cref="Clean(string)"/>.</summary>
    public static string? CleanOrNull(string? text) => text == null ? null : Clean(text);

    /// <summary>
    /// Makes a short host-supplied label (a colour-category name) safe to embed inline in
    /// Markdown: invisible controls stripped, whitespace (including line breaks, which would
    /// end a blockquote) collapsed to single spaces, and Markdown punctuation backslash-escaped
    /// so it cannot open emphasis, a link, a code span or an HTML tag.
    /// </summary>
    public static string EscapeInline(string text)
    {
        var flat = CollapseWhitespace(Clean(text));
        var sb = new System.Text.StringBuilder(flat.Length + 8);
        foreach (var c in flat)
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '#' or '|' or '~')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Makes a short label safe inside an HTML comment (<c>&lt;!-- … --&gt;</c>), where
    /// backslash escapes mean nothing: whitespace is collapsed and every <c>--</c> is broken
    /// up, so the label can never close the comment early.
    /// </summary>
    public static string HtmlCommentSafe(string text)
    {
        var flat = CollapseWhitespace(Clean(text));
        while (flat.Contains("--", StringComparison.Ordinal))
            flat = flat.Replace("--", "- -", StringComparison.Ordinal);
        return flat;
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsInvisible(char c) =>
        (c < ' ' && c is not ('\t' or '\n' or '\r'))   // C0 controls, incl. PDFium's U+0002
        || c == '\u007F'                                // DEL
        || (c >= '\u0080' && c <= '\u009F')             // C1 controls
        || c == '\u00AD';                               // soft hyphen
}
