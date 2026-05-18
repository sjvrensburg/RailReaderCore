# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**RailReaderCore** is a set of .NET 10 libraries extracted from [RailReader2](https://github.com/sjvrensburg/railreader2) — a desktop PDF viewer with AI-guided "rail reading" for high-magnification viewing. This repository is the source-of-truth for the portable parts of that codebase, packaged so the same business logic can power desktop, web (planned: RailReader Lite via Avalonia.Browser), and mobile apps.

- **License**: MIT
- **Remote**: `https://github.com/sjvrensburg/RailReaderCore.git`
- **Default branch**: `main`
- **Parent project**: `https://github.com/sjvrensburg/railreader2` (the Avalonia desktop app consumes these libraries via project reference today; will switch to NuGet once published)

## Build & Develop

Prerequisites: .NET 10 SDK.

```bash
# Build all four projects + tests
dotnet build RailReaderCore.slnx -c Release

# Run all tests
dotnet test tests/RailReader.Core.Tests -c Release

# Run a specific test class or method
dotnet test tests/RailReader.Core.Tests --filter "ClassName=RailReader.Core.Tests.CameraTests"
dotnet test tests/RailReader.Core.Tests --filter "FullyQualifiedName~TestMethodName"

# Download PP-DocLayoutV3 ONNX model (only needed for RailReader.Core.Analysis consumers)
./scripts/download-model.sh
```

**Always use `-c Release`** — debug builds are significantly slower for the inference paths.

## Architecture

```
RailReaderCore.slnx
├── src/RailReader.Core/          ← Portable abstractions: models, controllers, interfaces. No PDFium, no ONNX, no filesystem. (Future NuGet)
├── src/RailReader.Core.Pdfium/   ← Desktop PDFium impls of IPdfTextService/IPdfLinkService/IPdfOutlineService + filesystem-backed AppConfig/AnnotationService/ConsoleLogger/LayoutModelLocator
├── src/RailReader.Core.Analysis/ ← ONNX-backed ILayoutAnalyzer (PP-DocLayoutV3)
├── src/RailReader.Renderer.Skia/ ← SkiaSharp rasterisation + IPdfServiceFactory (PDFium-backed)
└── tests/RailReader.Core.Tests/  ← xUnit headless tests
```

Reference graph (all arrows point downward):

```
Renderer.Skia ──→ Core + Core.Pdfium
Core.Analysis ──→ Core
Core.Pdfium  ──→ Core
Core          ←── (root; no project refs)
```

The deliberate split: `Core` is the only project a non-desktop consumer (Lite / mobile) needs to take. It pulls in only the OpenAI SDK NuGet (for `VlmService`). All native binaries (PDFium, ONNX) and filesystem code live in sibling projects, behind interfaces.

### RailReader.Core (the portable layer)

UI-free, rendering-free, IO-free. Key files:

- `DocumentController.cs` — headless controller facade (orchestration, animation tick loop, viewport). Takes `CoreSettings`, `IRecentFilesStore`, `IAnnotationStore`, `IPdfServiceFactory`. Delegates zoom animation to `ZoomAnimationController.cs`, auto-scroll to `AutoScrollController.cs`, annotation interaction to `AnnotationInteractionHandler.cs`, search to `Services/SearchService.cs`.
- `DocumentState.cs` — per-document state (PDF via `IPdfService`, camera, rail nav, analysis cache, annotations, bookmarks). Takes `CoreSettings`.
- `Models/CoreSettings.cs` — **immutable record** of runtime tuning values consumed by Core. The platform builds it from its own config persistence (e.g. `AppConfig.ToCoreSettings()`). When settings change, the platform constructs a new `CoreSettings` and pushes it via `controller.OnConfigChanged(newSettings)`.
- `Models/` — data models (`Annotations`, `BookmarkEntry`, `Camera`, `LayoutBlock`, `LineInfo`, `RectF`, `ColorRGBA`, `PdfLink`, `OutlineEntry`, `PageAnalysis`, `PageText`, `SearchMatch`, etc.).
- `Services/I*.cs` — every platform boundary: `IPdfService` (rasterisation), `IPdfTextService`, `IPdfLinkService`, `IPdfOutlineService`, `IPdfServiceFactory`, `IAnnotationStore`, `IRecentFilesStore`, `ILayoutAnalyzer`, `IMarkdownExportService`.
- `Services/RailNav.cs` (+ `.AutoScroll.cs` + `.Snap.cs`) — rail-mode state machine: snap, scroll, clamp, auto-scroll, jump mode.
- `Services/LineDetector.cs` — three-strategy line detection: atomic-class collapse for figures/tables/images → PDFium char-box clustering → pixel-projection fallback. Stepwise equations like `γ₁ = Cov(…)` keep per-line structure because `display_formula` is deliberately not atomic.
- `Services/AnalysisWorker.cs` — background inference thread (`Channel<T>` queue). Takes a `Func<ILayoutAnalyzer>` factory; the platform supplies the concrete analyzer.
- `Services/SearchService.cs` — full-text search with regex/case sensitivity, result grouping by page.
- `Services/VlmService.cs` — OpenAI-compatible vision-API client. Currently the only non-system NuGet dep of `Core` (the `OpenAI` package).
- `Services/AnnotationFileManager.cs` — reference-counted shared `AnnotationFile` instances per PDF path. Takes `IAnnotationStore` for IO.
- `ILogger.cs` — logging abstraction (`ILogger`, `NullLogger`). The concrete `ConsoleLogger` lives in `Core.Pdfium` because it writes to disk.

