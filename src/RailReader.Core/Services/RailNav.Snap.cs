using System.Diagnostics;

namespace RailReader.Core.Services;

public sealed partial class RailNav
{
    // Below the rail zoom threshold the page is small enough that the whole thing (and the current
    // line) is visible, so a forced ("start rail here") activation should guide the eye line-by-line
    // WITHOUT moving the camera — centring/left-aligning the line here just shoves the page off to a
    // corner. Above the threshold, normal rail framing applies. The current snap (if any) is left
    // running; at low zoom none is ever started.
    private bool SnapSuppressed(double zoom) => zoom < _config.RailZoomThreshold;

    public void StartSnapToCurrent(double cameraX, double cameraY, double zoom, double windowWidth, double windowHeight)
    {
        if (!CanNavigate || SnapSuppressed(zoom)) return;
        var (targetX, targetY) = ComputeTargetCamera(zoom, windowWidth, windowHeight);
        BeginSnap(cameraX, cameraY, targetX, targetY);
    }

    /// <summary>
    /// Snap to the right (end) edge of the current line. Used when navigating backward
    /// via edge-hold so the user lands at the end of the previous line and can continue scrolling.
    /// </summary>
    public void StartSnapToCurrentEnd(double cameraX, double cameraY, double zoom, double windowWidth, double windowHeight)
    {
        if (!CanNavigate || SnapSuppressed(zoom)) return;

        var (blockLeft, blockRight, blockWidthPx) = GetChunkBounds(zoom);
        double targetX;
        if (ShouldCenterUnit(blockWidthPx, windowWidth))
            targetX = windowWidth / 2.0 - (blockLeft + blockRight) / 2.0 * zoom;
        else
            targetX = windowWidth - blockRight * zoom;
        var (_, targetY) = ComputeTargetCamera(zoom, windowWidth, windowHeight);

        BeginSnap(cameraX, cameraY, SnapX(targetX, zoom), SnapY(targetY));
    }

    /// <summary>
    /// Snap to the current cell: centres the active cell's <see cref="CellInfo.CenterX"/>
    /// horizontally on the current line, so cell stepping follows "label …… value" across
    /// the row at magnification. Falls back to <see cref="StartSnapToCurrent"/> when the
    /// current line has no cells (non-table line, or cell navigation disabled).
    /// </summary>
    public void StartSnapToCell(double cameraX, double cameraY, double zoom, double windowWidth, double windowHeight)
    {
        if (!CanNavigate) return;
        if (CurrentCellInfo is { } cell)
            StartSnapToPoint(cameraX, cameraY, zoom, windowWidth, windowHeight, cell.CenterX);
        else
            StartSnapToCurrent(cameraX, cameraY, zoom, windowWidth, windowHeight);
    }

    /// <summary>
    /// Snap to the current line, centering a specific page X coordinate horizontally.
    /// Used for search result navigation so the match is visible rather than
    /// snapping to the block's left edge.
    /// </summary>
    public void StartSnapToPoint(double cameraX, double cameraY, double zoom,
        double windowWidth, double windowHeight, double pageX)
    {
        if (!CanNavigate || SnapSuppressed(zoom)) return;

        var (_, targetY) = ComputeTargetCamera(zoom, windowWidth, windowHeight);
        double targetX = ClampX(windowWidth / 2.0 - pageX * zoom, zoom, windowWidth);

        BeginSnap(cameraX, cameraY, SnapX(targetX, zoom), SnapY(targetY));
    }

    /// <summary>
    /// Computes horizontal scroll fraction (0=line start, 1=line end) for the current
    /// camera position. Used to preserve reading position across zoom changes.
    /// </summary>
    public double ComputeHorizontalFraction(double cameraX, double zoom, double windowWidth)
    {
        if (!CanNavigate) return 0;
        var (blockLeft, blockRight, blockWidthPx) = GetChunkBounds(zoom);
        if (blockWidthPx <= windowWidth) return 0;

        double maxX = -blockLeft * zoom;
        double minX = windowWidth - blockRight * zoom;
        if (Math.Abs(maxX - minX) < 1) return 0;

        return Math.Clamp((maxX - cameraX) / (maxX - minX), 0, 1);
    }

