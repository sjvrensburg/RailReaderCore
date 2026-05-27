# Changelog

## 0.8.0 — surface cleanup

Pure cleanup pass. No new functionality. Public-API change: one
type's access modifier widened (additive), one type removed
(subtractive).

### Removed

- **`TopDownReadingOrderResolver`** + its tests. Never wired into
  `AnalysisWorker`; the production resolver pair has always been
  `ModelOrderResolver` (for model-supplied order) and
  `XYCutPlusPlusResolver` (for unsupervised column-aware ordering).
  The top-down resolver was a reference implementation kept around
  for comparison and is no longer useful.
- **`RailReader.Core.Analysis.LightGbm`** package + the
  `LineTokenizer` / `LineToken` / `DocLayNetRoles` /
  `ITextLayoutAnalyzer` / `LightGbmLayoutAnalyzer` scaffolding.
  Held back from NuGet via `IsPackable=false` throughout and never
  carried a real feature-engineering implementation. The intended
  path (huridocs-style text-only DLA via two LightGBM models) is
  superseded by the strategic move to native-mobile + ONNX
  PP-DocLayoutV3, where the model size constraint that motivated
  the LightGBM path no longer applies. Removed the corresponding
  `lightgbm` target from `scripts/download-model.sh`.

### Changed

- **`LineDetector` is now `public`** (was `internal`). Lite v0.8.0
  had to re-port the char-cluster line-detection algorithm because
  the type was inaccessible from outside the assembly; the planned
  .NET MAUI mobile build will hit the same problem against the
  same algorithm. Promoting to `public` removes the duplication
  for any downstream consumer that needs in-block line detection.
  Pure additive surface change.

## 0.7.3

### Fixed

