# RailReaderCore

Portable libraries powering [RailReader2](https://github.com/sjvrensburg/railreader2) and intended for reuse by future companion apps (web, mobile). Distributed as a set of [NuGet packages](https://www.nuget.org/packages?q=RailReader).

## Packages

| Project | Purpose | External deps |
|---|---|---|
| `RailReader.Core` | Portable abstractions, models, controllers, rail-navigation, line detection, search, annotations, reading-order resolvers | none (system only) |
| `RailReader.Core.Pdfium` | Desktop PDFium implementations of the Core interfaces + filesystem-backed `AppConfig` / `AnnotationService` / `ConsoleLogger` / `LayoutModelLocator` | PDFium native libraries |
| `RailReader.Core.Analysis` | ONNX-backed `ILayoutAnalyzer` implementations (PP-DocLayoutV3, PP-DocLayout-S, Docling Heron) | `Microsoft.ML.OnnxRuntime` |
| `RailReader.Core.Vlm.OpenAI` | `IVlmService` for OpenAI-compatible chat-completions endpoints (OpenAI, Ollama, vLLM, LightOnOCR, …) | `OpenAI` |
| `RailReader.Core.Ocr.RapidOcr` | `IOcrService` for pages with no text layer, via PaddleOCR PP-OCR ONNX models | `RapidOcrNet`, `SkiaSharp` |
| `RailReader.Renderer.Skia` | SkiaSharp rasterisation + `IPdfServiceFactory` that desktop consumers wire into Core | `SkiaSharp`, `PDFtoImage` |

## Reference graph

```
RailReader.Core              ← no native deps, no IO
  ├─ Core.Pdfium             → Core
  ├─ Core.Analysis           → Core
  ├─ Core.Vlm.OpenAI         → Core
  ├─ Core.Ocr.RapidOcr       → Core
  └─ Renderer.Skia           → Core + Core.Pdfium
```

A future Lite (web/WASM) app would consume `RailReader.Core` only and substitute its own `IPdfService` / `IPdfTextService` / `ILayoutAnalyzer` / `IVlmService` implementations (e.g. PDF.js, ORT-Web, browser fetch).

## Model-agnostic layout pipeline

Core defines two seams that let any layout-detection model drive RailReader:

- **`ILayoutAnalyzer`** — wraps a specific ONNX model and declares its class table + input size + whether it provides reading order via `LayoutModelCapabilities`. Each detection is stamped with a portable `BlockRole`; Core never branches on the model-specific class id.
- **`IReadingOrderResolver`** — assigns 0..N-1 reading order to detected blocks. Two built-ins ship:
  - `ModelOrderResolver` (trusts the analyzer's order hints — default pick for models with `ProvidesReadingOrder=true`)
  - `XYCutPlusPlusResolver` (column-aware recursive XY-cut, default for non-ordering models — handles two/three-column papers and full-width spanners correctly)

Core itself ships a fourth, model-free analyzer: **`TextLayoutAnalyzer`** recovers blocks from
the text layer by bottom-up grouping (Docstrum's idea — thresholds taken from the page's own
nearest-neighbour spacing rather than constants), with no ONNX runtime, no weights, and no native
dependency. It gives a web or low-end mobile build a rail pipeline out of the box and any build a
fallback when the model is missing. Every block is `BlockRole.Text` — with no model there is no
class signal, so role-keyed features (table-row reading, cell navigation, figure framing,
auto-scroll stop classes) do nothing — and it needs a text layer, so a scan yields nothing unless
OCR supplied one.

`Core.Analysis` ships three model-backed analyzers:

| Analyzer | Model | Input | Reading order | Notes |
|---|---|---|---|---|
| `LayoutAnalyzer` | PP-DocLayoutV3 (RT-DETR) | 800×800 letterbox | model-provided | 25 classes; the existing default for the desktop app (~50 MB) |
| `PPDocLayoutSLayoutAnalyzer` | PP-DocLayout-S (PicoDet/GFL) | 1920 longest-edge raster → 480×480 internal | XYCut++ | 23 classes; lightweight (~4.7 MB) — intended detector for future web (WASM/ORT-Web) and mobile builds |
| `HeronLayoutAnalyzer` | Docling Heron (RT-DETRv2) | 640×640 resize | XYCut++ | 17 classes; broader category space (code, forms, key-value regions) (~164 MB) |

Additional analyzers slot in as further `Core.Analysis` types or as separate sibling packages — the existing three are the template.

## OCR for pages with no text layer

Almost everything RailReader does with text — char-clustering line detection, the
reading-order tie-break, table row and cell detection, search, Markdown export, VLM prompt
assembly — reads a `PageText`. A scanned PDF has none, so those paths degrade to
pixel-projection line detection and no text at all. `RailReader.Core.Ocr.RapidOcr` fills the
gap by *synthesising* a `PageText`, which upgrades all of them at once.

Wire it in alongside the layout analyzer and pick how much work to do:

```csharp
controller.InitializeWorker(
    capabilities, analyzerFactory,
    ocrServiceFactory: () => new RapidOcrService(),
    ocrMode: OcrMode.Full);

controller.OcrMode = OcrMode.Lines;   // changeable at any time
```

| Mode | Cost | What a scanned page gets |
|---|---|---|
| `Off` (default) | none | pixel-projection lines, no text |
| `Lines` | detection pass only | real line geometry for rail mode |
| `Full` | detection + per-line recognition | text, per-character boxes, table cells, search, export |

OCR runs on the analysis worker's thread, reusing the pixmap already rendered for the layout
model — so it costs no extra rasterisation, but its accuracy tracks that model's input size.
At the 800 px PP-DocLayoutV3 and Heron ask for, body text is only a few pixels tall; pair
`Full` with PP-DocLayout-S (1920 px) for usable transcription. Pages that already have a text
layer never invoke OCR, and a failure to load or run it leaves layout analysis working
(`AnalysisWorker.OcrStartupError` records why).

The PP-OCRv5 Latin models (~14 MB) arrive with the `RapidOcrNet` package and are copied
beside your binaries automatically on build and publish. `OcrModelLocator` also probes the
app directory, `$APPDIR`, the user data directory and the working directory, so
hand-installed models — including the smaller PP-OCRv6 sets — are picked up too.

### Skew

A page that went through a sheet feeder is routinely a fraction of a degree off square, and line
grouping is blind to that: glyphs are sorted and clustered by vertical position, so once a line
drifts further across the column than the gap to its neighbour, adjacent printed lines interleave
and merge. On ordinary book text that happens below 1°.

`CoreSettings.DeskewOcrLines` (on by default) corrects it, reusing a measurement the OCR detector
already makes — the rotated rectangle it fits to each text line. Angles are aggregated across the
page by length-weighted median, capped at ±5°, and gated so an unconfident page falls back to
exactly zero correction. The angle is applied as a shear **inside line grouping only**: no pixels
are rotated and no rotated geometry leaves the pipeline, so glyph boxes, annotations, search and
rail framing are untouched.

It needs OCR — a page with its own text layer was never skewed, and with `OcrMode.Off` there is
nothing to estimate from. `tools/deskew-probe` reports the recovered angle and its effect per
page.

## Optional backend capabilities

Some backends can do more than the core interfaces require. Rather than widen those interfaces
(and every consumer's wiring) for something only one backend supports, Core discovers the extra
by casting the service it was given:

- **`IPdfRulingService`** — a page's vector ruling lines. Both backends implement it
  (`Core.PdfPig` via `page.Paths`, `Core.Pdfium` via the page object list, descending into form
  XObjects), so tables get their column grid from the exact lines the producer drew rather than
  from dark pixel runs in the analysis pixmap — and a ruled table's *rows* come from its
  horizontal rules, so a cell whose text wraps is one row rather than two.

Nothing needs enabling: wire the services as usual and the capability is used if present. A
wrapper around a service must forward these interfaces or it will silently hide the capability —
see `GatedPdfPigTextService`.

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
