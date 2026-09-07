# Continuous scrolling — Core implementation plan

> **Status (2026-09-07):** Phases 0-4 implemented on branch `plan/continuous-scroll`, v0.61.0.
> All invariants (I1-I7) hold; full pre-existing test suite green throughout. One correction to
> this document found during implementation: §1.2's re-anchor formula has the sign backwards —
> it must be `ox' = ox + (Left[b]-Left[a])·z` (**new minus old**), not `(Left[a]-Left[b])·z` as
> written below; verified independently against this section's own `off_p`-invariance proof.
> Deferred to a follow-up: the render window renders synchronously rather than via the
> `Task.Run`-based `ScheduleWindow` queue this document describes (§5 Phase 1) — correct and safe,
> but not overlapped with the frame; revisit if scroll feels janky on slow devices.
> **Driver:** the RailReader2 GUI team wants pages treated as one continuous entity, inside and
> outside rail mode. **Scope:** `RailReader.Core` (+ a small `Renderer.Skia` follow-up). The host
> (railreader2) has its own checklist in §7.
> **Verdict:** viable as an **opt-in, additive** change. Single-page behaviour stays byte-for-byte
> unchanged for every consumer that does not set `CoreSettings.ContinuousScroll`.
>
> This supersedes the 2026-06-14 "too risky" assessment. That assessment assumed continuous scroll
> meant redefining `Camera.OffsetY` as a document-wide coordinate (every consumer of the camera
> changes) and rewriting `RailNav` to span pages. Neither is necessary — see §1. Two things changed
> since June that make the contained design possible: the multi-viewport refactor moved camera
> geometry, page position, the render cache and the tick onto `Viewport` (so there is exactly one
> place to teach about neighbouring pages), and `DocumentModel.GoToPage` is now viewport-addressed
> with a single chokepoint for page transitions.

## Contents

