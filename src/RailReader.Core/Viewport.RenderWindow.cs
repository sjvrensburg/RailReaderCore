using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// One rasterised entry in the neighbours of a continuous-scroll render window (the
/// anchor page's bitmap stays in <see cref="Viewport.CachedPage"/>/<see cref="Viewport.CachedDpi"/>
/// as it always has — see docs/continuous-scroll-plan.md §5 Phase 1).
/// </summary>
public readonly record struct VisiblePage(
    int Page, double OffsetX, double OffsetY, double Width, double Height,
    IRenderedPage? Bitmap, int Dpi);

partial class Viewport
{
    private sealed class WindowEntry : IDisposable
    {
        public required IRenderedPage Bitmap { get; init; }
        public required int Dpi { get; init; }
        public required double Width { get; init; }
        public required double Height { get; init; }
        public void Dispose() => Bitmap.Dispose();
    }

    private readonly Dictionary<int, WindowEntry> _renderWindow = new();

    /// <summary>
    /// Pages currently being rasterised on a background thread for the window (scheduled by
    /// <see cref="EnsureRenderWindow"/>, resolved by the completion continuation below). A page in
    /// here has no bitmap yet — <see cref="VisiblePages"/> reports it with <c>Bitmap: null</c> so a
    /// host can still reserve its slot (draw a placeholder / the page gap colour) instead of the
    /// frame silently omitting it.
    /// </summary>
    private readonly HashSet<int> _pendingRenders = new();

    /// <summary>
    /// Guards <see cref="_renderWindow"/> and <see cref="_pendingRenders"/> ONLY — never held
    /// across a PDFium call (the render itself runs lock-free in the background task; only the
    /// bookkeeping around it is synchronised). This is NOT a substitute for the "mutate Viewport
    /// only on the UI thread" rule: in production the host's <see cref="IThreadMarshaller"/> always
    /// serialises the completion callback onto the UI thread, so this lock is never contended
    /// there. It exists because <see cref="SynchronousThreadMarshaller"/> (headless/tests) runs the
    /// completion inline on whichever background thread finished rendering, which can genuinely
    /// race a test's own thread reading/writing these two collections — without this, that race is
    /// a real "Collection was modified" hazard, not just a stale-read.
    /// </summary>
    private readonly object _windowLock = new();

    /// <summary>True while at least one neighbour page is still rasterising in the background.</summary>
    internal bool HasPendingWindowRenders { get { lock (_windowLock) return _pendingRenders.Count > 0; } }

    /// <summary>
    /// True when this view is running in continuous-scroll mode (derived from
    /// <see cref="CoreSettings.ContinuousScroll"/> on the document's current settings snapshot).
    /// A confined (<see cref="Focus"/>ed) view ignores the mode entirely — it stays single-page.
    /// </summary>
    public bool ContinuousScroll => Owner.Config.ContinuousScroll && CurrentFocusBlockIndex is null;

    /// <summary>
    /// Every page this view currently has (or is about to have) a bitmap for, in ascending page
    /// order, with each page's transform in the anchor camera frame (see <see cref="PageOffset"/>).
    /// A page whose render is still in flight (<see cref="HasPendingWindowRenders"/>) appears here
    /// too, with <c>Bitmap: null</c> — its geometry comes straight from <see cref="DocumentModel.PageLayout"/>
    /// and does not depend on the render having completed. In single-page mode (or while confined)
    /// this always has exactly one entry describing the anchor page — a host that draws every entry
    /// in <see cref="VisiblePages"/> behaves identically to drawing the single legacy page fields.
    /// </summary>
    public IReadOnlyList<VisiblePage> VisiblePages
    {
        get
        {
            if (!ContinuousScroll || Owner.PageLayout is not { } layout)
            {
                return [new VisiblePage(CurrentPageBacking, Camera.OffsetX, Camera.OffsetY,
                    PageWidthBacking, PageHeightBacking, CachedPage, CachedDpi)];
            }

            List<VisiblePage> list;
            lock (_windowLock)
            {
                list = new List<VisiblePage>(_renderWindow.Count + _pendingRenders.Count + 1)
                {
                    new VisiblePage(CurrentPageBacking, Camera.OffsetX, Camera.OffsetY,
                        PageWidthBacking, PageHeightBacking, CachedPage, CachedDpi)
                };
                foreach (var (page, entry) in _renderWindow)
                {
                    var (ox, oy) = PageOffset(page);
                    list.Add(new VisiblePage(page, ox, oy, entry.Width, entry.Height, entry.Bitmap, entry.Dpi));
                }
                foreach (var page in _pendingRenders)
                {
                    if (_renderWindow.ContainsKey(page)) continue; // a stale-DPI re-render is in flight; the OLD bitmap above still serves
                    var (ox, oy) = PageOffset(page);
                    list.Add(new VisiblePage(page, ox, oy, layout.Width(page), layout.Height(page), null, 0));
                }
            }
            list.Sort((a, b) => a.Page.CompareTo(b.Page));
            return list;
        }
    }