    /// <summary>
    /// Snap to the current line, preserving horizontal fraction and vertical screen position.
    /// Used after zoom changes to maintain the user's reading position.
    /// </summary>
    public void StartSnapPreservingPosition(double cameraX, double cameraY, double zoom,
        double windowWidth, double windowHeight, double horizontalFraction, double lineScreenY)
    {
        if (!CanNavigate || SnapSuppressed(zoom)) return;

        var line = CurrentLineInfo;

        double targetY = lineScreenY - line.Y * zoom;
        double centeredY = windowHeight / 2.0 - line.Y * zoom;
        VerticalBias = targetY - centeredY;

        var (blockLeft, blockRight, blockWidthPx) = GetChunkBounds(zoom);
        double targetX;
        if (blockWidthPx <= windowWidth)
        {
            var (stdX, _) = ComputeTargetCamera(zoom, windowWidth, windowHeight);
            targetX = stdX;
        }
        else
        {
            double maxX = -blockLeft * zoom;
            double minX = windowWidth - blockRight * zoom;
            targetX = maxX - horizontalFraction * (maxX - minX);
        }

        targetX = ClampX(targetX, zoom, windowWidth);
        BeginSnap(cameraX, cameraY, SnapX(targetX, zoom), SnapY(targetY));
    }

    private void BeginSnap(double startX, double startY, double targetX, double targetY)
    {
        _snap = new SnapAnimation
        {
            StartX = startX, StartY = startY,
            TargetX = targetX, TargetY = targetY,
            Timer = Stopwatch.StartNew(),
            DurationMs = _config.SnapDurationMs,
        };
    }

    /// <summary>
    /// Snap X to a grid that guarantees at least 1 screen pixel of precision.
    /// At high zoom the grid is finer (smooth feel); at low zoom it coarsens
    /// to prevent sub-pixel text shimmer. Internal so the scroll/auto-scroll
    /// ticks (in sibling partials) can apply the same snapping every frame.
    /// </summary>
    internal double SnapX(double x, double zoom)
    {
        if (!_config.PixelSnapping) return x;
        double grid = Math.Max(4.0, zoom);
        return Math.Round(x * grid) / grid;
    }

    private double SnapY(double y) => _config.PixelSnapping ? Math.Round(y) : y;

    /// <summary>
    /// Public wrapper over <see cref="ComputeTargetCamera"/>: the camera offset that
    /// frames the currently-seated block/line at <paramref name="zoom"/> using the exact
    /// rail framing (centre narrow chunks, left-align wide ones with the 5% inset).
    /// Works regardless of <see cref="Active"/> as long as analysis is loaded, so a
    /// caller can compute the landing frame before zoom crosses the rail threshold.
    /// </summary>
    public (double X, double Y) ComputeSnapTarget(double zoom, double windowWidth, double windowHeight)
    {
        if (!HasAnalysis) return (0, 0); // caller guarantees HasAnalysis; defensive only
        return ComputeTargetCamera(zoom, windowWidth, windowHeight);
    }

    private (double X, double Y) ComputeTargetCamera(double zoom, double windowWidth, double windowHeight)
    {
        var line = CurrentLineInfo;
        double targetY = windowHeight / 2.0 - line.Y * zoom + VerticalBias;

        // Frame the whole chunk (column run) so crossing block boundaries within
        // a column doesn't shift the camera horizontally.
        var (chunkLeft, chunkRight, chunkWidthPx) = GetChunkBounds(zoom);
        double targetX;
        if (ShouldCenterUnit(chunkWidthPx, windowWidth))
        {
            double chunkCenterX = (chunkLeft + chunkRight) / 2.0;
            targetX = windowWidth / 2.0 - chunkCenterX * zoom;
        }
        else
        {
            targetX = windowWidth * 0.05 - chunkLeft * zoom;
        }

        // Keep the target inside the scrollable range for wide content. The 5%
        // line-start inset sits *outside* the range (its left boundary already
        // includes the content margin), so ClampX's soft-ease would treat it as
        // over-scroll and pull the camera left frame-by-frame after the snap —
        // the "overshoot left then snap right" on each line advance. Hard-clamp
        // into the range so the per-frame soft ClampX is a no-op here.
        if (chunkWidthPx > windowWidth)
        {
            double maxX = -chunkLeft * zoom;                  // left boundary
            double minX = windowWidth - chunkRight * zoom;    // right boundary
            targetX = Math.Clamp(targetX, minX, maxX);
        }

        if (_config.PixelSnapping)
        {
            targetY = Math.Round(targetY);
            targetX = SnapX(targetX, zoom);
        }

        return (targetX, targetY);
    }

    private bool TickSnapAnimation(ref double cameraX, ref double cameraY)
    {
        if (_snap is not { } snap)
            return false;

        double elapsed = snap.Timer.Elapsed.TotalMilliseconds;
        double t = Math.Min(elapsed / snap.DurationMs, 1.0);
        double eased = 1.0 - Math.Pow(1.0 - t, 3);

        cameraX = snap.StartX + (snap.TargetX - snap.StartX) * eased;
        cameraY = snap.StartY + (snap.TargetY - snap.StartY) * eased;

        if (t >= 1.0)
        {
            _snap = null;
            return false;
        }
        return true;
    }

    private sealed class SnapAnimation
    {
        public double StartX, StartY, TargetX, TargetY;
        public required Stopwatch Timer;
        public double DurationMs;
    }
}
