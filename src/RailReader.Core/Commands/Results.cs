using RailReader.Core.Models;

namespace RailReader.Core.Commands;

/// <summary>
/// Summary of a single open document.
/// </summary>
public sealed record DocumentInfo(
    string FilePath,
    string Title,
    int PageCount,
    int CurrentPage,
    double Zoom,
    double OffsetX,
    double OffsetY,
    bool RailActive,
    bool HasAnalysis,
    int NavigableBlocks,
    bool AutoScrollActive,
    bool JumpMode);

/// <summary>
/// List of all open documents with active index.
/// </summary>
public sealed record DocumentList(
    int ActiveIndex,
    List<DocumentSummary> Documents);

public sealed record DocumentSummary(
    int Index,
    string FilePath,
    string Title,
    int PageCount,
    int CurrentPage);

/// <summary>
/// Result of a search operation.
/// </summary>
public sealed record SearchResult(
    int TotalMatches,
    int ActiveIndex,
    Dictionary<int, int> MatchesPerPage);

/// <summary>
/// Options for headless screenshot export.
/// </summary>
public sealed record ScreenshotOptions
{
    public int Dpi { get; init; } = 300;
    public bool RailOverlay { get; init; } = true;
    public bool Annotations { get; init; } = true;
    public bool SearchHighlights { get; init; } = true;
    public bool DebugOverlay { get; init; } = false;
    public bool LineFocusBlur { get; init; } = false;
    public float LineFocusBlurIntensity { get; init; } = 0.5f;
    public bool LineHighlightEnabled { get; init; } = true;
    public double LinePadding { get; init; } = 0.2;
    public LineHighlightTint LineHighlightTint { get; init; } = LineHighlightTint.Auto;
    public double LineHighlightOpacity { get; init; } = 0.25;

    /// <summary>
    /// When true, crop the output to simulate what's visible in the viewport
    /// at the document's current camera position and zoom level.
    /// The output dimensions match ViewportWidth x ViewportHeight.
    /// </summary>
    public bool SimulateViewport { get; init; } = false;
    public int ViewportWidth { get; init; } = 1200;
    public int ViewportHeight { get; init; } = 900;
}

/// <summary>
/// Current reading position in a document for agent/screen-reader consumption.
/// </summary>
/// <param name="LineCount">Number of lines in the current block (so callers can show "line 3 of 7"
/// and detect end-of-block).</param>
/// <param name="HorizontalFraction">How far the camera is across the current line's scrollable
/// width, 0 (start) to 1 (end); 0 when the column fits and nothing scrolls horizontally.</param>
public sealed record ReadingPosition(
    int Page,
    int BlockIndex,
    int LineIndex,
    BlockRole Role,
    string BlockText,
    string LineText,
    BBox BlockBBox,
    int LineCount,
    double HorizontalFraction);

/// <summary>
/// Summary of a single layout block for accessible page descriptions.
/// </summary>
public sealed record BlockSummary(
    int Index,
    BlockRole Role,
    string TextPreview,
    BBox BBox,
    int ReadingOrder);

/// <summary>
/// Accessible description of a page's layout and content for screen readers
/// and AI agents.
/// </summary>
public sealed record PageDescription(
    int Page,
    int TotalBlocks,
    IReadOnlyList<BlockSummary> Blocks);

