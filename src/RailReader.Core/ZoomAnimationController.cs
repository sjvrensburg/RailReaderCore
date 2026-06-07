using System.Diagnostics;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Manages smooth zoom animations (cubic ease-out) toward a focus point.
/// Extracted from DocumentController for testability and separation of concerns.
/// </summary>
internal sealed class ZoomAnimationController
{
    private sealed class ZoomAnimation
    {
        public double StartZoom, TargetZoom;
        public double StartOffsetX, StartOffsetY;
        public double TargetOffsetX, TargetOffsetY;
        public double CursorPageX, CursorPageY;
        public Stopwatch Timer = Stopwatch.StartNew();
        // Rail position preservation: captured when zoom starts in rail mode
        public double HorizontalFraction = -1; // 0=line start, 1=line end; <0 means not in rail
        public double LineScreenY;              // Y position of active line on screen
        // Pure camera move (geometric centred framing): drive the camera to an explicit target
        // WITHOUT any rail re-evaluation — no per-frame UpdateRailZoom (so rail can't re-engage
        // mid-flight) and no completion snap. Used to frame non-navigable blocks (figures/tables).
        public bool PureCameraMove;
    }

    private ZoomAnimation? _zoomAnim;

    /// <summary>Whether a zoom animation is currently in progress.</summary>
    public bool IsAnimating => _zoomAnim is not null;

    /// <summary>The target zoom level of the current animation, or null if not animating.</summary>
    public double? PendingTargetZoom => _zoomAnim?.TargetZoom;

    /// <summary>The target X offset of the current animation, or null if not animating.</summary>
    public double? PendingTargetOffsetX => _zoomAnim?.TargetOffsetX;

    /// <summary>The target Y offset of the current animation, or null if not animating.</summary>
    public double? PendingTargetOffsetY => _zoomAnim?.TargetOffsetY;

    /// <summary>Cancel any in-progress zoom animation.</summary>
    public void Cancel()
    {
        _zoomAnim = null;
    }

    /// <summary>
    /// Starts a smooth zoom animation toward <paramref name="focusX"/>,<paramref name="focusY"/>
    /// (screen coordinates). Accumulates from any in-progress animation.
    /// </summary>
    public void Start(DocumentState doc, double newZoom, double focusX, double focusY, double vpWidth)
    {
        double baseOx = _zoomAnim?.TargetOffsetX ?? doc.Camera.OffsetX;
        double baseOy = _zoomAnim?.TargetOffsetY ?? doc.Camera.OffsetY;
        double baseZoom = _zoomAnim?.TargetZoom ?? doc.Camera.Zoom;

        double targetOx = focusX - (focusX - baseOx) * (newZoom / baseZoom);
        double targetOy = focusY - (focusY - baseOy) * (newZoom / baseZoom);

        // Capture rail reading position before zoom so we can restore it on completion
        double hFraction = -1;
        double lineScreenY = 0;
        if (doc.Rail.Active && doc.Rail.HasAnalysis)
        {
            hFraction = doc.Rail.ComputeHorizontalFraction(doc.Camera.OffsetX, doc.Camera.Zoom, vpWidth);
            lineScreenY = doc.Rail.CurrentLineInfo.Y * doc.Camera.Zoom + doc.Camera.OffsetY;
        }

        _zoomAnim = new ZoomAnimation
        {
            StartZoom = doc.Camera.Zoom,
            TargetZoom = newZoom,
            StartOffsetX = doc.Camera.OffsetX,
            StartOffsetY = doc.Camera.OffsetY,
            TargetOffsetX = targetOx,
            TargetOffsetY = targetOy,
            CursorPageX = (focusX - targetOx) / newZoom,
            CursorPageY = (focusY - targetOy) / newZoom,
            HorizontalFraction = hFraction,
            LineScreenY = lineScreenY,
        };
    }

