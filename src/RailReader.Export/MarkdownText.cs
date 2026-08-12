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

    private static bool IsInvisible(char c) =>
        (c < ' ' && c is not ('\t' or '\n' or '\r'))   // C0 controls, incl. PDFium's U+0002
        || c == '\u007F'                                // DEL
        || (c >= '\u0080' && c <= '\u009F')             // C1 controls
        || c == '\u00AD';                               // soft hyphen
}