    /// <summary>
    /// The transform (offset) of <paramref name="page"/> in the anchor page's camera frame — the
    /// page-anchored camera representation from docs/continuous-scroll-plan.md §1.2. Identical to
    /// <see cref="Camera"/>'s own offset when <paramref name="page"/> is the anchor
    /// (<see cref="CurrentPage"/>). Falls back to the camera's own offset (as if there were one page)
    /// when no <see cref="DocumentModel.PageLayout"/> has been built yet.
    /// </summary>
    public (double OffsetX, double OffsetY) PageOffset(int page)
    {
        if (Owner.PageLayout is not { } layout) return (Camera.OffsetX, Camera.OffsetY);
        double z = Camera.Zoom;
        int a = CurrentPageBacking;
        double ox = Camera.OffsetX + (layout.Left(page) - layout.Left(a)) * z;
        double oy = Camera.OffsetY + (layout.Top(page) - layout.Top(a)) * z;
        return (ox, oy);
    }

    /// <summary>Document-space X offset (the anchor-frame camera renamed to page 0's frame).</summary>
    public double DocumentOffsetX =>
        Owner.PageLayout is { } l ? Camera.OffsetX - l.Left(CurrentPageBacking) * Camera.Zoom : Camera.OffsetX;

    /// <summary>Document-space Y offset (the anchor-frame camera renamed to page 0's frame).</summary>
    public double DocumentOffsetY =>
        Owner.PageLayout is { } l ? Camera.OffsetY - l.Top(CurrentPageBacking) * Camera.Zoom : Camera.OffsetY;

    /// <summary>The page whose vertical extent contains the viewport's centre — the page an
    /// automatic re-anchor (Phase 2) would adopt. Returns <see cref="CurrentPage"/> outside
    /// continuous mode / before the layout exists.</summary>
    public int ComputeAnchorPage(double windowWidth, double windowHeight)
    {
        if (Owner.PageLayout is not { } layout || Camera.Zoom <= 0) return CurrentPageBacking;
        double centreDocY = (windowHeight / 2.0 - DocumentOffsetY) / Camera.Zoom;
        return layout.PageAtY(centreDocY);
    }

    /// <summary>
    /// Resolves a screen point to (page, page-local X, page-local Y). In single-page mode this is
    /// exactly the legacy formula against the anchor page. In continuous mode the point may land on
    /// a neighbouring page.
    /// </summary>
    public (int Page, double PageX, double PageY) ResolvePoint(double canvasX, double canvasY)
    {
        if (!ContinuousScroll || Owner.PageLayout is not { } layout)
        {
            return (CurrentPageBacking, (canvasX - Camera.OffsetX) / Camera.Zoom, (canvasY - Camera.OffsetY) / Camera.Zoom);
        }

        double docX = (canvasX - DocumentOffsetX) / Camera.Zoom;
        double docY = (canvasY - DocumentOffsetY) / Camera.Zoom;
        int page = layout.PageAtY(docY);
        return (page, docX - layout.Left(page), docY - layout.Top(page));
    }

