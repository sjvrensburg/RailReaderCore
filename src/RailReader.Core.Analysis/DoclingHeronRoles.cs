using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core.Analysis;

/// <summary>
/// Docling Heron's 17-class label list with each class's mapping to a
/// portable <see cref="BlockRole"/>. Source: <c>config.json</c> on
/// https://huggingface.co/docling-project/docling-layout-heron-onnx
/// (the ONNX export of <c>ds4sd/docling-layout-heron</c>, RT-DETRv2 architecture).
///
/// Heron is detection-only — it does <b>not</b> emit a per-detection
/// reading-order signal. The pipeline pairs it with
/// <see cref="XYCutPlusPlusResolver"/> by default.
/// </summary>
public static class DoclingHeronRoles
{
    public const int InputSize = 640;

    public static IReadOnlyList<LayoutClassDescriptor> Classes { get; } =
    [
        new( 0, "caption",             BlockRole.Caption),
        new( 1, "footnote",            BlockRole.Footnote),
        new( 2, "formula",             BlockRole.DisplayMath),
        new( 3, "list_item",           BlockRole.Text),
        new( 4, "page_footer",         BlockRole.Footer),
        new( 5, "page_header",         BlockRole.Header),
        new( 6, "picture",             BlockRole.Figure),
        new( 7, "section_header",      BlockRole.Heading),
        new( 8, "table",               BlockRole.Table),
        new( 9, "text",                BlockRole.Text),
        new(10, "title",               BlockRole.Title),
        new(11, "document_index",      BlockRole.Text),
        new(12, "code",                BlockRole.Algorithm),
        new(13, "checkbox_selected",   BlockRole.Decoration),
        new(14, "checkbox_unselected", BlockRole.Decoration),
        new(15, "form",                BlockRole.Decoration),
        new(16, "key_value_region",    BlockRole.Text),
    ];

    public static LayoutModelCapabilities Capabilities { get; } =
        new(InputSize, Classes, ProvidesReadingOrder: false);

    /// <summary>
    /// Looks up the role mapped to a Heron class name (e.g. <c>"text"</c> or
    /// <c>"section_header"</c>). Returns null if the name is unknown. Useful
    /// for any future config-migration shim that needs to translate
    /// Heron-name-based persisted settings into role-based settings.
    /// </summary>
    public static BlockRole? RoleForName(string name) => Capabilities.RoleForName(name);
}