- **PdfPig word-gap detection failed on tightly-typeset academic PDFs.**
  The 0.7.2 fix used a geometry-based threshold (gap > 25 % of the
  local font size) which worked for the SkiaSharp synthetic test
  fixture but mis-fired on real journal text where inter-word gaps
  can be smaller than the threshold. Result: drag-to-copy from a
  Frontiers paper still produced
  `"Inrecentyears,timeseriesanalysisbasedondeeplearning…"`. Now uses
  PdfPig's purpose-built
  [`NearestNeighbourWordExtractor`](https://github.com/UglyToad/PdfPig/wiki/Document-Layout-Analysis#word-extraction)
  to identify word boundaries — clusters glyphs by mutual baseline
  distance with a sophisticated algorithm that handles justified
  typesetting, mixed fonts, and rotated text. Synthetic `' '` /
  `'\n'` chars are inserted between consecutive words; the boundary
  is checked on both sides to avoid duplicating whitespace that the
  underlying PDF already encodes (some PDFs emit explicit space
  characters in their content streams; the extractor preserves them
  inside `Word.Letters`).

## 0.7.2

### Fixed

- **PdfPig text extraction had no word spaces or line breaks.** PdfPig's
  `Page.Letters` collection contains only the visible glyphs from the
  content stream — PDF doesn't emit explicit space tokens, just
  horizontal gaps — so `PdfTextService.BuildPageText` was concatenating
  letters directly. Result: search for "hello world" never matched
  anything ("hello world" never appeared in the extracted text), and
  drag-to-copy produced glued-together gibberish like `"Besides,each
  typeofmethodisconstracks…"`. Now reconstructs word boundaries from
  geometry: a horizontal gap greater than 25 % of the local font size
  emits a synthetic `' '`; a mid-Y change between consecutive letters
  emits a synthetic `'\n'`. The synthetic chars get `CharBox`es
  positioned in the actual physical gap so
  `PageText.ExtractTextInRect` picks them up when a drag rect spans
  the surrounding glyphs. The threshold uses `Letter.PointSize` rather
  than glyph width because narrow chars (`i`, `r`, `t`) confused the
  width-based version, mis-firing word breaks inside words like
  "This" and "paragraph".

### Tests

- Two new regression tests: synthetic fixture extraction now contains
  `"Page 1 of 3"` (with spaces) and at least 2 `'\n'`s between the
  three lines. Total 378 → **380 / 380** pass.

## 0.7.1

Two perf/ergonomics patches for the PdfPig-backed renderer surfaced by
the first working RailReaderLite prototype.

### Changed (`RailReader.Renderer.PdfPigSkia`)

- **Cached `PdfDocument` instance.** `PdfPigSkiaPdfService` now opens
  the PDF once in its constructor and holds the parsed document for the
  lifetime of the instance. Previously each `RenderPage` /
  `RenderThumbnail` / `RenderPagePixmap` call re-opened the byte[] and
  re-parsed the entire file under the gate — visible in RailReaderLite
  as a multi-hundred-ms cost on every Next-page click. Now the
  per-render path is just `GetPageAsSKBitmap` against the cached
  `PdfDocument`, much cheaper.
- **`PdfPigSkiaPdfService(byte[])` constructor.** New overload that
  takes the PDF bytes directly. The file-path constructor now forwards
  to it. Lets web consumers feed picker-supplied bytes straight in
  without a temp-file round-trip — important for Avalonia.Browser where
  the picked file lives in the browser sandbox.
- **`IDisposable` on `PdfPigSkiaPdfService`.** The cached document is a
  managed resource; consumers swapping documents in a single process
  (e.g. the Lite "Open another PDF" flow) should `Dispose()` the
  previous instance to release it deterministically. `Dispose()` is
  idempotent.

### Changed (`RailReader.Core.PdfPig`)

- **`PdfOutlineService.Extract(PdfDocument)` overload.** Lets callers
  that already keep a parsed `PdfDocument` alive (like the renderer
  above) avoid re-parsing just to pull bookmarks. The existing
  `Extract(byte[])` overload forwards to it.

### Tests

- 3 new tests in `PdfPigSkiaPdfServiceTests`: byte[] and path
  constructors agree on every observable surface; multi-page rendering
  reuses the cached document without blowing up; `Dispose()` is
  idempotent. Total suite 375 → **378 / 378** pass.

## Unreleased

Scaffolding for [#14](https://github.com/sjvrensburg/RailReaderCore/issues/14) — a pure-managed text-only layout analyzer for Lite/web/mobile, modelled on
huridocs's fast pipeline (`pdftohtml` → tabular features → two LightGBM
models). **Held back from nuget.org** until the feature-engineering port
lands and is validated; tracking issue [#16](https://github.com/sjvrensburg/RailReaderCore/issues/16).

### Added (not yet published)

- **`RailReader.Core.Analysis.LightGbm`** — new project. `IsPackable=false`
  until the FE port resolves. Contains:
  - `ITextLayoutAnalyzer` — sibling interface to `ILayoutAnalyzer` for
    analyzers that work on the PDF text layer rather than a rasterised
    pixmap. Capabilities advertise `InputSize=0` to signal "skip
    rasterisation".
  - `DocLayNetRoles` — 11-class DocLayNet → `BlockRole` mapping. Same
    shape as the existing role classes in `Core.Analysis` for the ONNX
    analyzers.
  - `LineTokenizer.Tokenize(Page)` — clusters PdfPig `Letter`s into
    baseline lines via mid-Y clustering with a 1×-median-letter-height
    threshold; flips Y-up→Y-down to match Core's coordinate convention.
    Functional and tested.
  - `LightGbmLayoutAnalyzer(tokenTypeModelPath, paragraphModelPath)` —
    constructor loads both models via `LightGBMNet.Tree`
    (`OvaPredictor.FromFile` / `BinaryPredictor.FromFile`). `RunAnalysis`
    throws `NotImplementedException` until the FE port lands.
- **`scripts/download-model.sh lightgbm`** — pulls
  `token_type_lightgbm.model` and `paragraph_extraction_lightgbm.model`
  from `HURIDOCS/pdf-document-layout-analysis` on HuggingFace.
  **Model sizes are 106 MB + 17 MB ≈ 123 MB combined** — much heavier
  than initially estimated; honest sizing baseline before the FE port
  invests in WASM-load engineering.

### Shape assertions (load-bearing for the future FE port)

- token-type model `NumInputs == 1968` (context_size 4 × 8 pairs ×
  246 features/pair)
- paragraph model `NumInputs == 536`

### Tests

- 10 new tests across `LineTokenizerTests.cs` (5) and
  `LightGbmAnalyzerSmokeTests.cs` (5). Model-loading tests pass
  trivially when model files aren't downloaded — keeps CI green on a
  clean checkout. Suite total: 365 → **375 / 375**.

### Dependency

- `LightGBMNet.Tree` 1.0.* (MIT, netstandard2.0, zero NuGet deps,
  pure-managed). Parses LightGBM's text-dump format directly. Last
  release 2025-11-28. Not bound to native `lib_lightgbm`.

## 0.7.0

Adds the rasterisation half of the Lite/web/mobile story. Pairs the
parser-only `RailReader.Core.PdfPig` (0.6.0) with a new managed renderer
so a non-PDFium consumer now has the full `IPdfServiceFactory` surface.
Closes [#13](https://github.com/sjvrensburg/RailReaderCore/issues/13).

### Added

- **`RailReader.Renderer.PdfPigSkia`** — new project. Implements
  `IPdfService` (`PdfPigSkiaPdfService`) and `IPdfServiceFactory`
  (`PdfPigSkiaPdfServiceFactory`) by composing `Core.PdfPig`'s
  text/link/outline services with rasterisation through
  [`PdfPig.Rendering.Skia`](https://github.com/BobLd/PdfPig.Rendering.Skia)
  (BobLd, Apache-2.0). Mirrors `RailReader.Renderer.Skia`'s shape — same
  `IRenderedPage` (`SKBitmap` wrapper), same `RenderPage / RenderThumbnail
  / RenderPagePixmap` triple, same BGRA→RGB conversion outside the gate
  for the ONNX analyzer feed.
- **`PdfPigGate.Lock`** — internal serialisation gate for PdfPig calls,
  mirroring the discipline of `PdfiumGate.Lock` in `Core.Pdfium`.
  `PdfDocument` is not documented thread-safe, and `PdfPig.Rendering.Skia`
  may hold global state, so every code path that opens a document goes
  through the gate.
- **DPI handling.** `RenderPage(dpi)` maps the caller's DPI request to
  the renderer's scale factor (`dpi / 72`). `RenderThumbnail` and
  `RenderPagePixmap` use the existing FitPageToTarget convention so
  callers swap factories without touching call sites.

### Tests

- `tests/RailReader.Core.Tests/PdfPigSkiaPdfServiceTests.cs` — 6 new
  smoke tests covering page count, size, full-page render, thumbnail
  bounds, RGB pixmap shape + brightness sanity, and factory surface.
  Total suite: **365/365** pass (359 prior + 6 new).
- Same in-process isolation discipline as the parser tests — renderer
  tests don't mix PDFium and PdfPig in one test method (the test host
  crashes; see `PdfPigServiceTests` class summary).

### Known limitation

- `PdfPig.Rendering.Skia` 0.1.14.2 is self-declared pre-1.0. Pixel
  fidelity vs PDFium is not yet measured side-by-side; the desktop hot
  path keeps `Renderer.Skia`. Treat this renderer as the Lite/web/mobile
  path until validated otherwise.

## 0.6.0

Adds a fifth NuGet package — `RailReader.Core.PdfPig` — providing pure-
managed implementations of the three PDF parsing services
(`IPdfTextService`, `IPdfLinkService`, `IPdfOutlineService`) backed by
[UglyToad.PdfPig](https://github.com/UglyToad/PdfPig). This is the
unblock for the planned `RailReaderLite` (Avalonia.Browser) and future
mobile targets — neither can ship PDFium. Rasterisation (`IPdfService`)
stays out of scope for this package; it lives in a sibling renderer just
as PDFium's parsing (`Core.Pdfium`) sits next to `Renderer.Skia`.

### Added

- **`RailReader.Core.PdfPig`** — new project with three services:
  - `PdfTextService` — wraps `Page.Letters`, flips Y-up → Y-down using
    page height, emits one `CharBox` per character in the concatenated
    text (ligature glyphs whose `Letter.Value` is multi-char get one box
    per char, all pointing at the same `BoundingBox` — matches PDFium's
    per-codepoint behaviour so downstream indexing is identical).
  - `PdfLinkService` — walks `Page.GetAnnotations()`, filters to
    `AnnotationType.Link`, resolves `PdfAction` into `UriDestination` /
    `PageDestination(PageIndex = PageNumber - 1, …)`. Single pass covers
    both URI and internal-document links.
  - `PdfOutlineService` — walks `PdfDocument.TryGetBookmarks(...)`, maps
    `DocumentBookmarkNode.PageNumber` (1-indexed) to Core's
    `OutlineEntry.Page` (0-indexed). `ContainerBookmarkNode` (group-only)
    yields entries with null page, matching PDFium's output shape.
- **Cross-backend invariant.** Page indexes are 0-based on the Core
  interface; both PDFium and PdfPig implementations handle the conversion
  internally so consumers swap backends without index drift.

### Tests

- `tests/RailReader.Core.Tests/PdfPigServiceTests.cs` — 8 standalone
  tests against the synthetic `TestFixtures.GetTestPdfPath()` PDF.
  351 prior + 8 new = **359/359** pass.
- **Documented limitation:** in-process cross-backend tests
  (PDFium and PdfPig on the same byte[] in one xUnit test method) crash
  the test host — almost certainly a native/managed allocator interaction
  via PDFium's loaded shared library. Each backend works fine on its own;
  real consumers only pick one factory anyway. Convergence-style asserts
  should use golden files or separate test assemblies in the future.

## 0.5.1

Pure internal refactor of `RailReader.Core.Analysis` to remove duplication
introduced as the third `ILayoutAnalyzer` (PP-DocLayout-S) landed alongside
PP-DocLayoutV3 and Docling Heron in 0.5.0. No behavioural change; 351/351
tests pass; downstream consumers (railreader2) need no changes.

### Changed

- **`OnnxRuntimeInitializer`** (`RailReader.Core.Analysis`, internal) —
  extracts the ~40-line OnnxRuntime native-library preload that was copy-
  pasted into all three analyzers' static constructors. Each analyzer's
  static ctor is now a one-liner. Behaviour is byte-identical; the helper
  is idempotent via an `Interlocked` guard so the filesystem probes only
  run once per process.
- **`LayoutModelCapabilities.RoleForName(string)`** — promoted the
  class-name → `BlockRole` lookup from three identical static methods
  (`PPDocLayoutV3Roles`, `PPDocLayoutSRoles`, `DoclingHeronRoles`) to a
  single method on the capabilities record. The three per-class static
  `RoleForName` methods remain as one-line forwards for back-compat — no
  consumer-visible signature change.
- **`LayoutAnalyzer.TryBuildBlock(...)`** (internal) — extracts the
  bounds-clamp + min-size check + `LayoutBlock` construction shared by all
  three analyzers' detection-extraction loops. The 5-px minimum is now
  named `LayoutConstants.MinDetectionSizePx` instead of a literal.
- **`NormalizationConstants`** (`RailReader.Core.Analysis`, internal) —
  hoists `ImageNetMean`/`ImageNetStd` out of `PPDocLayoutSLayoutAnalyzer`
  so a future analyzer reusing those statistics doesn't redefine them.

## 0.5.0

Adds a third `ILayoutAnalyzer` implementation — PP-DocLayout-S — alongside the
existing PP-DocLayoutV3 and Docling Heron. PP-S is the lightweight option
(~4.7 MB ONNX vs V3's ~50 MB / Heron's ~164 MB), intended as the detector for
any future web (WASM/ORT-Web via `Avalonia.Browser`) or mobile build.

### Added

- **`PPDocLayoutSLayoutAnalyzer`** (`RailReader.Core.Analysis`) — third
  `ILayoutAnalyzer` implementation against PaddleOCR's PP-DocLayout-S
  (PicoDet/GFL, 23 classes, 480×480 model input). Lives alongside
  `LayoutAnalyzer` and `HeronLayoutAnalyzer`. ONNX I/O: **two** inputs
  (`image` float NCHW + `scale_factor` float [H/origH, W/origW] — note no
  `im_shape`, unlike V3) → `[M, 6]` detection tensor + scalar `num_dets`
  with NMS already baked into the graph at score_threshold=0.3. Boxes come
  back already in caller-pixmap coordinates because `scale_factor` makes the
  detection head un-resize them internally.
- **`PPDocLayoutSRoles`** (`RailReader.Core.Analysis`) — PP-DocLayout-S's
  23-class label list (from PP-S's `inference.yml`) with role mapping,
  exposed as `LayoutModelCapabilities` for callers wiring
  `PPDocLayoutSLayoutAnalyzer`. `ProvidesReadingOrder: false` — defaults
  pair PP-S with `XYCutPlusPlusResolver` (same path as Heron).
- **Decoupled InputSize / ModelInputSize** for PP-S: the analyzer advertises
  `Capabilities.InputSize = 1920` (rasterisation hint to the consumer) while
  internally running the model at 480×480. Rasterising straight to 480 loses
  bibliography rows and small text on academic content; downsizing from
  1920 inside the analyzer preserves recall without bloating the ONNX. This
  is the load-bearing lesson from the Python `raildla` prototype that ports
  the same detector.
- **`scripts/download-model.sh pps`** — PP-DocLayout-S download block, lands
  the model at `models/pp_doclayout_s.onnx`. Sourced from
  [`stefanj0/PP-DocLayout-S-ONNX`](https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX),
  a `paddle2onnx` export of the upstream Paddle-native checkpoint
  ([`PaddlePaddle/PP-DocLayout-S`](https://huggingface.co/PaddlePaddle/PP-DocLayout-S);
  no official ONNX exists upstream). The source URL is overridable via
  `PP_S_ONNX_URL`. `download-model.sh all` now downloads all three
  analyzers' models.

## 0.4.1

### Fixed

- **`HeronLayoutAnalyzer`** passed `orig_target_sizes` to the ONNX session as
  `[pxH, pxW]` (the PyTorch `RTDetrImageProcessor` convention), but this
  Heron ONNX export expects `[pxW, pxH]` — the baked-in post-processor
  multiplies normalised box coords by the tensor as `[W, H, W, H]`. The
  mismatch transposed the model's output: x-coords were scaled by the
  height target and clamped to pixmap width, while y-coords were scaled by
  the width target — producing detections only in roughly the top
  `pageW/pageH × 100%` of the page on portrait inputs (e.g. ~75% on A4, so
  the bottom of every academic-style page was silently dropped). Now passes
  `[pxW, pxH]`, matching the ONNX export's expectation. Verified end-to-end
  against the published 0.4.0 model on a two-column Pattern Recognition
  paper: detections now reach within ~30 pt of the page bottom on portrait
  pages, vs. ~240 pt short in 0.4.0.

## 0.4.0

Adds an algorithmic, column-aware reading-order resolver and an alternative
ONNX layout analyzer (Docling Heron) alongside the existing PP-DocLayoutV3.
Heron is downloadable separately via `scripts/download-model.sh heron` — it
is not bundled with the desktop installer.

### Added

- **`XYCutPlusPlusResolver`** (`RailReader.Core.Services`) — column-aware
  recursive XY-cut reading-order resolver. Pure geometry (no model, no IO).
  Inspired by Liu, Li & Wei (2025), *"XY-Cut++: Advanced Layout Ordering via
  Hierarchical Mask Mechanism"* ([arXiv:2504.10258](https://arxiv.org/abs/2504.10258));
  this implementation adopts the paper's geometric kernel only — column-gutter
  preference over horizontal cuts, with full-width spanning blocks (titles,
  full-width figures, page-bottom footnotes) handled implicitly via the
  straddler check. Designed for the two- and three-column academic layouts
  that `TopDownReadingOrderResolver` mis-orders.
- **`ReadingDirection` enum** (`RailReader.Core.Services`) — placeholder for
  future CJK / Arabic support. Only `LeftToRightTopToBottom` is implemented;
  other values throw `NotSupportedException` from the resolver ctor.
- **`HeronLayoutAnalyzer`** (`RailReader.Core.Analysis`) — second
  `ILayoutAnalyzer` implementation, against Docling's Heron model
  (RT-DETRv2, 17 classes, 640×640 input). Lives alongside `LayoutAnalyzer`;
  consumers pick which to instantiate. ONNX I/O: `images` (uint8 NCHW) +
  `orig_target_sizes` (int64) → `labels` / `boxes` / `scores`, with
  post-processing baked into the model graph.
- **`DoclingHeronRoles`** (`RailReader.Core.Analysis`) — Heron's 17-class
  label list with role mapping, exposed as `LayoutModelCapabilities` for
  callers wiring `HeronLayoutAnalyzer`. `ProvidesReadingOrder: false`.
- **`scripts/download-model.sh heron`** — Heron download block, model lands
  at `models/docling-layout-heron.onnx` (~164 MB, Apache-2.0 license).
  `download-model.sh ppdoc` and `download-model.sh all` are also accepted;
  the no-arg invocation still downloads PP-DocLayoutV3 (unchanged default).

### Changed

- **Behaviour change (non-breaking API):** `AnalysisWorker`'s default
  reading-order resolver for models that do *not* provide reading order is
  now `XYCutPlusPlusResolver` (was `TopDownReadingOrderResolver`). Callers
  that construct `AnalysisWorker` without an explicit `IReadingOrderResolver`
  against a non-ordering model will see a different read order on
  multi-column pages. Models that emit reading order (e.g. PP-DocLayoutV3)
  are unaffected — they still default to `ModelOrderResolver`.
  `TopDownReadingOrderResolver` is retained as a debug/fallback baseline.

## 0.3.1

### Added

- **`LayoutAnalyzer` constructor overload** accepting an optional `LayoutModelCapabilities` — lets consumers load a custom ONNX model with a different class table / role mapping (e.g. a PP-DocLayoutV3 variant fine-tuned on a domain-specific label space). The model must still follow PP-DocLayoutV3's I/O contract (`im_shape`/`image`/`scale_factor` inputs, `[N, 6+]` detection tensor with optional 7th reading-order column). Defaults to `PPDocLayoutV3Roles.Capabilities` so existing callers are unaffected.

## 0.3.0

Makes `RailReaderCore` document-layout-model agnostic. Apps can now bring any layout-detection model (PP-DocLayoutV3 is the only one shipped today, but YOLO-style detectors, DocLayout-YOLO, ORT-Web models, etc. all slot in) by implementing `ILayoutAnalyzer` and declaring a `LayoutModelCapabilities` that maps the model's native classes onto a portable `BlockRole` enum. Reading order is now a separately injectable concern — apps that pair a detection-only model with an algorithmic resolver (XY-cut, LayoutReader, …) supply an `IReadingOrderResolver` instead of relying on the model.

### Added

- **`BlockRole` enum** (`RailReader.Core.Models`) — semantic role of a layout block (`Text`, `Heading`, `Title`, `Caption`, `Aside`, `DisplayMath`, `InlineMath`, `Algorithm`, `Table`, `Figure`, `Chart`, `Header`, `Footer`, `PageNumber`, `Footnote`, `Reference`, `Decoration`, `Unknown`). Core branches on this instead of the model-specific `ClassId`.
- **`LayoutModelCapabilities` + `LayoutClassDescriptor` records** — each `ILayoutAnalyzer` declares its `InputSize`, full class table (id → name → `BlockRole`), and whether the model provides reading order.
- **`IReadingOrderResolver` interface** with two built-in implementations: `ModelOrderResolver` (trusts the analyzer's order hints) and `TopDownReadingOrderResolver` (Y-then-X fallback). Apps can supply their own resolver (e.g. XY-cut) via the new optional ctor arg on `AnalysisWorker` / `DocumentController.InitializeWorker`.
- **`BlockPostProcessor`** (Core) — vertical-overlap trimming + line detection, moved out of `LayoutAnalyzer` so the same pipeline runs regardless of model.
- **`DefaultRoleSets`** (Core) — default `Navigable` and `Centering` role sets used by `CoreSettings`.
- **`PPDocLayoutV3Roles`** (Core.Analysis) — PP-DocLayoutV3's 25-class label list with role mapping, exposed as `LayoutModelCapabilities` for callers wiring `LayoutAnalyzer`.

### Changed

- **Breaking:** `ILayoutAnalyzer` gains a required `LayoutModelCapabilities Capabilities { get; }`. Each `RunAnalysis` call must stamp `LayoutBlock.Role` on every returned block.
- **Breaking:** `LayoutBlock` gains `BlockRole Role`. `ClassId` is retained for diagnostics only — Core no longer branches on it.
- **Breaking:** `CoreSettings.NavigableClasses` / `CenteringClasses` (`IReadOnlySet<int>`) → `NavigableRoles` / `CenteringRoles` (`IReadOnlySet<BlockRole>`).
- **Breaking:** `DocumentController.InitializeWorker(Func<ILayoutAnalyzer>)` → `InitializeWorker(LayoutModelCapabilities, Func<ILayoutAnalyzer>, IReadingOrderResolver?)`. Capabilities must be passed eagerly so `AnalysisWorker.InputSize` is readable before the analyzer finishes loading.
- **Breaking:** `AnalysisWorker` ctor takes `(LayoutModelCapabilities, Func<ILayoutAnalyzer>, IThreadMarshaller, IReadingOrderResolver?, ILogger?)`. New `Capabilities` and `InputSize` properties. The worker pipeline is now `analyzer → reading-order resolver → BlockPostProcessor`.
- **Breaking:** `DocumentState.SubmitAnalysis` / `GoToPage` / `ReapplyNavigableClasses` rename their `IReadOnlySet<int> navigableClasses` parameter to `IReadOnlySet<BlockRole> navigableRoles` (and `ReapplyNavigableClasses` → `ReapplyNavigableRoles`).
- **Breaking:** `PeekEntry.ClassId` → `Role` (`BlockRole`).
- **Breaking:** `VlmService.GetBlockAction(int classId)` → `GetBlockAction(BlockRole role)`.
- **Breaking:** `RailNav.SetAnalysis(PageAnalysis, IReadOnlySet<int>)` → `SetAnalysis(PageAnalysis, IReadOnlySet<BlockRole>)`.
- `AppConfig` settings rename to `NavigableRoles` / `CenteringRoles` and persist `BlockRole` enum names (`"Text"`, `"DisplayMath"`, …) instead of PP-DocLayoutV3 class names. The schema version bumps from 1 to 2; on first load any v0/v1 config is migrated automatically and re-saved (PP-DocLayoutV3 names translated via the built-in table) so user customisations are preserved.
- `OverlayRenderer.DrawDebugOverlay` labels blocks with `block.Role` instead of looking up a model-specific class name table.

### Removed

- **Breaking:** `LayoutConstants.InputSize` (moved to `LayoutModelCapabilities.InputSize`).
- **Breaking:** All PP-DocLayoutV3-specific constants from `LayoutConstants`: `LayoutClasses`, `ClassAlgorithm`, `ClassChart`, `ClassDisplayFormula`, `ClassDocTitle`, `ClassFooterImage`, `ClassFormulaNumber`, `ClassHeaderImage`, `ClassImage`, `ClassInlineFormula`, `ClassParagraphTitle`, `ClassTable`, `FigureClasses`, `TableClasses`, `EquationClasses`, `DefaultNavigableClasses()`, `DefaultCenteringClasses()`, `ClassNameToIndex`, `GetClassName`. `LayoutConstants` now holds only model-agnostic tuning (`ConfidenceThreshold`, `NmsIouThreshold`, `DarkLuminanceThreshold`, `DensityThresholdFraction`, `MinLineHeightPx`).
- **Breaking:** `PeekIndexBuilder.EquationClasses` static field (was a transitional public surface; bucketing now happens internally via `BlockRole`).

## 0.2.0

### Added

- **New package `RailReader.Core.Vlm.OpenAI`** containing the OpenAI-compatible `IVlmService` implementation (`OpenAIVlmClient`). Works against OpenAI proper and any compatible endpoint (Ollama, vLLM, LightOnOCR, …).
- **`IVlmService` interface** in `RailReader.Core` for VLM transcription. Slots into the existing provider-abstraction pattern; future Anthropic / Gemini backends would arrive as additional `RailReader.Core.Vlm.*` sibling packages.

### Changed

- **Breaking:** `RailReader.Core` no longer depends on the `OpenAI` NuGet package. Consumers that called `VlmService.DescribeBlockAsync(...)` or `VlmService.TestConnectionAsync(...)` must:
  1. Add a `PackageReference` to `RailReader.Core.Vlm.OpenAI`.
  2. Construct an `OpenAIVlmClient` (stateless — singleton-safe) and call the same-named instance methods on `IVlmService`.
- **Breaking:** `VlmService.Schemas` is no longer a dictionary of `BinaryData`. Replaced with `VlmService.GetSchema(BlockAction)` returning `(string FieldName, string Schema)`. Callers building OpenAI requests should wrap the schema in `BinaryData.FromString(...)` at the call site (already done inside `OpenAIVlmClient`).
- **Breaking:** `IVlmService.TestConnectionAsync` takes `VlmEndpointConfig` instead of `CoreSettings` (the former static `VlmService.TestConnectionAsync(CoreSettings, …)` shape). Call sites can adapt with `VlmEndpointConfig.FromCoreSettings(settings)`, matching `DescribeBlockAsync`'s existing parameter shape.
- Pure helpers on `VlmService` (`GetPrompt`, `GetBlockAction`, `ExtractSchemaField`) and the nested `BlockAction` / `PromptStyle` enums and `VlmEndpointConfig` / `VlmResult` records remain in Core, unchanged. Source references like `VlmService.PromptStyle.Instruction` still compile.

### Removed

- `VlmService.DescribeBlockAsync` and `VlmService.TestConnectionAsync` static methods. The corresponding `IVlmService` methods on `OpenAIVlmClient` replace them.
- `OpenAI` package reference from `RailReader.Core`. Core's runtime closure is now `Microsoft.NET.ILLink.Tasks` only (auto-referenced for AOT).

## 0.1.3

### Added

- Shared package README for `RailReader.Core.Pdfium`, `RailReader.Core.Analysis`, and `RailReader.Renderer.Skia` (previously only `RailReader.Core` had one).

## 0.1.2

### Changed

- Removed `InternalsVisibleTo` entries for downstream consumer assemblies (`RailReader2`, `RailReader2.Cli`, `RailReader.Export`, `RailReader.Export.Tests`) from all four packages. These were a transitional measure in 0.1.1 — the public API promotions in that release are sufficient now that railreader2 has been verified against the published packages.

### Added

- `CHANGELOG.md`.

## 0.1.1

### Added

- `InternalsVisibleTo` entries for downstream consumer assemblies (`RailReader2`, `RailReader2.Cli`, `RailReader.Export`, `RailReader.Export.Tests`) in all four packages, enabling the switch from `ProjectReference` to NuGet `PackageReference` in the railreader2 desktop app.

### Changed

- **Breaking:** Removed per-service `static ILogger Logger` properties from `AppConfig`, `AnnotationService`, `CleanupService`, `PdfTextService`, `PdfOutlineService`, `PdfLinkService`, `LayoutAnalyzer`, and `SkiaPdfService`. All logging now routes through `RailReaderLogging.Logger` directly. Downstream consumers that previously wired individual loggers must replace those assignments with a single `RailReaderLogging.Logger = logger;` call.

### Public API promotions

The following previously `internal` types and members are now `public`:

- `RadialMenuGeometry` (RailReader.Core)
- `ColorUtils` (RailReader.Core)
- `OutlineBreadcrumb` (RailReader.Core)
- `PeekIndexBuilder` + `PeekIndexBuilder.EquationClasses` (RailReader.Core)
- `AnnotationGeometry` (RailReader.Core)
- `OverlayRenderer` paint factory methods: `GetDimPaint`, `GetRevealPaint`, `GetOutlinePaint`, `GetLinePaint`, `GetHighlightPaint`, `GetActivePaint`, `GetDebugFillPaint`, `GetDebugStrokePaint`, `GetDebugBgPaint`, `GetDebugTextPaint`, `GetDebugFont` (RailReader.Renderer.Skia)
