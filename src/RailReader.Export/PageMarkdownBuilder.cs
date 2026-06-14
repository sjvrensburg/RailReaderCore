using System.Text;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Export;

/// <summary>
/// Assembles Markdown output for a single page from classified layout blocks,
/// resolved heading levels, extracted text, VLM results, and annotations.
/// </summary>
public static class PageMarkdownBuilder
{
    public record VlmBlockResult(int BlockIndex, string? Text, string? Error);

    /// <summary>
    /// Reader-visible annotations for a page, in document order. Holds every surfaced
    /// type (text markups, sticky notes, typewriter/FreeText, carets, and commented
    /// drawings) rather than a fixed pair of lists, so adding a type needs no shape change.
    /// </summary>
    public record PageAnnotations(IReadOnlyList<Annotation> Annotations);

    /// <summary>
    /// Builds Markdown for a single page from layout blocks.
    /// </summary>
    public static string Build(
        IReadOnlyList<LayoutBlock> blocks,
        IReadOnlyDictionary<int, int> headingLevels,
        IReadOnlyDictionary<int, string> blockTexts,
        IReadOnlyDictionary<int, VlmBlockResult>? vlmResults,
        IReadOnlyDictionary<int, string>? figurePaths)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < blocks.Count; i++)
        {
            var role = blocks[i].Role;
            var vlm = vlmResults?.GetValueOrDefault(i);
            blockTexts.TryGetValue(i, out var text);

            var blockMd = RenderBlock(role, i, text, headingLevels, vlm, figurePaths);
            if (blockMd != null)
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(blockMd);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds plain-text Markdown for a page with no layout analysis.
    /// Uses outline entries at page boundaries for heading markers.
    /// </summary>
    public static string BuildPlainText(
        PageText pageText,
        IReadOnlyList<HeadingLevelResolver.FlatOutlineEntry> flatOutline,
        int pageIndex,
        PageAnnotations? annotations)
    {
        var pageHeadings = flatOutline.Where(e => e.Page == pageIndex).ToList();
        return BuildPlainTextForPage(pageText, pageHeadings, annotations);
    }

    /// <summary>
    /// Builds plain-text Markdown using outline entries already filtered to this page.
    /// Avoids the per-page outline re-scan that <see cref="BuildPlainText"/> performs.
    /// </summary>
    public static string BuildPlainTextForPage(
        PageText pageText,
        IReadOnlyList<HeadingLevelResolver.FlatOutlineEntry> pageHeadings,
        PageAnnotations? annotations)
    {
        var sb = new StringBuilder();

        foreach (var heading in pageHeadings)
        {
            var prefix = new string('#', Math.Clamp(heading.Depth, 1, 6));
            sb.AppendLine($"{prefix} {heading.Title}");
            sb.AppendLine();
        }

        var trimmed = pageText.Text.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            sb.AppendLine(trimmed);
            sb.AppendLine();
        }

        if (annotations != null)
            AppendAnnotations(sb, annotations, pageText);

        return sb.ToString();
    }

    /// <summary>
    /// Appends annotation blockquotes in document order. Text markups
    /// (highlight/underline/strikeout/squiggly) render the text they cover (when
    /// pageText is available) plus the reviewer's attached comment; sticky notes and
    /// FreeText render their body; carets and commented drawings render their comment.
    /// </summary>
    internal static void AppendAnnotations(StringBuilder sb, PageAnnotations annotations, PageText? pageText = null)
    {
        if (annotations.Annotations.Count == 0)
            return;

        sb.AppendLine();

        foreach (var ann in annotations.Annotations)
            AppendAnnotation(sb, ann, pageText);
    }

    private static void AppendAnnotation(StringBuilder sb, Annotation ann, PageText? pageText)
    {
        switch (ann)
        {
            case TextMarkupAnnotation markup:
                AppendMarkup(sb, markup, pageText);
                break;
            case TextNoteAnnotation note:
                AppendComment(sb, "Note", note.EffectiveContents);
                break;
            case FreeTextAnnotation freeText:
                AppendComment(sb, "Comment", freeText.EffectiveContents);
                break;
            case CaretAnnotation caret:
                AppendComment(sb, "Inserted text", caret.EffectiveContents);
                break;
            case RectAnnotation rect:
                AppendComment(sb, "Box", rect.EffectiveContents);
                break;
            case FreehandAnnotation ink:
                AppendComment(sb, "Drawing", ink.EffectiveContents);
                break;
        }
    }

    /// <summary>
    /// Renders a text-markup annotation: the covered text (or a colour marker when the
    /// text can't be recovered) followed by the reviewer's comment if one is attached.
    /// </summary>
    private static void AppendMarkup(StringBuilder sb, TextMarkupAnnotation markup, PageText? pageText)
    {
        var label = MarkupLabel(markup);
        var covered = ExtractMarkupText(markup, pageText);

        if (covered != null)
        {
            sb.AppendLine($"> {covered}");
            sb.AppendLine($"<!-- {label}: {markup.Color} -->");
        }
        else
        {
            sb.AppendLine($"> [{label}: {markup.Color}]");
        }

        // The reviewer's note attached to the markup (PDF /Contents) — for moderation
        // this comment is the substance, distinct from the text being marked.
        var comment = markup.EffectiveContents;
        if (!string.IsNullOrWhiteSpace(comment))
            sb.AppendLine($"> — {comment.Trim()}");

        sb.AppendLine();
    }

    private static string MarkupLabel(TextMarkupAnnotation markup) => markup switch
    {
        UnderlineAnnotation => "underline",
        StrikeOutAnnotation => "strikeout",
        SquigglyAnnotation => "squiggly",
        _ => "highlight",
    };

    private static string? ExtractMarkupText(TextMarkupAnnotation markup, PageText? pageText)
    {
        if (pageText == null || markup.Rects.Count == 0)
            return null;

        var texts = new List<string>();
        foreach (var rect in markup.Rects)
        {
            var t = pageText.ExtractTextInRect(rect.X, rect.Y, rect.X + rect.W, rect.Y + rect.H);
            if (t != null) texts.Add(t);
        }
        return texts.Count > 0 ? string.Join(" ", texts) : null;
    }

    private static void AppendComment(StringBuilder sb, string label, string contents)
    {
        if (string.IsNullOrWhiteSpace(contents))
            return;
        sb.AppendLine($"> **{label}:** {contents.Trim()}");
        sb.AppendLine();
    }

    private static string? RenderBlock(
        BlockRole role,
        int blockIndex,
        string? text,
        IReadOnlyDictionary<int, int> headingLevels,
        VlmBlockResult? vlm,
        IReadOnlyDictionary<int, string>? figurePaths)
    {
        return role switch
        {
            BlockRole.Title or BlockRole.Heading => RenderHeading(blockIndex, text, headingLevels),
            BlockRole.Text or BlockRole.Aside or BlockRole.Reference or BlockRole.Footnote => RenderTextBlock(text),
            BlockRole.DisplayMath or BlockRole.InlineMath or BlockRole.Algorithm => RenderEquation(vlm, text),
            BlockRole.Table => RenderTable(vlm, text),
            BlockRole.Figure or BlockRole.Chart => RenderFigure(blockIndex, vlm, figurePaths),
            BlockRole.Caption => RenderFigureTitle(text),
            BlockRole.Header or BlockRole.Footer or BlockRole.PageNumber or BlockRole.Decoration => null,
            _ => RenderTextBlock(text),
        };
    }

    private static string? RenderHeading(int blockIndex, string? text, IReadOnlyDictionary<int, int> headingLevels)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        int level = headingLevels.GetValueOrDefault(blockIndex, 2);
        var prefix = new string('#', level);
        return $"{prefix} {text.Trim()}";
    }

    private static string? RenderTextBlock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text.Trim();
    }

    private static string RenderEquation(VlmBlockResult? vlm, string? fallbackText)
    {
        if (vlm?.Text != null)
            return $"$${vlm.Text.Trim()}$$";

        if (!string.IsNullOrWhiteSpace(fallbackText))
            return $"[equation: {fallbackText.Trim()}]";

        return "[equation]";
    }

    private static string RenderTable(VlmBlockResult? vlm, string? fallbackText)
    {
        if (vlm?.Text != null)
            return vlm.Text.Trim();

        if (!string.IsNullOrWhiteSpace(fallbackText))
        {
            var sb = new StringBuilder();
            sb.AppendLine("```");
            sb.AppendLine(fallbackText.Trim());
            sb.Append("```");
            return sb.ToString();
        }

        return "[table]";
    }

    private static string RenderFigure(int blockIndex, VlmBlockResult? vlm, IReadOnlyDictionary<int, string>? figurePaths)
    {
        var path = figurePaths?.GetValueOrDefault(blockIndex);
        var description = vlm?.Text?.Trim();

        if (path != null)
            return $"![{description ?? "figure"}]({path})";

        if (description != null)
            return $"[figure: {description}]";

        return "[figure]";
    }

    private static string? RenderFigureTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return $"*{text.Trim()}*";
    }
}