1. [The representation: page-anchored camera](#1-the-representation-page-anchored-camera)
2. [What "continuous" means in rail mode (v1 vs v2)](#2-what-continuous-means-in-rail-mode-v1-vs-v2)
3. [Invariants](#3-invariants)
4. [New public surface](#4-new-public-surface)
5. [Phases and file-level changes](#5-phases-and-file-level-changes)
6. [Tests](#6-tests)
7. [Host (railreader2) contract](#7-host-railreader2-contract)
8. [Risks, mitigations, open decisions](#8-risks-mitigations-open-decisions)
9. [Out of scope / deferred](#9-out-of-scope--deferred)
10. [Release](#10-release)

---

## 1. The representation: page-anchored camera

### 1.1 Today

`Camera` (`src/RailReader.Core/Models/Camera.cs`) is `(OffsetX, OffsetY, Zoom)` and maps the
**current page's** point space to the screen: `screen = page × Zoom + Offset`. `Viewport`
(`src/RailReader.Core/Viewport.cs`) owns `CurrentPage`, `PageWidth/Height`, one rasterised bitmap
(`CachedPage`, line 372) plus one prefetch buffer (`Prefetched`, line 417), and `ClampCamera`
(line 925) pins the offset so the page cannot leave the viewport. Every consumer — rail snap
targets (`RailNav.Snap.cs:169 ComputeTargetCamera`), zoom-about-cursor
(`ZoomAnimationController.cs:56`), hit-testing (`DocumentController.Navigation.cs HandleClick`,
`ActivateRailAt` line 419), annotations, search, the desktop's `BuildCamera` matrix, minimap,
`ScreenshotCompositor` — computes in this one page's frame.

### 1.2 The design

**Keep `Camera` exactly as it is, and define it as the transform of the *anchor page*
(`Viewport.CurrentPage`).** Add a document-level, immutable `PageLayout` (prefix sums of page
heights with a gap; pages centred in a column of the widest page). Every other page's transform is
*derived* from the anchor's:

```
Layout:   Top[p]  = Σ_{q<p} (H[q] + Gap)          Left[p] = (MaxW − W[p]) / 2
          TotalHeight = Top[n−1] + H[n−1]           MaxW = max W[p]

Anchor a = vp.CurrentPage, camera (z, ox, oy) is page a's transform.

Transform of page p:   off_p = ( ox + (Left[p] − Left[a])·z ,  oy + (Top[p] − Top[a])·z )
Document offset:       DocOff = ( ox − Left[a]·z ,  oy − Top[a]·z )
Re-anchor a → b:       ox' = ox + (Left[a] − Left[b])·z ,  oy' = oy + (Top[a] − Top[b])·z
```

Re-anchoring is a pure renaming: for every page p, `off_p` computed from `(b, ox', oy')` equals
`off_p` computed from `(a, ox, oy)`. The screen does not move.

**Why this is the whole trick.** Zoom-about-a-screen-focus is an affine map applied uniformly, so
it is frame-invariant. With `D = oy − Top[a]·z` the document-space rule `D' = f − (f − D)·(z'/z)`
rearranges to `oy' = f − (f − oy)·(z'/z)` — *exactly the existing formula* in
`ZoomAnimationController.Start`. Likewise panning (`HandlePan` adds `dx, dy` to the offset) is
identical in either frame. So:

- **Unchanged:** zoom/pan math, zoom animation, rail snap targets and per-frame clamp, vertical
  bias, `ReadingPosition`, persistence (`SavePosition` stores page + anchor camera, round-trips),
  annotations/search/links (page-keyed, page-space), `FocusBlock` confinement, overlays for the
  anchor page, multi-viewport (each view has its own anchor; the layout is shared).
- **Changed:** the vertical/horizontal *clamp* (document extent instead of page extent), the
  *render path* (a small window of page bitmaps instead of one + one prefetch), *hit-testing* (a
  screen point may land on a neighbour page), *page transitions* (a new "preserve screen position"
  transition used by re-anchoring and by rail's page advance), and the non-rail arrow-key edge-hold
  (no longer needed: panning just continues).

**Why not a document-space camera.** It is the June design: every camera consumer in Core, the
Skia renderer and the host changes at once, `ReadingPosition`/persistence semantics change, and
there is no byte-for-byte fallback. The page-anchored design has a strictly smaller blast radius
and a natural opt-in switch. Nothing user-visible is lost: the anchor is an implementation detail.

## 2. What "continuous" means in rail mode (v1 vs v2)

`RailNav` stays **page-local** in v1. A rail step off the last line still returns
`NavResult.PageBoundaryNext` (`RailNav.cs:532`), and the controller still walks pages via
`SkipToNavigablePage` (`DocumentController.Navigation.cs:61`) and reseats the rail on the new
page's analysis. What changes is only the *camera continuity* of that transition: today
`DocumentModel.GoToPage` (`DocumentModel.cs:848`) keeps the old page-local `OffsetY`, so the
screen cuts to the equivalent position on the new page and then snaps to line 0. In continuous
mode the transition uses `PageTransition.PreserveScreen` (the re-anchor renaming above), so the
screen does not move at the moment of the page change, and the existing `StartSnap` then animates
from where the reader is to the next page's first line, across the visible page gap. The
neighbour page is already rasterised (render window), so the reader sees one smooth scroll.

Both directions (`StartSnapToEnd` for backward), the deferred path (analysis not cached:
`PendingSkip`, rail cleared, snap starts when the result lands via `ApplyAnalysisToViewport`,
`DocumentController.Animation.cs:388`) and auto-scroll's page advance (`TickAutoScroll`,
line 172) all flow through the same `GoToPage` call, so they get continuity for free. Auto-scroll
still **parks** on a page boundary (a stop unit) in v1.

**v2 (deferred, §9):** a document-wide `RailNav` (cross-page chunks so a paragraph split across a
page break frames as one unit, cross-page cell flow, auto-scroll flowing through a page break
without parking). That is the rewrite the June memo feared; it is not required for the GUI team's
goal and should be scoped only after v1 has shipped and been used.

## 3. Invariants

The implementer must keep these true; tests in §6 check each.

- **I1 — Single-page mode is byte-for-byte unchanged.** With `ContinuousScroll == false` no
  existing test changes and no existing code path is entered differently. Every new behaviour is
  behind `Viewport.ContinuousScroll` (derived from the document's `CoreSettings`), and the
  confined (`FocusBlock`) path ignores the mode entirely.
- **I2 — Re-anchoring never moves the screen.** For all pages p, `PageOffset(p)` before and after
  a `PreserveScreen` transition are equal to within 1e-9.
- **I3 — Rail owns the anchor while active.** Automatic re-anchoring (§5, Phase 2) runs only when
  `!Rail.Active` (paused implies active), `!Zoom.IsAnimating`, and not confined. While rail is
  active, `CurrentPage` changes only through the existing rail page-advance path.
- **I4 — A single-page document under continuous mode clamps identically to single-page mode**
  (`TotalHeight = H[0]`, `MaxW = W[0]`): the document clamp degenerates to the page clamp.
- **I5 — `CurrentPage` is always a page that intersects the viewport** after any tick or input in
  browse mode (that is what re-anchoring guarantees), so every page-keyed consumer that reads
  `vp.CurrentPage` (search-match highlighting, annotations, `EvictDistantPageCaches`,
  `BackgroundAnalysisQueue.Reset`, `ReadingPosition.Page`) keeps a sensible meaning.
- **I6 — UI-thread discipline is unchanged.** Window renders run on the pool with the view's `Cts`
  (line 369) and `RenderGeneration` guard, marshal back, and bail on `Owner.IsDisposed || IsDisposed
  || RenderGeneration != generation`, exactly like `PrefetchPage` (line 547) today. PDFium calls
  stay inside the backends' `PdfiumGate.Lock`.
- **I7 — One layout per (document, ViewRotation).** `PageLayout` is immutable; a rotation rebuilds
  it *before* any viewport re-clamps (`DocumentModel.OnViewRotationChanged`, line 149, runs before
  the per-view `Viewport.OnViewRotationChanged`, line 524).

## 4. New public surface

All additive. Names are proposals; keep them unless there is a collision.

```csharp
// Models/CoreSettings.cs
public bool   ContinuousScroll            { get; init; } = false;
public double ContinuousPageGapPts        { get; init; } = 12.0;  // gap between pages, PDF points
public int    ContinuousRenderWindowPages { get; init; } = 4;     // max rasterised pages per view

// Models/PageLayout.cs  (new, immutable, pure geometry — unit-testable without a PDF)
public sealed class PageLayout
{
    public PageLayout(IReadOnlyList<(double W, double H)> sizes, double gap);
    public int Count { get; }
    public double Gap { get; }
    public double MaxWidth { get; }
    public double TotalHeight { get; }
    public double Top(int page);  public double Left(int page);
    public double Width(int page); public double Height(int page);
    public int PageAtY(double docY);          // binary search; in a gap → nearer page; clamped at ends
    public (int First, int Last) PagesIntersecting(double docY0, double docY1);
}

// Services/IPdfService.cs — default interface method, non-breaking
IReadOnlyList<(double Width, double Height)> GetPageSizes(int viewRotation)
{
    var list = new List<(double, double)>(PageCount);
    for (int i = 0; i < PageCount; i++) list.Add(GetPageSize(i, viewRotation));
    return list;
}
// Override in SkiaPdfService (Renderer.Skia/SkiaPdfService.cs:67) and PdfPigSkiaPdfService
// (Renderer.PdfPigSkia/PdfPigSkiaPdfService.cs:105) to open the document ONCE: today's
// per-page GetPageSize re-parses PdfBytes on every call, which is fine for one page and not
// fine for a 1000-page layout build.

// DocumentModel.cs
public PageLayout? PageLayout { get; }            // null until EnsurePageLayout ran
public void EnsurePageLayout();                   // idempotent; safe on the open thread

// Models/PageTransition.cs (new)
public enum PageTransition { Default, PreserveScreen }
//   Default        — single-page: today's behaviour (keep offset, page clamp).
//                    continuous:  page top at the viewport top (an explicit jump lands on the page).
//   PreserveScreen — the §1.2 renaming; used by re-anchoring and rail page advance.

// DocumentModel.GoToPage — add an optional trailing parameter (existing callers unchanged)
public bool GoToPage(Viewport vp, int page, AnalysisWorker? worker,
    IReadOnlySet<BlockRole> navigableRoles, double windowWidth, double windowHeight,
    PageTransition transition = PageTransition.Default);

// Viewport.cs
public bool ContinuousScroll { get; }                               // mode in effect for this view
public (double OffsetX, double OffsetY) PageOffset(int page);       // transform of page p, anchor frame
public double DocumentOffsetX { get; }  public double DocumentOffsetY { get; }
public IReadOnlyList<VisiblePage> VisiblePages { get; }             // page order; single-page mode → [anchor]
public (int Page, double PageX, double PageY) ResolvePoint(double canvasX, double canvasY);
public int ComputeAnchorPage(double windowWidth, double windowHeight);  // page under the viewport centre

public readonly record struct VisiblePage(
    int Page, double OffsetX, double OffsetY, double Width, double Height,
    IRenderedPage? Bitmap, int Dpi);   // Bitmap null while its render is in flight

// DocumentController.cs
public void AnchorToPage(int page);   // GoToPage(focused, page, PreserveScreen); host uses it before
                                       // handing page-space input for a non-anchor page to the
                                       // annotation handler / HitTestLink
public (int Page, double PageX, double PageY) ResolvePoint(double canvasX, double canvasY);
```

Semantics to document in the XML docs: in continuous mode `PageChanged` also fires when
scrolling re-anchors (the page indicator follows the viewport centre), and `Viewport.PageWidth/
Height`, `CachedPage`, `CachedDpi`, `MinimapPage` describe the **anchor** page.

## 5. Phases and file-level changes

Each phase is a separate PR-sized increment, buildable and green on its own. Do not start Phase 2
until Phase 1's tests pass with the mode off and on.

### Phase 0 — `PageLayout` + page sizes (S)

- `Models/PageLayout.cs`: as in §4. Pure; no dependencies.
- `Services/IPdfService.cs`: `GetPageSizes(int viewRotation)` default method.
- `Renderer.Skia/SkiaPdfService.cs:67` and `Renderer.PdfPigSkia/PdfPigSkiaPdfService.cs:105`:
  single-open overrides. For PDFium: one `lock (PdfiumGate.Lock)`, open once, loop
  `FPDF_GetPageSizeByIndexF` (or the PDFtoImage equivalent), apply the same rotation composition
  `GetPageSize(int,int)` applies today.
- `DocumentModel.cs`: `PageLayout` + `EnsurePageLayout()` (reads `_config.ContinuousPageGapPts`,
  `_pdf.GetPageSizes(_viewRotation)`). Invalidate + rebuild in the `ViewRotation` setter path
  (line 128/149) **before** the per-view rotation handlers run. Nothing reads it yet.
- `CoreSettings.cs`: the three settings.

### Phase 1 — Render window (M)

Goal: a `Viewport` can hold bitmaps for several pages. **Recommendation: do not refactor the
shipped single-page path.** Add `Viewport.RenderWindow` (internal class, file
`Viewport.RenderWindow.cs`) and branch at the top of the five render methods when
`ContinuousScroll` is on; leave their single-page bodies untouched. This is deliberate
duplication that buys I1; unify later once continuous is proven (§9).

- `RenderWindow` entries: `page → (IRenderedPage Page, IRenderedPage? Minimap, int Dpi,
  double W, double H, int Generation)`. Minimap only for the anchor.
- `Wanted(ww, wh)`: pages intersecting the viewport, plus one beyond in each direction, ordered by
  distance from the viewport centre, capped at `ContinuousRenderWindowPages` and a megapixel budget
  (sum of `w·h·dpi²/72²` over entries ≤ `RenderDpi.MaxMegapixels × 2`; drop the farthest first).
- `LoadPageBitmap` (line 446): continuous → ensure the anchor's entry synchronously (render if
  missing, same DPI rule `CalculateRenderDpi(zoom, W, H, RenderDpi)` per page), set
  `PageWidth/Height` via `SetPageSizeFromLoad`, then `ScheduleWindow()` for the rest.
- `ScheduleWindow()`: at most one in-flight render per view (reuse `PrefetchPending`/
  `DpiRenderPending` as a single `RenderPending`), pick the first wanted page whose entry is
  missing or whose DPI is outside the hysteresis band (anchor first), `Task.Run` with `Cts.Token`,
  `RenderGeneration` captured, marshal back, install, clear the flag, and re-run `ScheduleWindow()`
  so the queue drains one page per completion. Mirror the bail conditions of `PrefetchPage`.
- `UpdateRenderDpiIfNeeded` (line 614): continuous → keep the scroll-skip gate, then
  `ScheduleWindow()`; return whether something was scheduled. `RenderDpiDirty` forces every
  entry's DPI to be re-evaluated (as the forced path does today).
- `PrefetchPage` (line 547): continuous → no-op (the window already covers the next page).
- `OnViewRotationChanged` (524) / `OnRenderQualityChanged`: continuous → `RenderGeneration++`,
  dispose all entries, re-run `LoadPageBitmap`.
- `Dispose`: dispose entries.
- Facades: `CachedPage`/`CachedDpi`/`MinimapPage` getters return the anchor's entry when continuous.
  Their setters are `internal` and only used by the single-page path.
- Eviction: at the end of `TickViewport` (continuous only) dispose entries outside `Wanted`.
  The host must not retain `VisiblePages[i].Bitmap` past the frame (§7).
- `VisiblePages` (§4) is implemented here: from `Layout`, `Camera`, `Width/Height`; in single-page
  mode it returns one element built from the existing fields so a host can adopt one draw loop.
- Mode switch at runtime (`OnConfigChanged`, `DocumentController.cs:866`): call a new
  `vp.OnScrollModeChanged(bool continuous)`. Entering: `Owner.EnsurePageLayout()`, move
  `CachedPage/CachedDpi/MinimapPage` into the window as the anchor's entry, drop `Prefetched`,
  `ClampCamera`. Leaving: keep the anchor's entry as `CachedPage` etc., dispose the rest,
  `ClampCamera` (the page clamp pulls the camera back onto the page).

### Phase 2 — Continuous browse mode (M)

- `Viewport.ClampCamera` (925): after the confinement branch, if continuous and `Layout` is
  present: clamp `DocumentOffsetX` against `MaxW·z` and `DocumentOffsetY` against
  `TotalHeight·z` with the same "centre if smaller than the window, else clamp to
  `[wh − scaled, 0]`" rule, then convert back to the anchor frame. I4 falls out.
- `Viewport.PageOffset`, `DocumentOffsetX/Y`, `ResolvePoint`, `ComputeAnchorPage` (§4).
  `ResolvePoint`: single-page → `(CurrentPage, (cx − ox)/z, (cy − oy)/z)` verbatim; continuous →
  `docY = (cy − DocOffY)/z`, `p = Layout.PageAtY(docY)`, page coords relative to `Top[p]/Left[p]`.
- `DocumentModel.GoToPage` (848): implement `PageTransition`. Order inside the method:
  `ClearPendingState` → compute the new offsets from the *old* anchor and the layout →
  `vp.CurrentPage = page` → `LoadPageBitmap` (window hit in the common case) → `SubmitAnalysis` →
  restore zoom → assign the offsets → `ClampCamera`. `Default` in continuous mode sets
  `oy = 0` (page top at viewport top) and keeps `ox`; single-page keeps today's body exactly.
- `DocumentController`:
  - `ReanchorIfNeeded(Viewport vp, double ww, double wh)` (private): I3 gate, then
    `b = vp.ComputeAnchorPage(ww, wh)`; if `b != vp.CurrentPage` →
    `vp.Owner.GoToPage(vp, b, _worker, _config.NavigableRoles, ww, wh, PageTransition.PreserveScreen)`,
    `doc.QueueLookahead`, `Search.UpdateCurrentPageMatches()`, `RaisePageChanged(vp)`. Do **not**
    push history (scrolling is not navigation).
  - `TickViewport` (`Animation.cs:27`): call it after `TickAutoScroll` and before the pump; fold
    the result into `pageChanged`. Then, continuous only, the window eviction step.
  - `HandlePan` (636): call it after `ClampCamera` so the page indicator updates on the same input.
  - `HandleVerticalNav` (`Navigation.cs:181`) non-rail branch: continuous → pan + clamp +
    `ReanchorIfNeeded`, skip `PageEdgeHold` entirely (the document ends are the only edges).
  - `GoToPage(int)` (526): unchanged signature; passes `Default`. `ScrollToDestination` (499),
    `NavigateToBookmark`, back/forward, links: unchanged — they set the offset after the jump.
  - `HandleClick`, `ActivateRailAt` (419), `HitTestLink(pageX, pageY)`: resolve via
    `vp.ResolvePoint`; if the resolved page ≠ anchor, `AnchorToPage` first, then proceed with the
    resolved page coordinates. `AnnotationInteractionHandler` stays page-local: the host resolves
    and anchors before calling it (§7). `AnchorToPage` and `ResolvePoint` public wrappers (§4).
  - `SearchService.NavigateToActiveMatch` / `ScrollToMatchRect`: no change needed (`_goToPage`
    then an explicit offset assignment + `ClampCamera`, which now clamps to the document).
  - `AddDocument`: call `EnsurePageLayout()` (cheap no-op if the open thread already did it via
    `LoadPageBitmap`).

### Phase 3 — Continuous rail (S)

- `SkipToNavigablePage` (`Navigation.cs:61`): pass
  `vp.ContinuousScroll ? PageTransition.PreserveScreen : PageTransition.Default` to
  `doc.GoToPage`. That single change gives cross-page continuity for manual line advance, edge-hold
  advance, auto-scroll advance, the backward `StartSnapToEnd` path and the deferred `PendingSkip`
  path (the camera waits at the boundary; `ApplyAnalysisToViewport` snaps when the result lands).
- `TickAutoScroll` (172): the `PrefetchPage(next)` call is already a no-op in continuous mode.
  Park-on-page-change stays.
- `HandleClick`'s rail branch and `ActivateRailAt` already anchor first (Phase 2), so clicking a
  line on the neighbour page seats the rail there.
- Verify `ResumeRailFromPause` (a Ctrl-drag can now leave the anchor page; I3 keeps the anchor):
  the restore snaps back to the seated page — expected. Document it.

### Phase 4 — Screenshot parity, docs (S)

- `Renderer.Skia/ScreenshotCompositor.cs`: `RenderPage` (27) + `CropToViewport` (218) render one
  page. Add a continuous path that composites `vp.VisiblePages` (bitmap, per-page annotations,
  search highlights, debug overlay; rail overlay on the anchor only) so the CLI/agent screenshot
  matches the GUI. Keep `RenderPage` for the single-page path.
- `CLAUDE.md`: a "Continuous scroll (`PageLayout`, page-anchored camera)" paragraph under the
  `RailReader.Core` section; `docs/multi-viewport-design.md`: a pointer. `CHANGELOG.md` entry.

Effort: S ≈ a day, M ≈ two to three days, for an agent with the codebase loaded.

## 6. Tests

Add `tests/RailReader.Core.Tests/ContinuousScrollTests.cs` (+ `PageLayoutTests.cs`). Use the
existing harness: `TestFixtures.GetTestPdfPath()` (3-page synthetic PDF),
`SynchronousThreadMarshaller`, `FakeLayoutAnalyzer` for rail, and the `MultiViewportTests` setup
pattern. Enable the mode with `_appConfig.ToCoreSettings() with { ContinuousScroll = true }`.

- **PageLayout:** prefix sums with gap; `Left` centring for mixed widths; `PageAtY` inside a page,
  inside a gap (nearer page), beyond both ends; `PagesIntersecting`; rotation swaps W/H (build
  from `GetPageSizes(1)`).
- **I1 guard:** the entire existing suite passes with no test edits. Additionally, with the mode
  off, `VisiblePages.Count == 1`, `ResolvePoint` equals the legacy formula, and
  `GoToPage(..., Default)` leaves `OffsetY` exactly as before.
- **I2 re-anchor:** pan a 3-page doc past page 0; assert `CurrentPage == 1`, `PageChanged` fired
  once, no history push, and `PageOffset(p)` for p = 0..2 unchanged to 1e-9 across the transition.
- **Zoom invariance:** `Zoom.Start` about a focus over page 1 while anchored on page 0; tick to
  completion; assert the document point under the focus is fixed and `PageOffset` is consistent
  after a subsequent re-anchor.
- **Clamp:** at the top of page 0 `DocumentOffsetY == 0`; at the end
  `DocumentOffsetY == wh − TotalHeight·z`; horizontal clamp against `MaxW`; I4 with a 1-page PDF.
- **Render window:** at a page boundary `VisiblePages` has two entries with offsets differing by
  `(H[0] + Gap)·z`; bitmaps present after the scheduled renders drain (synchronous marshaller);
  DPI re-render after a zoom applies to every visible page; entries beyond the window are disposed;
  `CachedPage` is the anchor's bitmap; rotation/quality change clears and re-renders.
- **Rail cross-page (Phase 3):** seat rail on page 0's last line (FakeLayoutAnalyzer with
  navigable blocks on pages 0 and 1, analysis cached for both); `HandleArrowDown`; assert
  `CurrentPage == 1`, the previous line's screen Y is unchanged at the instant of the transition,
  a snap is in flight (`Rail.SnapProgress < 1`) whose target equals `Rail.ComputeSnapTarget`
  for line 0 of page 1; run ticks and assert the camera lands there. Repeat backward
  (`StartSnapToEnd`) and for the deferred case (analysis for page 1 not cached: camera stays,
  `PendingSkip` set, then `PollAnalysisResults` seats and snaps).
- **Auto-scroll page advance:** reaches the page boundary, transitions with continuity, parks.
- **Hit-test on a neighbour page:** `ResolvePoint` returns page 1 coords for a screen point below
  page 0; `ActivateRailAt` there anchors to page 1 and seats the rail; `HandleClick` on a page-1
  link resolves the page-1 link.
- **Persistence round-trip:** save (anchor + camera) → reopen → identical `PageOffset` for all pages.
- **Multi-viewport:** two viewports on one document with different anchors and zooms; re-anchoring
  one leaves the other's camera untouched; `EvictDistantPageCaches` still honours both.
- **Mode switch at runtime:** toggle on → window seeded from the single bitmap, camera clamped to
  the document; toggle off → camera clamped back onto the anchor page, extra bitmaps disposed.
- **Confined view:** with `Focus` set, continuous mode is inert (`VisiblePages == [anchor]`, no
  re-anchor, block clamp unchanged) — extend `FocusBlockTests`.

Run the flaky-test loop from `CLAUDE.md` on the window-render tests (they involve `Task.Run`).

## 7. Host (railreader2) contract

Core-side scope ends at the API in §4; this is what the GUI team needs to do to consume it. It is
small because overlays are already camera-relative.

- **Draw loop:** replace the single `PdfPageRenderState(Image, PageW, PageH, Camera: BuildCamera(vp))`
  with one draw per `vp.VisiblePages` entry using `SKMatrix.CreateScaleTranslation(z, z, e.OffsetX,
  e.OffsetY)`. Paint the page gap as background. Do not retain `e.Bitmap` beyond the frame (the
  window disposes evicted entries at the end of a tick); the existing `ViewportImages` retire path
  needs a per-page variant.
- **Overlays per visible page:** annotations (`Annotations.Pages[e.Page]`), search
  (`MatchesForPage(e.Page)`), debug (`AnalysisCache[e.Page]`) — all already page-keyed. Rail
  overlay and line highlight: anchor page only. Line-focus blur must dim the other visible pages.
- **Input:** `ViewportPanel.ScreenToPage` → `controller.ResolvePoint`; before calling any
  `AnnotationInteractionHandler` method or `HitTestLink` for a resolved page ≠ `vp.CurrentPage`,
  call `controller.AnchorToPage(page)`. A drag that crosses a page boundary stays on its start page.
- **Wheel:** in browse mode, decide whether the wheel scrolls (`HandlePan(0, dy)`) or zooms
  (today). Core supports both; a continuous viewer usually scrolls and zooms with Ctrl.
- **Minimap / scrollbar:** use `doc.PageLayout.TotalHeight` and `vp.DocumentOffsetY` for a
  document-wide thumb; the page-local minimap keeps working for the anchor.
- **Page indicator:** `PageChanged` now fires on scroll re-anchoring; treat it as "page under the
  viewport centre".
- **Freeze panes, portals, `FocusBlock` views:** unchanged (they read the anchor camera / are
  single-page by construction).
- **Settings UI:** map a "Continuous scrolling" toggle to `CoreSettings.ContinuousScroll` in
  `AppConfig.ToCoreSettings`; the runtime switch goes through `OnConfigChanged`.

## 8. Risks, mitigations, open decisions

- **Synchronous render on a re-anchor cache miss.** `GoToPage` renders the anchor synchronously
  on the UI thread (it does today on every page change). With the window one page ahead this is a
  hit during normal scrolling; a very fast fling can outrun it. Accept in v1; measure with
  `/usr/bin/time -v` on a 300 DPI page; if it shows, let `LoadPageBitmap` install the anchor
  asynchronously and let the host draw the gap colour until it lands.
- **Memory.** Rail zoom (≥3×) renders at ~216–300 DPI: ~33 MB per Letter page; a window of three
  is ~100 MB per viewport, versus ~66 MB today (page + prefetch). The megapixel budget and
  `ContinuousRenderWindowPages` bound it; two split panes double it, as they do today.
- **`GetPageSizes` on huge documents.** With the single-open override this is one document parse
  plus n cheap size reads; without it, n parses. Do the override in Phase 0, and time it on the
  largest PDF in the corpus.
- **Snap flight length across a page break.** At rail zoom the distance from the last line of a
  page to the first line of the next can be several viewport heights; the 450 ms cubic snap covers
  it but fast. *Recommendation:* keep the single snap in v1 (it is what "continuous" means); if the
  GUI team finds it too abrupt, add `CoreSettings.ContinuousRailSnapDurationMs` rather than a
  distance cap.
- **`PageChanged` chatter while scrolling.** Re-anchoring uses the viewport centre, which crosses
  a boundary once per page; no hysteresis is needed. Hosts that trigger heavy work on
  `PageChanged` (index panels, AT announcements) should debounce — flag this to the GUI team.
- **Explicit jump semantics in continuous mode.** `Default` lands the page top at the viewport
  top. Alternative: keep the reader's fractional position. Recommendation: page-top; it is what
  every continuous viewer does and what `ScrollToDestination`/search override anyway.
- **Rail while the reader scrolled away (paused).** I3 keeps the anchor on the seated page during
  a Ctrl-drag; resume snaps back, possibly across pages. Acceptable; documented.
- **OCR'd / textless pages.** No interaction: analysis submission is per anchor page as today and
  read-ahead rules are unchanged.

## 9. Out of scope / deferred

- **Document-wide `RailNav` (v2):** cross-page chunks, cross-page cell flow, auto-scroll without a
  park at page breaks. Requires `RailNav` to hold ≥2 `PageAnalysis` instances and a navigable index
  that spans pages; touches `BuildChunks`, `ComputeTargetCamera`, `ReadingPosition`, the
  `PendingSkip` machinery and every `NavResult.PageBoundary*` consumer. Scope only after v1 ships.
- **Unifying the single-page and window render paths** in `Viewport` (deliberate duplication in
  Phase 1). Do it when continuous is the default for the desktop, with the whole suite as the guard.
- **Horizontal continuity / two-page spreads.** `PageLayout` centres pages in one column; a
  spread layout is a different `PageLayout` construction plus a `Left` rule, not a camera change,
  so it can come later without touching Phases 2–3.
- **RailReaderLite (WASM)** is frozen; the MAUI target would inherit this via `Core` unchanged.

## 10. Release

- Additive, opt-in → minor bump `0.61.0`, `CHANGELOG.md` section listing the new settings, the
  `IPdfService.GetPageSizes` default method (existing implementations keep compiling), the
  `GoToPage` optional parameter, the `Viewport`/`DocumentController` additions, and the
  `PageChanged` semantics note for continuous mode.
- Test against railreader2 via local pack + temporary `NuGet.config` override before release.
- The implementing agent must not tag, push tags, open or merge PRs (see `CLAUDE.md`).

### Definition of done (v1)

1. Phases 0–4 merged as separate PRs, each green (`dotnet test tests/RailReader.Core.Tests -c Release`).
2. With `ContinuousScroll = false`, the pre-existing suite is unchanged and green (I1).
3. The §6 tests exist and pass, including the flaky-loop run on the window-render tests.
4. `tools/` screenshot / CLI output for a continuous viewport matches the GUI (Phase 4).
5. `CLAUDE.md` and `CHANGELOG.md` updated; this document's status line updated.