    /// <summary>
    /// Ensures the render window covers every page currently wanted (pages intersecting the
    /// viewport plus one page of padding on each side, capped at
    /// <see cref="CoreSettings.ContinuousRenderWindowPages"/>), scheduling a background render for
    /// each missing/stale page and evicting pages no longer wanted. No-op outside continuous mode.
    /// The anchor page's own bitmap is never held here — it lives in <see cref="CachedPage"/> as
    /// always.
    /// <para>
    /// Rendering runs off the UI thread (mirrors <see cref="PrefetchPage"/>/<see cref="UpdateRenderDpiIfNeeded"/>):
    /// this method only decides WHAT is wanted and hands each missing/stale page to a
    /// <see cref="Task.Run(Action)"/> that rasterises it and posts the result back via
    /// <see cref="IThreadMarshaller"/>. A page already in flight is not re-scheduled
    /// (<see cref="_pendingRenders"/>); a page whose OLD bitmap is merely stale (DPI hysteresis)
    /// keeps serving that bitmap from <see cref="_renderWindow"/> until the fresh one lands — it is
    /// never blanked out while re-rendering.
    /// </para>
    /// </summary>
    internal void EnsureRenderWindow(double windowWidth, double windowHeight)
    {
        if (!ContinuousScroll) return;
        Owner.EnsurePageLayout();
        if (Owner.PageLayout is not { } layout || windowWidth <= 0 || windowHeight <= 0) return;

        double docY0 = -DocumentOffsetY / Camera.Zoom;
        double docY1 = (windowHeight - DocumentOffsetY) / Camera.Zoom;
        var (first, last) = layout.PagesIntersecting(docY0, docY1);
        first = Math.Max(0, first - 1);
        last = Math.Min(layout.Count - 1, last + 1);

        var wanted = new List<int>();
        for (int p = first; p <= last; p++)
            if (p != CurrentPageBacking) wanted.Add(p);

        // Nearest-page-first, with ties (a page equidistant before and after the anchor) broken in
        // favour of the page AHEAD: losing the page just left behind costs nothing (it won't be
        // revisited without an explicit re-scroll), but losing the page about to become the anchor
        // reintroduces the synchronous render-on-cache-miss the window exists to avoid.
        wanted = wanted.OrderBy(p => Math.Abs(p - CurrentPageBacking)).ThenBy(p => p <= CurrentPageBacking).ToList();

        int cap = Math.Max(0, Owner.Config.ContinuousRenderWindowPages - 1); // -1: anchor doesn't count against the cap
        if (wanted.Count > cap)
            wanted = wanted.Take(cap).ToList();

        // Aggregate megapixel budget (plan §5 Phase 1): each entry is already individually capped by
        // CalculateRenderDpi, but nothing previously bounded the TOTAL — at rail zoom a full window of
        // ContinuousRenderWindowPages pages could allocate unboundedly. Cap the sum of w·h·dpi²/72²
        // (pixel count) over window entries at RenderDpi.MaxMegapixels × 2; since `wanted` is already
        // nearest-first, trimming the tail here is exactly "drop the farthest first".
        if (RenderDpi.MaxMegapixels > 0 && wanted.Count > 0)
        {
            double budgetPixels = RenderDpi.MaxMegapixels * 2.0 * 1_000_000.0;
            double usedPixels = 0;
            var withinBudget = new List<int>(wanted.Count);
            foreach (var p in wanted)
            {
                double pw = layout.Width(p), ph = layout.Height(p);
                int pDpi = DocumentModel.CalculateRenderDpi(Camera.Zoom, pw, ph, RenderDpi);
                double pixels = pw * ph * pDpi * pDpi / (72.0 * 72.0);
                if (withinBudget.Count > 0 && usedPixels + pixels > budgetPixels) break;
                usedPixels += pixels;
                withinBudget.Add(p);
            }
            wanted = withinBudget;
        }
        var wantedSet = new HashSet<int>(wanted);
        int viewRotation = Owner.ViewRotation;
        int generation = RenderGeneration;
        var pagesToSchedule = new List<(int Page, double Width, double Height, int Dpi)>();

        lock (_windowLock)
        {
            foreach (var key in _renderWindow.Keys.Where(k => !wantedSet.Contains(k)).ToList())
            {
                _renderWindow[key].Dispose();
                _renderWindow.Remove(key);
            }
            // Stop tracking a page no longer wanted as "in flight": its eventual completion is
            // still safely ignored by the generation/CurrentPage/ContinuousScroll checks below, but
            // it must not block a future re-request for that same page from being scheduled.
            _pendingRenders.RemoveWhere(p => !wantedSet.Contains(p));

            foreach (var page in wanted)
            {
                // Read the size from the already-built layout rather than Owner.Pdf.GetPageSize:
                // for SkiaPdfService the latter re-parses the whole document from PdfBytes on every
                // call (see SkiaPdfService.GetPageSize's own doc comment), and this loop runs on
                // essentially every frame while scrolling — the exact per-call parse cost
                // GetPageSizes/PageLayout exists to eliminate. layout.Width/Height(page) is the same
                // value (both are sourced from GetPageSizes(_viewRotation) — see EnsurePageLayout)
                // for free.
                double w = layout.Width(page), h = layout.Height(page);
                int dpi = DocumentModel.CalculateRenderDpi(Camera.Zoom, w, h, RenderDpi);

                if (_renderWindow.TryGetValue(page, out var existing))
                {
                    bool stale = existing.Dpi <= 0
                        || dpi > existing.Dpi * RenderDpi.UpscaleHysteresis
                        || (dpi < existing.Dpi * RenderDpi.DownscaleHysteresis && existing.Dpi > RenderDpi.MinDpi);
                    if (!stale) continue;
                    // Stale, not missing: keep serving `existing` (still in _renderWindow) until the
                    // fresh render below lands — do NOT remove/dispose it here, or the page would go
                    // blank for however many frames the background render takes.
                }

                if (!_pendingRenders.Add(page)) continue; // already have an in-flight render targeting this page
                pagesToSchedule.Add((page, w, h, dpi));
            }
        }

        foreach (var (page, w, h, dpi) in pagesToSchedule)
        {
            var ct = Cts.Token;
            Task.Run(() =>
            {
                IRenderedPage? bitmap = null;
                Exception? error = null;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    bitmap = Owner.Pdf.RenderPage(page, dpi, viewRotation);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex; }

                Owner.Marshaller.Post(() =>
                {
                    try
                    {
                        if (error is not null)
                            Owner.Logger.Error($"Failed to render window page {page + 1}: {error.Message}", error);

                        // Bail without installing the bitmap when: the document/view is gone; a
                        // rotation/quality change moved on to a new render generation (this bitmap
                        // is in the old, now-wrong, orientation/DPI band); continuous mode was
                        // turned off while this was in flight; or `page` became the anchor in the
                        // meantime (its bitmap now belongs in CachedPage, not the window — installing
                        // it here too would list it twice in VisiblePages until the next eviction
                        // pass catches up).
                        if (Owner.IsDisposed || _disposed || RenderGeneration != generation
                            || !ContinuousScroll || page == CurrentPageBacking || bitmap is null)
                        {
                            bitmap?.Dispose();
                            return;
                        }

                        lock (_windowLock)
                        {
                            if (_renderWindow.TryGetValue(page, out var staleEntry))
                            {
                                staleEntry.Dispose();
                                _renderWindow.Remove(page);
                            }
                            _renderWindow[page] = new WindowEntry { Bitmap = bitmap, Dpi = dpi, Width = w, Height = h };
                        }
                    }
                    finally
                    {
                        lock (_windowLock) _pendingRenders.Remove(page);
                    }
                });
            }, ct);
        }
    }

    /// <summary>Disposes every render-window entry (not the anchor's <see cref="CachedPage"/>) and
    /// stops tracking in-flight renders (their eventual completion is still safely ignored — see
    /// the generation/disposal guard in <see cref="EnsureRenderWindow"/> — this just lets a fresh
    /// request for the same page be scheduled immediately instead of waiting on the old one).</summary>
    private void DisposeRenderWindow()
    {
        lock (_windowLock)
        {
            foreach (var entry in _renderWindow.Values) entry.Dispose();
            _renderWindow.Clear();
            _pendingRenders.Clear();
        }
    }

    /// <summary>
    /// Removes and returns <paramref name="page"/>'s render-window entry without disposing its
    /// bitmap — the caller takes ownership. Used by <see cref="LoadPageBitmap"/> to promote a
    /// neighbour that a re-anchor just made the new anchor, instead of re-rendering a page that
    /// was rasterised a moment ago as a visible neighbour (docs/continuous-scroll-plan.md §5
    /// Phase 1: "render if missing" — a page already in the window is not missing). Returns false
    /// (falling through to a fresh render) when the neighbour's background render hasn't completed
    /// yet — it is still tracked in <see cref="_pendingRenders"/>, not yet in the window.
    /// </summary>
    internal bool TryTakeFromWindow(int page, out IRenderedPage bitmap, out int dpi, out double width, out double height)
    {
        lock (_windowLock)
        {
            if (_renderWindow.TryGetValue(page, out var entry))
            {
                _renderWindow.Remove(page);
                (bitmap, dpi, width, height) = (entry.Bitmap, entry.Dpi, entry.Width, entry.Height);
                return true;
            }
        }
        (bitmap, dpi, width, height) = (null!, 0, 0, 0);
        return false;
    }

    /// <summary>
    /// Reacts to <see cref="CoreSettings.ContinuousScroll"/> flipping at runtime (from
    /// <see cref="DocumentController.OnConfigChanged"/>). Entering: builds the document layout and
    /// seeds the render window. Leaving: drops every window entry (the anchor's own bitmap is
    /// untouched either way) and re-clamps onto the single page.
    /// <para>
    /// <paramref name="continuous"/> only steers which branch runs here — it is NOT stored, and it
    /// does not by itself make <see cref="ContinuousScroll"/> true. That property reads
    /// <c>Owner.Config.ContinuousScroll</c>, so the caller must update the document's config to the
    /// new value BEFORE calling this (as <see cref="DocumentController.OnConfigChanged"/> does via
    /// <c>doc.UpdateBackgroundSettings</c>, ahead of every viewport's <c>OnScrollModeChanged</c> in
    /// the same loop). Calling this with <c>continuous: true</c> while <c>Owner.Config.ContinuousScroll</c>
    /// is still false is a silent no-op: <see cref="EnsureRenderWindow"/> bails on its own
    /// <see cref="ContinuousScroll"/> check and <see cref="Viewport.ClampCamera"/> takes the
    /// single-page branch instead of the document one.
    /// </para>
    /// </summary>
    internal void OnScrollModeChanged(bool continuous)
    {
        if (continuous)
        {
            Owner.EnsurePageLayout();
            if (Width > 0 && Height > 0)
            {
                ClampCamera(Width, Height);
                EnsureRenderWindow(Width, Height);
            }
        }
        else
        {
            DisposeRenderWindow();
            if (Width > 0 && Height > 0) ClampCamera(Width, Height);
        }
    }
}
