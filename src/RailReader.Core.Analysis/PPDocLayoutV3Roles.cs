using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core.Analysis;

/// <summary>
/// PP-DocLayoutV3's official 25-class label list with each class's mapping to
/// a portable <see cref="BlockRole"/>. Source:
/// https://huggingface.co/PaddlePaddle/PP-DocLayoutV3 inference.yml
///
/// Also exposed so config-migration code in <c>Core.Pdfium</c> can translate
/// old PP-DocLayout-name-based persisted settings into role-based settings.
/// </summary>
public static class PPDocLayoutV3Roles
{
    public const int InputSize = 800;

    public static IReadOnlyList<LayoutClassDescriptor> Classes { get; } =
    [
        new( 0, "abstract",          BlockRole.Text),
        new( 1, "algorithm",         BlockRole.Algorithm),
        new( 2, "aside_text",        BlockRole.Aside),
        new( 3, "chart",             BlockRole.Chart),
        new( 4, "content",           BlockRole.Text),
        new( 5, "display_formula",   BlockRole.DisplayMath),
        new( 6, "doc_title",         BlockRole.Title),
        new( 7, "figure_title",      BlockRole.Caption),
        new( 8, "footer",            BlockRole.Footer),
        new( 9, "footer_image",      BlockRole.Figure),
        new(10, "footnote",          BlockRole.Footnote),
        new(11, "formula_number",    BlockRole.Decoration),
        new(12, "header",            BlockRole.Header),
        new(13, "header_image",      BlockRole.Figure),
        new(14, "image",             BlockRole.Figure),
        new(15, "inline_formula",    BlockRole.InlineMath),
        new(16, "number",            BlockRole.PageNumber),
        new(17, "paragraph_title",   BlockRole.Heading),
        new(18, "reference",         BlockRole.Reference),
        new(19, "reference_content", BlockRole.Reference),
        new(20, "seal",              BlockRole.Decoration),
        new(21, "table",             BlockRole.Table),
        new(22, "text",              BlockRole.Text),
        new(23, "vertical_text",     BlockRole.Text),
        new(24, "vision_footnote",   BlockRole.Footnote),
    ];

    public static LayoutModelCapabilities Capabilities { get; } =
        new(InputSize, Classes, ProvidesReadingOrder: true);

    /// <summary>
    /// Looks up the role mapped to a PP-DocLayoutV3 class name, e.g. <c>"text"</c>
    /// or <c>"display_formula"</c>. Used by the <c>AppConfig</c> migration shim
    /// to upgrade configs persisted in the old name-based format.
    /// </summary>
    public static BlockRole? RoleForName(string name)
    {
        foreach (var c in Classes)
            if (c.Name == name) return c.Role;
        return null;
    }
}
