# Performance review — June 2026

Branch: `perf/profiling-investigation`. Scope: the code that changed across the
recent run of releases (v0.18 → v0.21) — reading-order resolver, rail
navigation, smooth-zoom animation, the agent API surface, and the two new
PdfPig-backed projects (`Core.PdfPig`, `Renderer.PdfPigSkia`).

Method: read the changed hot-path code, ran the existing `AgentApiPerfBenchmarks`
suite in Release for real numbers, and used a paired in-process micro-benchmark
to compare implementations without cross-run thermal noise (this box has
`intel_pstate` active — cross-process wall-clock numbers are not comparable).

## Fixed in this branch

### 1. `PageText` geometric queries were O(chars) per call → O(log + band)
**File:** `src/RailReader.Core/Models/PageText.cs`. **Severity: high (measured).**

`ExtractTextInRect` / `ExtractBlockText` scanned *every* `CharBox` on the page on
every call. `GetPageDescription` calls `ExtractBlockText` once per block, so it
was **O(blocks × chars)** — measured **~4.6 ms** for a 50-block / 5000-char page.
The same full scan runs per line in the reading-position path
(`BuildReadingPosition`), which fires on every line advance while reading.

Fix: a lazily-built, cached index of the char boxes sorted by vertical midpoint
(built once per `PageText`; `PageText` is created once per page and cached in
`DocumentState.TextCache`). A rect query binary-searches the Y-band and scans
only the boxes inside it. A fraction threshold falls back to a sized,
index-order linear pass when the band covers most of the page, so large-rect
extraction never regresses (this was a real trap — the naive index version was
1.7× *slower* on a whole-page rect because of scattered reads + a Y-scrambled
post-sort).

Results:
- `GetPageDescription` (50 blocks, 5000 chars): **4570 → ~1900 µs/call (2.4×)**.
- Paired in-process bench vs the old `List<(int,char)>` + delegate-sort path:
  small-rect (line) **7.6× faster**, whole-page rect **1.6× faster**.
- 595/595 tests pass.

### 2. `PdfPigSkiaPdfService.RenderAtPixelSize` double-locked the gate
**File:** `src/RailReader.Renderer.PdfPigSkia/PdfPigSkiaPdfService.cs`.
**Severity: low.** It called `GetPageSize` (takes `PdfPigGate.Lock`, resolves the
page) and then `RenderAt` (takes the lock again, resolves the page again) for a
single render. Folded into one locked scope that resolves the page once. Affects
every thumbnail / pixmap render on the PdfPig backend.

## Identified, not changed (lower priority / higher risk)

These are real but small given current call frequency and input sizes; left as
documented opportunities rather than risking edits to subtle code.

### 3. `XYCutPlusPlusResolver` — repeated multi-pass LINQ over blocks
**File:** `src/RailReader.Core/Services/XYCutPlusPlusResolver.cs`. Runs once per
page on the analysis worker thread, N ≈ 5–100 blocks, so the absolute cost is
small. Opportunities:
- `FindColumnSplit` (~L887) and `OrderRow` (~L796) take several separate
  `.Min()`/`.Max()` LINQ passes over the same list — collapse to one loop.
- `IsValidColumnSplit` / `IsValidCenterSplit` partition with `.Where().ToList()`
  then take min/max on the result — partition + extremes in one pass, no list.
- `PreMask` is O(N²) (`IsMarginNote` / `IsClippingSpanner` each rescan all
  blocks); `AttachHeadings` and `ReinsertMasked` do repeated `IndexOf`/insert.
  Fine at N ≤ 100; revisit only if block counts grow.

### 4. `PdfTextService.GetTextRangeRects` (PdfPig) — O(ranges × boxes)
**File:** `src/RailReader.Core.PdfPig/PdfTextService.cs` (~L55). For each range it
re-scans every char box. Ranges are usually few (selection highlights), so low
impact. If it ever takes many ranges, bucket boxes by index once first. Also
`BuildPageText` (~L142) walks the word list twice (`words.Sum(...)` for capacity,
then the main loop) — trivial.

### 5. Per-call PDF reparse is the interface contract, not a regression
`IPdfTextService` / `IPdfLinkService` / `IPdfOutlineService` take `byte[]
pdfBytes` per call, so both the PdfPig and the PDFium backends re-open the
document each call (`PdfDocument.Open` / PDFium `WithTextPage`). This is by
design and identical across backends — *not* something the new PdfPig code
regressed. The long-lived document only lives in `PdfPigSkiaPdfService` (render
path). If text/link extraction ever shows up hot, the fix is an interface
change (pass a cached handle), which is out of scope for a perf pass.

## Things that are already fine

- `RailNav` hot path: pure `Math.Min/Max` indexing, no LINQ/allocation.
- `ZoomAnimationController.Tick` (per-frame): no allocation; `Tick` measures
  ~1.6 µs against a 16.7 ms frame budget.
- Agent-API text-extraction "scaling" benchmark numbers (1k chars slower than
  20k) are JIT/warmup artifacts of the first sub-iteration, not non-linear
  scaling — the dedicated single-size benchmarks scale linearly.
