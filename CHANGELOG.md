# Changelog

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
