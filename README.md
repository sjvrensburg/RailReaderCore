# RailReaderCore

Portable libraries powering [RailReader2](https://github.com/sjvrensburg/railreader2) and intended for reuse by future companion apps (web, mobile). Distributed as a set of [NuGet packages](https://www.nuget.org/packages?q=RailReader).

## Packages

| Project | Purpose | External deps |
|---|---|---|
| `RailReader.Core` | Portable abstractions, models, controllers, rail-navigation, line detection, search, annotations, reading-order resolvers | none (system only) |
| `RailReader.Core.Pdfium` | Desktop PDFium implementations of the Core interfaces + filesystem-backed `AppConfig` / `AnnotationService` / `ConsoleLogger` / `LayoutModelLocator` | PDFium native libraries |
| `RailReader.Core.Analysis` | ONNX-backed `ILayoutAnalyzer` implementations (PP-DocLayoutV3, PP-DocLayout-S, Docling Heron) | `Microsoft.ML.OnnxRuntime` |
| `RailReader.Core.Vlm.OpenAI` | `IVlmService` for OpenAI-compatible chat-completions endpoints (OpenAI, Ollama, vLLM, LightOnOCR, …) | `OpenAI` |
| `RailReader.Renderer.Skia` | SkiaSharp rasterisation + `IPdfServiceFactory` that desktop consumers wire into Core | `SkiaSharp`, `PDFtoImage` |

## Reference graph

```
RailReader.Core              ← no native deps, no IO
  ├─ Core.Pdfium             → Core
  ├─ Core.Analysis           → Core
  ├─ Core.Vlm.OpenAI         → Core
  └─ Renderer.Skia           → Core + Core.Pdfium
```

A future Lite (web/WASM) app would consume `RailReader.Core` only and substitute its own `IPdfService` / `IPdfTextService` / `ILayoutAnalyzer` / `IVlmService` implementations (e.g. PDF.js, ORT-Web, browser fetch).

## Model-agnostic layout pipeline

Core defines two seams that let any layout-detection model drive RailReader:

- **`ILayoutAnalyzer`** — wraps a specific ONNX model and declares its class table + input size + whether it provides reading order via `LayoutModelCapabilities`. Each detection is stamped with a portable `BlockRole`; Core never branches on the model-specific class id.
- **`IReadingOrderResolver`** — assigns 0..N-1 reading order to detected blocks. Three built-ins ship:
  - `ModelOrderResolver` (trusts the analyzer's order hints — default pick for models with `ProvidesReadingOrder=true`)
  - `XYCutPlusPlusResolver` (column-aware recursive XY-cut, default for non-ordering models — handles two/three-column papers and full-width spanners correctly)
  - `TopDownReadingOrderResolver` (Y-then-X baseline, retained as a debug fallback)

`Core.Analysis` ships three analyzers today:

| Analyzer | Model | Input | Reading order | Notes |
|---|---|---|---|---|
| `LayoutAnalyzer` | PP-DocLayoutV3 (RT-DETR) | 800×800 letterbox | model-provided | 25 classes; the existing default for the desktop app (~50 MB) |
| `PPDocLayoutSLayoutAnalyzer` | PP-DocLayout-S (PicoDet/GFL) | 1920 longest-edge raster → 480×480 internal | XYCut++ | 23 classes; lightweight (~4.7 MB) — intended detector for future web (WASM/ORT-Web) and mobile builds |
| `HeronLayoutAnalyzer` | Docling Heron (RT-DETRv2) | 640×640 resize | XYCut++ | 17 classes; broader category space (code, forms, key-value regions) (~164 MB) |

Additional analyzers slot in as further `Core.Analysis` types or as separate sibling packages — the existing three are the template.

## Build & test

```bash
dotnet build RailReaderCore.slnx -c Release
dotnet test tests/RailReader.Core.Tests -c Release
```

Always use `-c Release` — debug builds are significantly slower on the inference paths.

## Models

`Core.Analysis` consumers need at least one ONNX layout-detection model on disk:

```bash
./scripts/download-model.sh           # default — PP-DocLayoutV3 (~50 MB, Apache-2.0)
./scripts/download-model.sh pps       # PP-DocLayout-S (~4.7 MB, Apache-2.0; lightweight)
./scripts/download-model.sh heron     # Docling Heron (~164 MB, Apache-2.0)
./scripts/download-model.sh all       # all three
```

PP-DocLayout-S is sourced from [`stefanj0/PP-DocLayout-S-ONNX`](https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX) — a `paddle2onnx` export of the upstream [`PaddlePaddle/PP-DocLayout-S`](https://huggingface.co/PaddlePaddle/PP-DocLayout-S) checkpoint (no official ONNX exists upstream). Override the source by setting `PP_S_ONNX_URL` before running the script.

Files land in `./models/`. The model search order on disk is defined by `Core.Pdfium`'s `LayoutModelLocator` (it walks several well-known locations relative to `AppContext.BaseDirectory` and `AppConfig.ConfigDir`).

## License

MIT — see `LICENSE`.