### RailReader.Core.Pdfium (desktop PDFium + filesystem impls)

Everything that touches the local filesystem or the PDFium native binary. Move this aside for web/mobile consumers and swap in alternative implementations of the same interfaces.

- `PdfTextService.cs` / `PdfLinkService.cs` / `PdfOutlineService.cs` — PDFium P/Invoke implementations of the Core interfaces.
- `PdfiumNative.cs` / `PdfiumGate.cs` / `PdfiumResolver.cs` — P/Invoke decls, serialization lock, native-library resolver. Every PDFium call must happen inside `lock (PdfiumGate.Lock)`.
- `AppConfig.cs` — file-backed mutable config (`~/.config/railreader2/config.json`), implements `IRecentFilesStore`, exposes `ToCoreSettings()`. Has its own mutation+save surface for UI binding; Core only ever sees the immutable `CoreSettings` snapshot.
- `AnnotationService.cs` — file IO for annotations, implements `IAnnotationStore`. Also retains static helpers (`MergeInto`, `ExportJson`, `ImportJson`, path utilities) that are stateless / pure.
- `CleanupService.cs` — removes stale logs, cache files, orphaned annotation files.
- `ConsoleLogger.cs` — file-backed `ILogger` writing to `session.log` under `AppConfig.ConfigDir`.
- `LayoutModelLocator.cs` — probes well-known disk paths for `PP-DocLayoutV3.onnx`.
- `RailReaderJsonContext.cs` — source-generated `JsonSerializerContext` for `AppConfig` + `AnnotationFile` (the only types Core.Pdfium serializes).

### RailReader.Core.Analysis (ONNX-backed inference)

- `LayoutAnalyzer.cs` — implements `ILayoutAnalyzer` via `Microsoft.ML.OnnxRuntime` + PP-DocLayoutV3. Letterboxes the rasterized page to 800×800 → CHW float tensor → ONNX → `[N,7]` detections `[classId, confidence, xmin, ymin, xmax, ymax, readingOrder]` → confidence filter → NMS → reading-order sort.

### RailReader.Renderer.Skia (SkiaSharp rasterisation)

- `SkiaPdfService.cs` / `SkiaPdfServiceFactory.cs` — PDFium-backed rasterisation. The factory hands out Core.Pdfium implementations of the text/link/outline interfaces.
- `SkiaRenderedPage.cs` — `IRenderedPage` wrapping `SKBitmap`.
- `AnnotationRenderer.cs` — Skia annotation drawing (highlight, freehand, text note, rectangle) with z-order sorting.
- `OverlayRenderer.cs` — rail overlay drawing (dim, block outline, line highlight).
- `ScreenshotCompositor.cs` — multi-layer composition to `SKBitmap`.
- `ColourEffectShaders.cs` — SkSL shader compilation (HighContrast, HighVisibility, Amber, Invert).
- `AnnotationExportService.cs` — annotation export to PDF via `SKDocument`.
- `BlockCropRenderer.cs` — renders block regions as PNG at 300 DPI for VLM transcription.
- `SkiaConversions.cs` — `ColorRGBA`↔`SKColor`, `RectF`↔`SKRect` helpers.

### Cross-Project Internals

Each project's `.csproj` declares `InternalsVisibleTo` for the projects that need to access its internals (mostly for `internal static ILogger Logger { get; set; }` properties that the entry-point sets at startup). When wiring a new consumer of these libraries:

