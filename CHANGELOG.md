# Changelog

## Unreleased

### Added

- **New package `RailReader.Core.Vlm.OpenAI`** containing the OpenAI-compatible `IVlmService` implementation (`OpenAIVlmClient`). Works against OpenAI proper and any compatible endpoint (Ollama, vLLM, LightOnOCR, …).
- **`IVlmService` interface** in `RailReader.Core` for VLM transcription. Slots into the existing provider-abstraction pattern; future Anthropic / Gemini backends would arrive as additional `RailReader.Core.Vlm.*` sibling packages.

### Changed

- **Breaking:** `RailReader.Core` no longer depends on the `OpenAI` NuGet package. Consumers that called `VlmService.DescribeBlockAsync(...)` or `VlmService.TestConnectionAsync(...)` must:
  1. Add a `PackageReference` to `RailReader.Core.Vlm.OpenAI`.
  2. Construct an `OpenAIVlmClient` (stateless — singleton-safe) and call the same-named instance methods on `IVlmService`.
- **Breaking:** `VlmService.Schemas` is no longer a dictionary of `BinaryData`. Replaced with `VlmService.GetSchema(BlockAction)` returning `(string FieldName, string Schema)`. Callers building OpenAI requests should wrap the schema in `BinaryData.FromString(...)` at the call site (already done inside `OpenAIVlmClient`).
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
