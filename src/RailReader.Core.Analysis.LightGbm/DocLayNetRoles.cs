using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core.Analysis.LightGbm;

/// <summary>
/// Mapping from the 11-class DocLayNet label set (used by huridocs's
/// token-type LightGBM model) to Core's portable <see cref="BlockRole"/>.
///
/// <para>
/// Class IDs match the order in
/// <c>huridocs/pdf-features/pdf_features/PdfTokenContext.py</c> →
/// <c>TokenType</c>: Caption=0, Footnote=1, Formula=2, ListItem=3,
/// PageFooter=4, PageHeader=5, Picture=6, SectionHeader=7, Table=8,
/// Text=9, Title=10. This is the DocLayNet "lightweight" label space —
/// coarser than PP-DocLayoutV3's 25 or PP-DocLayout-S's 23. Per-source
/// distinctions like <c>figure_caption</c> vs <c>table_caption</c> are
/// collapsed into a single <see cref="BlockRole.Caption"/>.
/// </para>
/// </summary>
public static class DocLayNetRoles
{
    /// <summary>
    /// Rasterisation hint is meaningless for a text-only analyzer (we
    /// never see pixels). Set to zero by convention; downstream code
    /// that branches on InputSize should treat zero as "skip rasterisation".
    /// </summary>
    public const int InputSize = 0;

    public static IReadOnlyList<LayoutClassDescriptor> Classes { get; } =
    [
        new( 0, "caption",        BlockRole.Caption),
        new( 1, "footnote",       BlockRole.Footnote),
        new( 2, "formula",        BlockRole.DisplayMath),
        new( 3, "list_item",      BlockRole.Text),
        new( 4, "page_footer",    BlockRole.Footer),
        new( 5, "page_header",    BlockRole.Header),
        new( 6, "picture",        BlockRole.Figure),
        new( 7, "section_header", BlockRole.Heading),
        new( 8, "table",          BlockRole.Table),
        new( 9, "text",           BlockRole.Text),
        new(10, "title",          BlockRole.Title),
    ];

    public static LayoutModelCapabilities Capabilities { get; } =
        new(InputSize, Classes, ProvidesReadingOrder: false);

    /// <summary>
    /// Looks up the role mapped to a DocLayNet class name (e.g.
    /// <c>"section_header"</c> → <see cref="BlockRole.Heading"/>).
    /// </summary>
    public static BlockRole? RoleForName(string name) => Capabilities.RoleForName(name);
}
