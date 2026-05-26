using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core.Analysis;

/// <summary>
/// PP-DocLayout-S's official 23-class label list with each class's mapping to a
/// portable <see cref="BlockRole"/>. Source:
/// https://huggingface.co/PaddlePaddle/PP-DocLayout-S inference.yml
///
/// <para>
/// PP-DocLayout-S is a lightweight PicoDet/GFL detector (~4.7 MB ONNX, 480×480
/// model input) — the same family as PP-DocLayoutV3 but ~10× smaller and a
/// different label space (23 vs 25 classes; no <c>inline_formula</c>, no
/// <c>vertical_text</c>, no <c>reference_content</c>, no <c>vision_footnote</c>;
/// <c>chart_title</c> and <c>table_title</c> are first-class). It is the
/// detector intended for future web (WASM/ORT-Web) and mobile consumers where
/// the 50 MB V3 model is too heavy.
/// </para>
///
/// <para>
/// Like Heron, PP-DocLayout-S is detection-only — it does <b>not</b> emit a
/// per-detection reading-order signal (the GFL head has no order branch). The
/// pipeline pairs it with <see cref="XYCutPlusPlusResolver"/> by default.
/// </para>
///
/// <para>
/// <see cref="LayoutModelCapabilities.InputSize"/> is the rasterisation hint to
/// the caller (1920 px on the longest edge) — deliberately decoupled from the
/// model's 480×480 input. Going straight to 480 loses bibliography rows and
/// small text on academic content; <see cref="PPDocLayoutSLayoutAnalyzer"/>
/// internally bilinearly downsizes the 1920-edge pixmap to 480×480 before
/// inference, which preserves recall without growing the ONNX file.
/// </para>
/// </summary>
public static class PPDocLayoutSRoles
{
    /// <summary>
    /// Rasterisation hint for the consumer (longest-edge pixel target). The
    /// model itself runs at 480×480; the analyzer downsizes internally.
    /// </summary>
    public const int InputSize = 1920;

    /// <summary>The model's actual spatial input dimension (480×480).</summary>
    public const int ModelInputSize = 480;

    public static IReadOnlyList<LayoutClassDescriptor> Classes { get; } =
    [
        new( 0, "paragraph_title", BlockRole.Heading),
        new( 1, "image",           BlockRole.Figure),
        new( 2, "text",            BlockRole.Text),
        new( 3, "number",          BlockRole.PageNumber),
        new( 4, "abstract",        BlockRole.Text),
        new( 5, "content",         BlockRole.Text),
        new( 6, "figure_title",    BlockRole.Caption),
        new( 7, "formula",         BlockRole.DisplayMath),
        new( 8, "table",           BlockRole.Table),
        new( 9, "table_title",     BlockRole.Caption),
        new(10, "reference",       BlockRole.Reference),
        new(11, "doc_title",       BlockRole.Title),
        new(12, "footnote",        BlockRole.Footnote),
        new(13, "header",          BlockRole.Header),
        new(14, "algorithm",       BlockRole.Algorithm),
        new(15, "footer",          BlockRole.Footer),
        new(16, "seal",            BlockRole.Decoration),
        new(17, "chart_title",     BlockRole.Caption),
        new(18, "chart",           BlockRole.Chart),
        new(19, "formula_number",  BlockRole.Decoration),
        new(20, "header_image",    BlockRole.Figure),
        new(21, "footer_image",    BlockRole.Figure),
        new(22, "aside_text",      BlockRole.Aside),
    ];

    public static LayoutModelCapabilities Capabilities { get; } =
        new(InputSize, Classes, ProvidesReadingOrder: false);

    /// <summary>
    /// Looks up the role mapped to a PP-DocLayout-S class name, e.g. <c>"text"</c>
    /// or <c>"paragraph_title"</c>. Useful for config-migration shims that need
    /// to translate name-based persisted settings into role-based settings.
    /// </summary>
    public static BlockRole? RoleForName(string name)
    {
        foreach (var c in Classes)
            if (c.Name == name) return c.Role;
        return null;
    }
}
