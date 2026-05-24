namespace RailReader.Core.Models;

/// <summary>
/// Semantic role of a layout block. Core branches on this rather than the
/// model-specific integer class id, so any layout-detection model can drive
/// RailReader as long as its <see cref="Services.ILayoutAnalyzer"/>
/// implementation maps its native classes onto these roles.
/// </summary>
public enum BlockRole
{
    Unknown,

    // Body
    Text,
    Heading,
    Title,
    Caption,
    Aside,

    // Math
    DisplayMath,
    InlineMath,
    Algorithm,

    // Visual
    Table,
    Figure,
    Chart,

    // Margins / structure
    Header,
    Footer,
    PageNumber,
    Footnote,
    Reference,

    // Decorative / drop
    Decoration,
}
