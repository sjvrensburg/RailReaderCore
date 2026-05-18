# RailReaderCore

Portable libraries powering [RailReader2](https://github.com/sjvrensburg/railreader2) and intended for reuse by future companion apps (web, mobile). Distributed (planned) as a set of NuGet packages.

## Packages

| Project | Purpose | External deps |
|---|---|---|
| `RailReader.Core` | Portable abstractions, models, controllers, rail-navigation, line detection, search, annotations | `OpenAI` (for `VlmService`) |
| `RailReader.Core.Pdfium` | Desktop PDFium implementations of the Core interfaces + filesystem-backed `AppConfig` / `AnnotationService` / `ConsoleLogger` / `LayoutModelLocator` | PDFium native libraries |
| `RailReader.Core.Analysis` | ONNX-backed `ILayoutAnalyzer` (PP-DocLayoutV3) | `Microsoft.ML.OnnxRuntime` |
| `RailReader.Renderer.Skia` | SkiaSharp rasterisation + factory; provides the `IPdfServiceFactory` desktop consumers wire into Core | `SkiaSharp`, `PDFtoImage` |

## Reference graph

```
RailReader.Core              ← no native deps, no IO
  ├─ Core.Pdfium             → Core
  ├─ Core.Analysis           → Core
  └─ Renderer.Skia           → Core + Core.Pdfium
```

A future Lite (web/WASM) app would consume `RailReader.Core` only and substitute its own `IPdfService` / `IPdfTextService` / `ILayoutAnalyzer` implementations (e.g. PDF.js, ORT-Web).

## Build & test

```bash
dotnet build RailReaderCore.slnx -c Release
dotnet test tests/RailReader.Core.Tests -c Release
```

The PP-DocLayoutV3 ONNX model is needed only by `RailReader.Core.Analysis` consumers; download it via:

```bash
./scripts/download-model.sh
```

## License

MIT — see `LICENSE`.
