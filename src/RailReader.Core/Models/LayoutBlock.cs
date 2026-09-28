namespace RailReader.Core.Models;

public sealed class LayoutBlock
{
    public BBox BBox { get; set; }

    /// <summary>
    /// The block's outline as the detector segmented it — a minimum-area
    /// rectangle that may be rotated, for pages photographed or scanned at an
    /// angle. Null when the analyzer has no segmentation output (only
    /// PP-DocLayoutV3 provides one; PP-DocLayout-S, Heron and
    /// <see cref="Services.TextLayoutAnalyzer"/> leave it unset). When the
    /// segmentation is axis-aligned to within its own resolution the quad is
    /// exactly <see cref="BBox"/>'s corners. Rail navigation and line
    /// detection use <see cref="BBox"/> only; post-processing may trim
    /// <see cref="BBox"/> (overlap resolution) but leaves the quad as detected.
    /// </summary>
    public BlockQuad? Quad { get; set; }
    public BlockRole Role { get; set; } = BlockRole.Unknown;
    public int ClassId { get; set; }
    public float Confidence { get; set; }
    public int Order { get; set; }
    public List<LineInfo> Lines { get; set; } = [];

    /// <summary>
    /// Clockwise quarter-turns (0–3) of view rotation that would make this
    /// block's text read upright — 0 for ordinary blocks, 1 for the common
    /// academic sideways table (rotated 90° counter-clockwise to fit a portrait
    /// page). Set by <see cref="Services.OrientationDetector"/> during analysis
    /// post-processing (majority glyph angle, with a pixel-projection fallback
    /// for scanned pages) or seeded by a layout model's vertical-text class.
    /// Sideways blocks (odd or 2 turns) collapse to a single atomic rail line —
    /// horizontal line detection would shatter them — and hosts can offer a
    /// rotate-to-read affordance (<see cref="DocumentController.RotateViewToReadBlock"/>).
    /// </summary>
    public int UprightTurns { get; set; }

    /// <summary>True when the block's text is not upright as displayed.</summary>
    public bool IsRotatedText => UprightTurns != 0;
}
