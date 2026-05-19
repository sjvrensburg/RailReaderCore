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

```bash
# Produce NuGet packages (output in dist/)
dotnet pack RailReaderCore.slnx -c Release -o dist/
```

## NuGet Publishing

All four projects produce NuGet packages sharing a single version set in `Directory.Build.props` (`VersionPrefix`). Package metadata (license, source link, deterministic builds) is centralized there.

**Publishing workflow:** tag-triggered via `.github/workflows/build.yml` using NuGet Trusted Publishing (OIDC — no stored API keys). Pushing any `v*` tag publishes real packages to nuget.org.

```bash
# 1. Bump VersionPrefix in Directory.Build.props
# 2. Add a section to CHANGELOG.md describing the changes
# 3. Commit, then:
git tag vX.Y.Z
git push origin main --tags
```

**One-time setup:** on nuget.org → Account → Trusted Publishing, add policy: owner=`sjvrensburg`, repo=`RailReaderCore`, workflow=`build.yml`, environment=`production`.

Before publishing, test against the desktop app (`railreader2`) since public API changes will break it until matching updates land there.

> ⚠ **Release actions require explicit user instruction.** Do not open PRs, merge, tag, or push tags on your own initiative — a tag push triggers a live NuGet publish. `CHANGELOG.md` is the source of truth for breaking changes and public-API promotions; consult it when reasoning about compatibility.

## Architecture

```
RailReaderCore.slnx
├── src/RailReader.Core/             ← Portable abstractions: models, controllers, interfaces. No PDFium, no ONNX, no filesystem, no non-system NuGet deps.
├── src/RailReader.Core.Pdfium/      ← Desktop PDFium impls of IPdfTextService/IPdfLinkService/IPdfOutlineService + filesystem-backed AppConfig/AnnotationService/ConsoleLogger/LayoutModelLocator
├── src/RailReader.Core.Analysis/    ← ONNX-backed ILayoutAnalyzer (PP-DocLayoutV3)
├── src/RailReader.Core.Vlm.OpenAI/  ← IVlmService impl for OpenAI-compatible chat-completions endpoints
├── src/RailReader.Renderer.Skia/    ← SkiaSharp rasterisation + IPdfServiceFactory (PDFium-backed)
└── tests/RailReader.Core.Tests/     ← xUnit headless tests
```

Reference graph (all arrows point downward):

```
Renderer.Skia    ──→ Core + Core.Pdfium
Core.Analysis    ──→ Core
Core.Pdfium      ──→ Core
Core.Vlm.OpenAI  ──→ Core
Core             ←── (root; no project refs, no non-system NuGet deps)
```

The deliberate split: `Core` is the only project a non-desktop consumer (Lite / mobile) needs to take. It has zero non-system NuGet deps. All native binaries (PDFium, ONNX) and provider SDKs (OpenAI) live in sibling projects, behind interfaces — additional VLM providers (Anthropic, Gemini, …) would slot in as further `Core.Vlm.*` packages.

### RailReader.Core (the portable layer)

UI-free, rendering-free, IO-free. Holds the orchestration surface (`DocumentController`, `DocumentState`), the data models, and the platform-boundary interfaces in `Services/I*.cs` (`IPdfService`, `IPdfTextService`, `IPdfLinkService`, `IPdfOutlineService`, `IPdfServiceFactory`, `IAnnotationStore`, `IRecentFilesStore`, `ILayoutAnalyzer`, `IVlmService`, `IMarkdownExportService`). Logging is injected once via `RailReaderLogging.Logger`; defaults to `NullLogger.Instance`.

`VlmService` (static, in Core) is the pure half of the VLM surface: prompt assembly, structured-output JSON schemas, layout-class → action routing, and the `BlockAction`/`PromptStyle` enums. The actual chat-completions call lives behind `IVlmService` in a provider-specific sibling package.

Settings flow through `CoreSettings` (an immutable record): the platform builds one from its own mutable config and pushes updates via `controller.OnConfigChanged(newSettings)`. Core never sees a mutable settings type and never writes anything.

### RailReader.Core.Pdfium (desktop PDFium + filesystem impls)

Everything that touches the local filesystem or the PDFium native binary lives here, behind the `Core` interfaces. Swap this project aside to retarget Core to a different runtime (web/mobile).

**Invariant:** every PDFium call must happen inside `lock (PdfiumGate.Lock)`. The P/Invoke surface (`PdfiumNative` / `PdfiumGate` / `PdfiumResolver`) enforces serialization across threads — there is no other way to get safe concurrent rendering.

`AppConfig` is the file-backed mutable config (`~/.config/railreader2/config.json`) and exposes `ToCoreSettings()` to bridge into Core. `RailReaderJsonContext` is the source-generated `JsonSerializerContext` for the only two types Core.Pdfium serialises (`AppConfig`, `AnnotationFile`).

### RailReader.Core.Analysis (ONNX-backed inference)

Single class: `LayoutAnalyzer` implements `ILayoutAnalyzer` against PP-DocLayoutV3 via `Microsoft.ML.OnnxRuntime`. Pipeline: letterbox the rasterized page to 800×800 → CHW float tensor → ONNX → `[N,7]` detections `[classId, confidence, xmin, ymin, xmax, ymax, readingOrder]` → confidence filter → NMS → reading-order sort. Never touches PDFium.

### RailReader.Core.Vlm.OpenAI (OpenAI-compatible VLM client)

Single class: `OpenAIVlmClient` implements `IVlmService` against any endpoint that speaks the OpenAI chat-completions protocol (OpenAI proper, Ollama, vLLM, LightOnOCR, …). Stateless — endpoint config is passed per call via `VlmEndpointConfig`. Prompts, schemas, and routing are pulled from the static `VlmService` helper in Core, so this package is purely a transport layer.

### RailReader.Renderer.Skia (SkiaSharp rasterisation)

The only project that imports both `Core` and `Core.Pdfium`. `SkiaPdfServiceFactory` is what desktop consumers wire into Core: it hands out a `SkiaPdfService` for rasterisation alongside the Core.Pdfium text/link/outline services. Also owns the Skia-side renderers (annotation, overlay, screenshot compositor, SkSL colour-effect shaders) and the 300-DPI block crop renderer used for VLM transcription.

### Wiring a consumer

- Set `RailReaderLogging.Logger` once at startup (or accept the `NullLogger` default).
- Construct `CoreSettings` from your config layer and pass it into `DocumentController`; on change, build a new `CoreSettings` and call `OnConfigChanged`.
- Supply `IPdfServiceFactory`, `IAnnotationStore`, `IRecentFilesStore`. Desktop wires `SkiaPdfServiceFactory` + `AnnotationService` + `AppConfig`; a Lite/mobile consumer would substitute its own.

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

## Relationship to RailReader2 (the desktop app)

The desktop app's `src/RailReader2/` (Avalonia UI shell), `src/RailReader2.Cli/` (headless CLI), and `src/RailReader.Export/` (Markdown export pipeline) currently consume these libraries via project references. When this repo is published as NuGet packages, the desktop app will switch to package references.

Until that switch happens, **structural changes in this repo must be tested against the desktop app**: changes to public APIs in Core, Core.Pdfium, Core.Analysis, or Renderer.Skia will break the desktop build until matching updates land there.