- Set `PdfTextService.Logger`, `PdfLinkService.Logger`, `PdfOutlineService.Logger`, `AnnotationService.Logger`, `AppConfig.Logger`, `CleanupService.Logger`, `LayoutAnalyzer.Logger`, `SkiaPdfService.Logger` at startup (or accept the `NullLogger` default).

## Key Concepts

### Settings flow

```
[platform AppConfig (mutable, file-backed)]
       │   ToCoreSettings()
       ▼
[CoreSettings record (immutable snapshot)]
       │   constructor / OnConfigChanged
       ▼
[DocumentController, DocumentState, RailNav, AutoScrollController]
```

When the user changes a setting in the UI, the UI mutates `AppConfig` directly, rebuilds a fresh `CoreSettings` via `appConfig.ToCoreSettings()`, calls `controller.OnConfigChanged(newSettings)` to propagate, then `appConfig.Save()` to persist. Core never sees a mutable settings type and never writes anything.

### Line detection

`LineDetector.DetectLines(block, charBoxes, rgbBytes, …)` applies three strategies:

1. **Atomic-class collapse** — pure-visual blocks (`chart`, `image`, `header_image`, `footer_image`, `table`) become a single line spanning the full block. Equations and algorithms are deliberately *not* atomic — stepwise derivations read line-by-line.
2. **PDFium char-box clustering** — when `IPdfTextService.ExtractPageText` returned per-character bounding boxes, cluster them by mid-Y with a 1.0× median-char-height split threshold. Subscripts and superscripts stay on their parent line; line height spans from cluster `min-top` to `max-bottom` so ascenders/descenders aren't clipped.
3. **Pixel projection** — fallback for scanned PDFs with no text layer.

`DocumentState`'s three analysis-submission paths (`SubmitAnalysis`, `SubmitPendingLookahead`, `SubmitBackgroundAnalysis`) extract `PageText` alongside the page pixmap and pass `CharBoxes` to the worker — char clustering is the load-bearing strategy for math-heavy content.

### Rail Mode

Activates above `CoreSettings.RailZoomThreshold` when analysis is available. Locks to detected text blocks, advances line-by-line with cubic ease-out snap. Sub-features: auto-scroll, jump mode, line focus blur, line highlight, named bookmarks. Free pan (Ctrl+drag) temporarily exits rail mode for unrestricted pan/zoom, snapping back on Ctrl release.

### Thread Safety

- **UI thread**: All controller mutations, keyboard/mouse, state building, PDFium calls.
- **Analysis Worker**: A dedicated thread reads from `Channel<AnalysisRequest>` and runs `ILayoutAnalyzer.RunAnalysis`. The ONNX implementation never touches PDFium. Results are pushed back via the result channel and consumed by the UI thread.
- **Thread pool**: `IPdfService.RenderPagePixmap()` can be called from `Task.Run()`. PDFium serialization is enforced by `lock (PdfiumGate.Lock)` inside every PDFium-touching call site.
- **Critical**: Never modify `DocumentState` from a background thread — use `IThreadMarshaller.Post()` to dispatch to the UI thread.

## Testing

Tests in `tests/RailReader.Core.Tests/` are xUnit. `TestFixtures.cs` generates synthetic PDFs at runtime via SkiaSharp's `SKDocument.CreatePdf` — that's why the test project references `Renderer.Skia` even though the tests themselves exercise Core, Core.Pdfium, and Core.Analysis surfaces.

Test categories: `DocumentControllerTests`, `CameraTests`, `RailNavTests`, `SearchServiceTests`, `AnnotationTests`, `AnnotationFileManagerTests`, `AnnotationInteractionHandlerTests`, `LineDetectionTests`, `LineDetectorTests`, `AutoScrollControllerTests`, `ZoomAnimationControllerTests`, `MarginCroppingTests`, `PdfLinkTests`.

## Relationship to RailReader2 (the desktop app)

The desktop app's `src/RailReader2/` (Avalonia UI shell), `src/RailReader2.Cli/` (headless CLI), and `src/RailReader.Export/` (Markdown export pipeline) currently consume these libraries via project references. When this repo is published as NuGet packages, the desktop app will switch to package references.

Until that switch happens, **structural changes in this repo must be tested against the desktop app**: changes to public APIs in Core, Core.Pdfium, Core.Analysis, or Renderer.Skia will break the desktop build until matching updates land there.
