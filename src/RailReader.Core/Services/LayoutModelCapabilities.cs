using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Self-description of a layout-detection model. Each
/// <see cref="ILayoutAnalyzer"/> exposes one of these so Core (and consumers
/// such as the debug overlay) can interpret detections without hard-coding
/// the model's class table.
/// </summary>
/// <param name="InputSize">
/// Pixel dimension Core should rasterise the page to before calling
/// <see cref="ILayoutAnalyzer.RunAnalysis"/>. The longest page edge is
/// scaled to this; PP-DocLayoutV3 uses 800, DocLayout-YOLO uses 1024, etc.
/// </param>
/// <param name="Classes">
/// The model's raw class table — one entry per native class id, with the
/// model's own label and the <see cref="BlockRole"/> it maps onto. Used by
/// the overlay renderer to label boxes and by Core to populate
/// <see cref="LayoutBlock.Role"/>.
/// </param>
/// <param name="ProvidesReadingOrder">
/// True if the model emits a reading-order signal on each detection (e.g.
/// PP-DocLayoutV3's 7th tensor column). The analysis pipeline uses this to
/// pick a default <see cref="IReadingOrderResolver"/> when none is supplied.
/// </param>
public sealed record LayoutModelCapabilities(
    int InputSize,
    IReadOnlyList<LayoutClassDescriptor> Classes,
    bool ProvidesReadingOrder)
{
    /// <summary>
    /// Looks up the role mapped to a class name in <see cref="Classes"/>.
    /// Returns null if the name is unknown. Used by config-migration shims
    /// that need to translate name-based persisted settings into role-based
    /// settings.
    /// </summary>
    public BlockRole? RoleForName(string name)
    {
        foreach (var c in Classes)
            if (c.Name == name) return c.Role;
        return null;
    }
}

/// <summary>One entry in a layout model's class table.</summary>
public sealed record LayoutClassDescriptor(int Id, string Name, BlockRole Role);