    /// <summary>
    /// Starts a smooth zoom animation to an EXPLICIT camera target (zoom + offsets),
    /// rather than zooming around a focus point. Identical cubic ease-out curve and
    /// duration (<see cref="CoreTuning.ZoomAnimationDurationMs"/>) as <see cref="Start"/>;
    /// only the target is supplied directly. <paramref name="cursorPageX"/>/<paramref
    /// name="cursorPageY"/> is the page-space point used for per-frame rail
    /// re-evaluation (<c>UpdateRailZoom</c>) and — when zoom crosses the rail threshold
    /// mid-animation — the point <c>FindBlockNearPoint</c> uses to pick the active
    /// block. Pass the target block's line centre so activation lands on it.
    /// </summary>
    public void StartTo(DocumentState doc,
        double targetZoom, double targetOffsetX, double targetOffsetY,
        double cursorPageX, double cursorPageY)
    {
        _zoomAnim = new ZoomAnimation
        {
            StartZoom = doc.Camera.Zoom,
            TargetZoom = targetZoom,
            StartOffsetX = doc.Camera.OffsetX,
            StartOffsetY = doc.Camera.OffsetY,
            TargetOffsetX = targetOffsetX,
            TargetOffsetY = targetOffsetY,
            CursorPageX = cursorPageX,
            CursorPageY = cursorPageY,
            HorizontalFraction = -1, // not preserving a prior rail position → completion takes the StartSnap branch
            LineScreenY = 0,
        };
    }

    /// <summary>
    /// Starts a smooth, EXPLICIT camera move (zoom + offsets) that bypasses rail entirely:
    /// no per-frame rail re-evaluation and no completion snap — the camera simply eases to the
    /// target. Same cubic ease-out and duration as <see cref="StartTo"/>. Used for geometric
    /// centred framing of non-navigable blocks (figures/tables/charts) that the rail index can't
    /// seat. The caller is responsible for deactivating rail first.
    /// </summary>
    public void StartCameraOnly(DocumentState doc,
        double targetZoom, double targetOffsetX, double targetOffsetY)
    {
        _zoomAnim = new ZoomAnimation
        {
            StartZoom = doc.Camera.Zoom,
            TargetZoom = targetZoom,
            StartOffsetX = doc.Camera.OffsetX,
            StartOffsetY = doc.Camera.OffsetY,
            TargetOffsetX = targetOffsetX,
            TargetOffsetY = targetOffsetY,
            CursorPageX = 0,
            CursorPageY = 0,
            HorizontalFraction = -1,
            LineScreenY = 0,
            PureCameraMove = true,
        };
    }

    /// <summary>Smooth zoom animation step.</summary>
    public void Tick(DocumentState doc, double ww, double wh,
        ref bool cameraChanged, ref bool animating)
    {
        if (_zoomAnim is { } za)
        {
            double elapsed = za.Timer.Elapsed.TotalMilliseconds;
            double t = Math.Clamp(elapsed / CoreTuning.ZoomAnimationDurationMs, 0, 1);
            // Cubic ease-out: 1 - (1-t)^3
            double ease = 1.0 - (1.0 - t) * (1.0 - t) * (1.0 - t);

            double prevZoom = doc.Camera.Zoom;
            doc.Camera.Zoom = za.StartZoom + (za.TargetZoom - za.StartZoom) * ease;
            doc.Camera.OffsetX = za.StartOffsetX + (za.TargetOffsetX - za.StartOffsetX) * ease;
            doc.Camera.OffsetY = za.StartOffsetY + (za.TargetOffsetY - za.StartOffsetY) * ease;
            doc.Camera.NotifyZoomChange();
            // Pure camera moves drive the camera directly — no rail bias scaling and no per-frame
            // rail re-evaluation, so rail can't re-engage and hijack a figure/table frame.
            if (!za.PureCameraMove)
            {
                doc.Rail.ScaleVerticalBias(prevZoom, doc.Camera.Zoom);
                doc.UpdateRailZoom(ww, wh, za.CursorPageX, za.CursorPageY);
            }
            cameraChanged = true;

            if (t >= 1.0)
            {
                double hFrac = za.HorizontalFraction;
                double lineY = za.LineScreenY;
                bool pure = za.PureCameraMove;
                _zoomAnim = null;
                doc.ClampCamera(ww, wh);
                if (!pure && doc.Rail.Active)
                {
                    if (hFrac >= 0)
                        doc.StartSnapPreservingPosition(ww, wh, hFrac, lineY);
                    else
                        doc.StartSnap(ww, wh);
                }
                doc.UpdateRenderDpiIfNeeded();
            }
            else
            {
                animating = true;
            }
        }
    }
}
