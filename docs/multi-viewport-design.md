# Multi-viewport design: independent & detachable viewports in RailReader.Core

> **Status:** Phase 0 landed on `feat/multi-viewport` (2026-06-20); Phases 1–3 pending.
> **Scope:** `RailReader.Core` only. **Driver:** support split-pane and detached-window reading —
> *N* independent, interactive cameras over one open document — without changing the threading model.
>
> **Implementation progress.** Phase 0 (the `DocumentState` → embedded `Viewport` extraction) is
> implemented: `Viewport` now owns the camera, `RailNav`, current-page + dimensions, the rasterised-page
> cache, the render-DPI state machine, the prefetch buffer, pending rail/skip state, the lookahead
> queue, the back/forward stacks, and the display prefs; `DocumentState` embeds one `Primary` viewport
> and delegates. Zero API/behaviour change — 713/713 tests green, full solution builds. **Not yet done
> in Phase 0:** the controller singletons (`_zoom` / `_autoScroll` / `_railPause` / `_pageEdgeHold` /
> `_vpWidth`/`_vpHeight`, §2.2) and `StateChanged`/`_cts` still live on `DocumentController` /
> `DocumentState`; their relocation is folded into the Phase 1 viewport-addressed controller refactor.

## Contents

1. [Goal & non-goals](#1-goal--non-goals)
2. [Core idea: split `DocumentState` into `DocumentModel` + `Viewport`](#2-core-idea-split-documentstate-into-documentmodel--viewport)
3. [Ownership shape](#3-ownership-shape)
4. [Render-loop model: per-viewport tick](#4-render-loop-model-per-viewport-tick)
5. [Analysis fan-out, lookahead & the shared worker](#5-analysis-fan-out-lookahead--the-shared-worker)
6. [Threading invariants](#6-threading-invariants)
7. [Migration — phased, railreader2-safe](#7-migration--phased-railreader2-safe)
8. [Persistence & settings](#8-persistence--settings)
9. [Testing](#9-testing)
10. [Open decisions](#10-open-decisions)

---

## 1. Goal & non-goals

**Goal.** Let one open document carry *N* independent, interactive viewports — each with its own
camera, rail position, page, zoom, render cache, and animation tick — so the GUI can host them in
split panes or detached windows. All on the single UI thread.

**Non-goals.**

- **No threading-model change.** Per-viewport threads stay rejected: `PdfiumGate` serialises all
  PDFium access process-wide (even across documents), so the rasterisation you'd parallelise can't
  run in parallel anyway; and the entire Core state model is single-thread-by-contract
  (`AssertUIThread`, unsynchronised `Camera`/`RailNav`). True parallelism here is high-risk,
  ~zero-gain. The CPU-bound work (ONNX, page render) is already off-thread.
- **No window/OS code in Core.** Tear-off windows, drag/resize/topmost, monitor placement, geometry
  persistence all live in the GUI (Avalonia). The existing `PortalWindow` already demonstrates the
  lifecycle. Core's job is only to expose an *addressable, independent `Viewport`* that a panel or a
  `Window` can drive.
- **No change** to `IPdfService` / analyzer / VLM interfaces.

**What this enables.** Today a "viewport" is implicit and fused 1:1 into `DocumentState`
(`Camera` is a field on it; the controller animates a single `ActiveDocument` with one viewport
size). The existing detached **Portal** is a *passive* `BlockCropRenderer` snapshot — it has no
camera precisely because Core has no independent one to hand it. This design provides that
independent camera, turning a detached window from a mirror into an interactive view.

---

## 2. Core idea: split `DocumentState` into `DocumentModel` + `Viewport`

`DocumentState` fuses two lifetimes. The split follows a seam that already exists: the geometry
layer (`DocumentState`, `RailNav`, `ZoomAnimationController`) already takes
`(windowWidth, windowHeight)` as **parameters** — nothing in the math reads a global viewport. The
work is moving *fields*, not rewriting algorithms.

### 2.1 Field-by-field placement

| Current `DocumentState` member | Goes to | Rationale |
|---|---|---|
| `_pdf`, `_pdfText`, `_pdfLink` | **DocumentModel** | One PDF handle per file; all access already `PdfiumGate`-serialised |
| `FilePath`, `PageCount`, `Title`, `Outline` | **DocumentModel** | Document identity |
| `_analysisCache`, `_textCache`, `_linkCache` | **DocumentModel** | Page-keyed, viewport-independent; immutable `PageAnalysis`/`PageText` are shareable read-only |
| `DocumentContentFraction` | **DocumentModel** | Union across all analysed pages |
| `Annotations`, `UndoStack`/`RedoStack`, `AnnotationGeneration`, `_annotationManager` | **DocumentModel** | Edits in one view must be visible in all |
| `BackgroundQueue` | **DocumentModel** | Analyse each page once for the document |
| `_pageCacheRadius`, `_tableRowReading`, `_cellNavigation` | **DocumentModel** | Analysis params must be consistent per doc (they shape cached results) |
| `Camera` | **Viewport** | The whole point |
| `Rail` (`RailNav`) | **Viewport** | Holds `CurrentBlock`/`Line`/`Cell`, snap, scroll, auto-scroll, `VerticalBias` |
| `CurrentPage`, `PageWidth`, `PageHeight` | **Viewport** | Each view sits on its own page |
| `CachedPage`, `CachedDpi`, `MinimapPage` | **Viewport** | DPI ∝ zoom — two views need two rasterisations |
| `_prefetched*`, `_dpiRenderPending`, `_renderDpi*`, `DpiRenderReady`, `OnDpiRenderComplete` | **Viewport** | Render-DPI state machine is per-camera |
| `PendingRailSetup`, `PendingSkip` | **Viewport** | Per-view rail/skip state |
| `PendingAnalysis` (lookahead queue) | **Viewport** | Per-view intent; a shared queue clobbers on navigation (§5) |
| `_backStack`, `_forwardStack` | **Viewport** | Each view navigates independently |
| `ColourEffect`, `LineFocusBlur`, `LineHighlightEnabled`, `MarginCropping`, `DebugOverlay` | **Viewport** (default from `CoreSettings`) | Per-view display prefs |
| `StateChanged` event | **Viewport** | "Which view changed" |
| `_cts` | **both** | Model CTS for its lifetime; **each Viewport gets its own CTS** for its render tasks (see §6) |

### 2.2 Controller singletons that become per-viewport

These are `DocumentController` fields today but hold per-view state, so they move *into* `Viewport`
— the same "extracted for testability" move that created them, one level down:

- `ZoomAnimationController _zoom` → `Viewport`
- `AutoScrollController _autoScroll` (its `AutoScrollActive` / `JumpMode`) → `Viewport`
- `RailPauseState _railPause` → `Viewport`
- `EdgeHoldStateMachine _pageEdgeHold` → `Viewport`
- `_vpWidth` / `_vpHeight` → `Viewport.Width` / `Height`

### 2.3 Stays global on `DocumentController`

`_config` (`CoreSettings`), `_worker` (the single shared `AnalysisWorker` / ONNX session),
`_annotationManager`, the injected services (`_marshaller`, `_pdfFactory`, `_recentFiles`,
`_annotationStore`, `_logger`), and a new **`FocusedViewport`** pointer (replaces the *role* of
`ActiveDocumentIndex`). `AnnotationInteractionHandler` and `SearchService` retarget to the focused
viewport.

---

## 3. Ownership shape

```
DocumentController
 ├── shared: _worker, _config, services, _annotationManager
 ├── List<DocumentModel> Documents          // one per open file (≈ tabs)
 ├── Viewport FocusedViewport               // input/search/annotation target
 └── each DocumentModel owns:
       List<Viewport> Viewports             // ≥1; Viewports[0] = Primary
```

`Viewport` holds a back-reference to its `DocumentModel` (read-only access to caches/PDF). Closing a
document disposes its viewports; closing a non-primary viewport just removes it. Composition (model
owns viewports) keeps lifetime clean and matches the GUI's tab + tear-off mental model: a tab is a
model with a primary viewport; detaching adds a viewport on the same model.

---

## 4. Render-loop model: per-viewport tick

Today one `controller.Tick(dt)` does **two** jobs — drains the analysis worker *and* animates the
single `ActiveDocument` — and returns an aggregate `TickResult`. The split separates those jobs:

- **`viewport.Tick(dt) → TickResult`** — pure camera/rail/zoom/auto-scroll animation. The body of
  today's `DocumentController.Tick` moves here, parameterised on the viewport's own
  `Camera`/`Rail`/size. Each on-screen surface (panel **or** detached window) drives its own
  viewport's tick from its own Avalonia compositor frame callback — still all on the UI-thread
  Dispatcher, so no concurrency is introduced.
- **`controller.PumpAnalysis()`** — the shared analysis pump, run once per frame globally. Defined
  in [§5.3](#53-two-pumps-one-global-analysis-pump-n-camera-ticks).

`IsAnimating` becomes per-viewport. This is what makes a detached window *interactive* rather than
the current passive Portal crop: the window owns a `Viewport` and ticks it. The legacy single
`controller.Tick` is retained as a facade through Phase 2 (§7).

---

## 5. Analysis fan-out, lookahead & the shared worker

This section governs how the **single** `AnalysisWorker` (one ONNX session) feeds **N viewports
across M document models**. It supersedes the single-active-document logic in
`PollAnalysisResults`. The skip-deferral state machine is unchanged in *logic* — it just relocates
to `Viewport` (its state — `CurrentPage`/`PendingRailSetup`/`PendingSkip`/`Rail` — is already
viewport-scoped per §2).

### 5.1 Ownership deltas this section requires

Three placements from §2 are load-bearing here and are restated as requirements:

| Member | Placement | Why (this section) |
|---|---|---|
| `PendingAnalysis` (lookahead queue) | **Viewport** (not model) | A shared model-level queue clobbers on every navigation — VP1@p6 and VP2@p20 overwrite each other's prefetch intent |
| text/link cache eviction center | **model-level, union of all viewport pages** | Single-center eviction drops a page out from under another viewport still sitting on it |
| `IsLive` (new) | **Viewport**, GUI-set | Replaces the `doc == ActiveDocument` gate (§5.2) |

`_analysisCache`/`_textCache`/`_linkCache`, `DocumentContentFraction`, and `BackgroundQueue` stay
**model-level** (shared, page-keyed, viewport-independent).

### 5.2 Viewport liveness — a tri-state, not a binary

Today exactly one document is "active," and that binary gates event-firing, animation requests, and
**deferred-skip resumption** (a non-active document *abandons* its `PendingSkip`). A detached window
is read independently and must resume its own skip — so the binary becomes three states:

| State | Count | Input | Fires events / requests anim | `PendingSkip` on analysis arrival |
|---|---|---|---|---|
| **Focused** | exactly 1 | keyboard/mouse; target of `Search`/`Annotations` | yes | resumes |
| **Live** | ≥0 (⊇ focused) | none | yes (own surface) | resumes |
| **Background** | rest | none | no | **abandons** (today's non-active behaviour) |

The GUI sets `Viewport.IsLive` (on-screen ⇒ live). Every `isActive` check in the fan-out becomes
`vp.IsLive`. Core defaults `IsLive` to track focus (focused ⇒ live, others ⇒ not), so the
single-viewport world reproduces today's gate with no GUI involvement until the GUI opts in (§7).

### 5.3 Two pumps: one global analysis pump, N camera ticks

The worker has one result channel; draining it per-viewport is redundant and couples
background-analysis arrival to the focused camera's animation. Split:

- **`controller.PumpAnalysis()`** — drains the worker, applies results model-first, fans out to live
  viewports, schedules lookahead/background work. Called **once per frame**, globally (GUI frame
  callback, or first viewport tick of the frame). Already sanctioned: `PollAnalysisResults` is
  documented as callable "from a low-frequency timer when not animating."
- **`viewport.Tick(dt) → TickResult`** — pure camera/rail/zoom/auto-scroll animation, driven by each
  surface's own frame callback.

`needsAnim`/`pageChanged` no longer return as aggregate bools; each affected viewport calls its own
`RequestAnimation()` / fires its own `PageChanged`.

### 5.4 `PumpAnalysis` — model-first, two-level fan-out

Model-first so `SetAnalysis` (which updates `DocumentContentFraction` and fires
`AnalysisCacheUpdated`) runs **once per model per result**, never once-per-viewport.

```
PumpAnalysis():                                   // controller-level, once/frame
  if worker is null: return
  while worker.Poll() is result:                  // drain shared channel
    matched = false
    for model in Documents where !Disposed and FilePath == result.FilePath:
      model.SetAnalysis(result.Page, result.Analysis)   // (1) cache once
      matched = true
      for vp in model.Viewports where !Disposed
                                  and CurrentPage == result.Page
                                  and PendingRailSetup:
        ApplyAnalysisToViewport(vp, result.Analysis)     // (2) rail per viewport
    if matched: AnalysisPageReady?.Invoke(result.Page)   // model-level event, once
  ScheduleReadAhead()                             // §5.5, after draining

ApplyAnalysisToViewport(vp, analysis):            // mirrors today's PollAnalysisResults inner body
  prevPage = vp.CurrentPage
  vp.Rail.SetAnalysis(analysis, _config.NavigableRoles)
  vp.PendingRailSetup = false
  vp.UpdateRailZoom(vp.Width, vp.Height)
  if vp.Rail.Active:
    if vp.PendingSkip is skip: ApplySkipLanding(vp, skip.Forward, skip.SavedBias)
    vp.PendingSkip = null
    vp.StartSnap(vp.Width, vp.Height)
    if vp.IsLive: vp.RequestAnimation(); vp.FireReadingPositionChanged()
  else if vp.PendingSkip is not null:
    if vp.IsLive:
      if TryResumeSkip(vp): vp.RequestAnimation(); vp.FireReadingPositionChanged()
    else:
      vp.PendingSkip = null                       // background abandons (tri-state)
  if vp.IsLive and vp.CurrentPage != prevPage:    // TryResumeSkip may have advanced it
    vp.FirePageChanged(vp.CurrentPage)
```

`SkipToNavigablePage`/`TryResumeSkip`/`ApplySkipLanding` take a `Viewport` instead of
`DocumentState`; their bodies are unchanged.

### 5.5 Submission paths

| Path | Scope | Notes |
|---|---|---|
| `SubmitAnalysis` (on navigation) | per-viewport | Producer task; preserves the atomic `_textCache[page]=…; worker.Submit(…)` post (§5.6, invariant 1). Guard `CurrentPage != page` is the *producer viewport's*. |
| `SubmitPendingLookahead` | **per-viewport** | Each live viewport drains its own `PendingAnalysis`. Cross-viewport dedup is free: it already skips `_analysisCache.ContainsKey(page) \|\| worker.IsInFlight(page)`. |
| `SubmitBackgroundAnalysis` | **model-level** | Whole-doc backfill via `BackgroundQueue`; renders the 800×800 pixmap synchronously (~5 ms). Re-centred on the **focused (or most-recently-navigated) viewport** — best-effort, already skips cached/in-flight. |
| `QueueLookahead` | **per-viewport** | Enqueues `CurrentPage+1…+count` into the viewport's own queue. |

`ScheduleReadAhead()` (end of `PumpAnalysis`) replaces the read-ahead tail of today's `Tick`. Gating
moves from "active doc not animating" to **"`worker.IsIdle` AND no live viewport has a user-initiated
render in flight"** (`vp.IsPdfiumBusy`): serve one live viewport's lookahead, else one model's
background page, priority to the focused viewport's model.

### 5.6 Invariants to preserve (do not "optimise" away)

The fan-out is correct because it is structurally the **same** problem as today's
multiple-tabs-of-the-same-file path (which already ships — it is why `PdfiumGate` exists).
`PollAnalysisResults` already loops all `Documents` matching `result.FilePath` and applies to each;
multiple independent consumers of one `(FilePath, Page)` result is a supported pattern. Three
properties make it correct, and the refactor must keep all three:

1. **`_inFlight` ⟺ `_textCache` atomicity.** `worker.Submit` (which does `_inFlight.Add`) and
   `_textCache[page]=…` execute in the *same* `_marshaller.Post`, same UI-thread turn. So any
   consumer observing `IsInFlight==true` is guaranteed the shared text. Never split these two writes.
2. **Producer-independent result fan-out.** A result is dispatched to every still-waiting viewport
   regardless of which one submitted, or whether the submitter navigated away / was disposed
   (`vp.IsDisposed` guard, §5.4). No "producer ownership" must be introduced.
3. **Consumers that miss the dedup window self-produce.** `_inFlight` is populated only *after* the
   async pixmap render, so a second consumer seeing `IsInFlight==false` launches its own producer
   task and is self-sufficient. This is what makes producer death a non-event.

### 5.7 Cache eviction (union)

`CurrentPage`'s setter must **not** call `EvictDistantPageCaches(value)` directly, dropping
text/link entries outside `±PageCacheRadius` of *that one page*. After the split, `_textCache`/
`_linkCache` are shared on the model: if VP1 jumps 5→50 and eviction re-centres on 50, **page 5's
text/links are evicted out from under VP2, which is still on page 5.** Instead, on any viewport page
change, the model evicts text/link entries outside `±PageCacheRadius` of **the union** of all live
viewports' current pages. Same for `UpdateBackgroundSettings`'s eviction. The analysis-geometry
cache is still never evicted.

### 5.8 Verified non-issues (don't over-engineer)

- **Double pixmap render** when two viewports both miss the dedup window: bounded,
  `PdfiumGate`-serialised; the second `worker.Submit` returns false so the **analyzer runs once**. An
  optional producer guard (`!model.AnyLiveViewportWaiting(page)` instead of `CurrentPage != page`)
  elides it but isn't required.
- **Per-viewport prefetch at different DPI** is correct (two zooms ⇒ two rasters), not a bug to fix.
- **Disposal during deferred skip**: the `vp.IsDisposed` guard (§5.4) handles it; the model still
  caches the result for others.

### 5.9 Targeted tests

1. Union eviction keeps a page that a *second* viewport is parked on after the first jumps away.
2. Two viewports with deferred skips on different pages resume **independently** (no queue clobbering).
3. A **live** (detached) viewport resumes its skip while a **background** tab's viewport **abandons**
   its skip — same analysis result, divergent outcome by `IsLive`.
4. Single analyzer invocation when two viewports request the same uncached page; both receive rail
   via fan-out.
5. `PumpAnalysis` fires `AnalysisCacheUpdated`/`AnalysisPageReady` exactly once for a two-viewport
   model.

---

## 6. Threading invariants

Unchanged from today — this is the safety guarantee that makes the whole design low-risk:

- All `Viewport`/`DocumentModel`/cache mutation stays UI-thread; `AssertUIThread()` is **preserved**,
  not relaxed.
- Per-viewport render/DPI/prefetch tasks run via `Task.Run` under `PdfiumGate.Lock`, posting results
  back through `_marshaller.Post` — exactly as today, just N of them. Caches are still written only on
  the UI thread ⇒ no locks needed.
- **Disposal ordering (correctness item).** A viewport's in-flight `Task.Run` touches `model._pdf`.
  `DocumentModel.Dispose` must cancel **all** viewport CTSs (and let the gate drain) *before*
  disposing `_pdf`, or a late render task hits a freed PDFium handle. Each `Viewport.Dispose` cancels
  its own CTS and frees its `CachedPage`/`MinimapPage`/prefetch.

---

## 7. Migration — phased, railreader2-safe

railreader2 consumes Core via NuGet, so the rule is: **every new multi-viewport symbol lands
additively in Phase 1, behind facades that reproduce today's single-viewport behaviour; the only
breaking release is Phase 3.** The three concepts from §5 — `Viewport.IsLive`,
`controller.PumpAnalysis()`, `Viewport.RequestAnimation` — are designed to *degenerate to current
behaviour* when a document has one viewport, which is exactly the Phase 0/1 state.

### Phase 0 — internal extraction, zero API/behaviour change

Introduce `Viewport` as an object *embedded inside* `DocumentState`; move the per-view fields (§2)
onto it; `DocumentState` delegates its existing public members to its single primary `Viewport`. The
§5 refinements all collapse to no-ops here:

- **union eviction** ⇒ union of one page = today's single center;
- **per-viewport `PendingAnalysis`** ⇒ the one queue;
- **`IsLive`** ⇒ always `true` (one viewport, always focused);
- **`PumpAnalysis`** ⇒ the body of today's `PollAnalysisResults`, still called from `Tick`.

Ships invisibly. Existing tests + railreader2 build untouched. Carries the disposal-ordering fix (§6)
and the eviction/loop restructure (§5.4, §5.7). **This is the bulk of the mechanical work.**

### Phase 1 — additive multi-viewport API

Everything below is **new public surface or a retained facade** — railreader2 compiles and behaves
identically with no edits, and opts in incrementally.

**New symbols**

| Symbol | Kind | Notes / default that preserves behaviour |
|---|---|---|
| `Viewport` (public class) | additive | Holds Camera/Rail/zoom-anim/auto-scroll/render cache/size + the §2 view-state |
| `DocumentState.Viewports` / `.Primary` | additive | `Primary` == the Phase 0 embedded viewport |
| `DocumentState.AddViewport()` / `RemoveViewport(vp)` | additive | A model starts with exactly `Primary` |
| `Viewport.Tick(double dt) : TickResult` | additive | Body relocated from `DocumentController.Tick`; the camera/rail/zoom/auto-scroll half |
| `Viewport.IsLive { get; set; }` | additive | **Core-defaults to focus-tracking**: focused ⇒ live, others ⇒ not. Reproduces today's `isActive` gate with no GUI involvement. GUI overrides for detached windows in Phase 2. |
| `Viewport.RequestAnimation { get; set; } : Action?` | additive | Per-surface "schedule a frame" callback — same shape as the existing `OnDpiRenderComplete`. Null on the primary in P1 (legacy `Tick` loop covers it). |
| `Viewport.PageChanged` / `ReadingPositionChanged` / `StateChanged` | additive | Per-viewport events. Controller-level events kept as facades (below). |
| `Viewport.Width` / `Height` (+ `SetSize`) | additive | Per-viewport viewport size |
| `controller.PumpAnalysis()` | additive | The global analysis pump (§5.4). Legacy `Tick` calls it internally. |
| `controller.FocusedViewport` | additive | Replaces the *role* of `ActiveDocumentIndex`; input/search/annotation target |

**Retained facades (the compatibility layer — kept through Phase 2)**

- `controller.Tick(dt)` ⇒ `PumpAnalysis(); return FocusedViewport.Tick(dt)`. `PumpAnalysis` stamps
  the focused viewport's overlay/page/anim dirty-flags so the returned `TickResult` still aggregates
  analysis-driven repaints exactly as today.
- `controller.PageChanged` / `ReadingPositionChanged` / `StateChanged` ⇒ forward the **focused**
  viewport's per-viewport events. (`AnalysisPageReady` stays genuinely model-level — page-keyed, not
  view-keyed.)
- `controller.IsAnimating` / `SetViewportSize` / `GetViewportSize` / `ActiveDocument` /
  `ActiveDocumentIndex` ⇒ delegate to the focused viewport / its model.

railreader2 impact in Phase 1: **none.** Its single `_controller.Tick(dt)` call and its event
subscriptions keep working through the facades.

### Phase 2 — GUI adoption (railreader2 only, no Core change)

railreader2 builds split-view / a real interactive detached viewport on the Phase 1 API:

1. Drive **per-surface** `viewport.Tick(dt)` from each top-level's own frame callback (panel and
   detached `Window`), instead of the single `controller.Tick`.
2. Call `controller.PumpAnalysis()` **once per frame** (global), decoupled from any one camera.
3. Set `viewport.IsLive = true` on each on-screen detached window; register its
   `viewport.RequestAnimation` to that window's "request next frame." This is the additive step that
   lets a detached window resume its own deferred skip and animate (§5.2) — the focused viewport
   already did via the facade.

> Note: this also retires the open question of whether to loop on the `IsAnimating` property vs
> `TickResult.StillAnimating` — each surface now reads *its own* viewport's `Tick` result, so there's
> no shared aggregate to disagree about.

### Phase 3 — cleanup (the one documented breaking release)

Only now do the facades go away. CHANGELOG-flagged, railreader2 pre-tested per the repo's release
rule.

- Remove `controller.Tick`/aggregate-`TickResult` entry → callers use per-surface `viewport.Tick` +
  `PumpAnalysis`.
- Remove the controller-level forwarding events → callers subscribe per-viewport.
- Remove `controller.IsAnimating` / `SetViewportSize` / `ActiveDocument` single-viewport facades.
- Drop `IsLive`'s focus-tracking default (GUI is now authoritative).
- Rename `DocumentState → DocumentModel`; delete the delegating shims.

### Break schedule at a glance

| Release | Breaking? | What railreader2 must do |
|---|---|---|
| Phase 0 | no | nothing (rebuild) |
| Phase 1 | no (all additive + facades) | nothing; may begin adopting |
| Phase 2 | no (Core unchanged) | per-surface Tick + one PumpAnalysis + set `IsLive`/`RequestAnimation` on detached views |
| Phase 3 | **yes** | drop facade usage; rename `DocumentState`→`DocumentModel`; subscribe per-viewport |

Design intent: a consumer can stay on the Phase 1 facades **indefinitely** and never see a break
until it chooses to adopt multi-viewport — and even adoption (Phase 2) is Core-compatible. Phase 3 is
optional tidy-up.

---

## 8. Persistence & settings

- `SaveReadingPosition` persists the **primary** viewport (page/zoom/offsets/colour). Detached
  viewports are session-ephemeral; their window geometry is persisted GUI-side (as the Portal already
  does). A persisted viewport session is a possible later addition.
- `OnConfigChanged` / `OnSliderChanged` iterate **all** viewports (rail config, navigable roles,
  render-DPI); per-view display prefs default from config per viewport.

---

## 9. Testing

- **Phase 0:** existing `DocumentController`/`DocumentState` tests stay green via delegation (they
  already use `SynchronousThreadMarshaller`).
- **Phase 1+ (multi-viewport):** the fan-out/skip set in [§5.9](#59-targeted-tests), plus:
  - two viewports on one model with independent cameras / rail positions;
  - closing one viewport leaves shared caches intact; closing the model disposes all viewports + PDF;
  - `DocumentModel.Dispose` drains viewport render tasks **before** freeing the PDF (§6).

---

## 10. Open decisions

Resolved by this doc (recorded so they aren't re-litigated): events are **per-viewport with focused
facades** (§7); the `DocumentState`→`DocumentModel` rename is **Phase 3** (§7); `BackgroundQueue`
re-centres on the focused/most-recent viewport (§5.5); ownership is **composition** — model owns
viewports (§3).

Still open:

1. **Two tabs of the same file = two `DocumentModel`s or one model with two viewports?** Affects
   whether annotations/caches are shared between them. Today they're separate `DocumentState`s but
   share the annotation file via `AnnotationFileManager.Checkout` ref-counting — so the
   model-per-tab choice must keep annotation sharing keyed by `FilePath`, independent of model
   identity.
2. **Per-viewport display prefs vs. inherit-from-document** for colour effect / margin cropping —
   do you want one pane greyscale and another not, or do they track the document? §2 places them on
   the viewport (independent); confirm that's desired UX.
3. **Per-viewport analysis params are blocked by the shared cache.** `TableRowReading`/
   `CellNavigation` ride in `AnalysisRequest` and shape the cached `PageAnalysis`. Two viewports
   can't post-process the same page differently without keying the cache on `(page, params)`. Kept
   **model/config-level**; revisit only if per-viewport cell-nav is ever wanted.
