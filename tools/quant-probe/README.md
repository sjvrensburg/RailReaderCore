# quant-probe — layout-analyzer performance & INT8 quantization study

Throwaway investigation tooling (not shipped, not in the solution). Documents
the perf characteristics of the three ONNX layout analyzers and whether INT8
quantization is viable on CPU. Companion to `tools/PerfHarness/`.

All numbers below were measured on a **Dell Precision 5570 / i7-12700H** (20
logical cores, `intel_pstate` active, **`avx_vnni` present** → real INT8 fast
path), .NET 10 / ONNX Runtime 1.24 CPU EP, on real academic PDFs from
`~/RailDLA/pdfs`.

> ⚠ **Measurement caveat:** this box idles cores at 400 MHz and ramps under
> load. Absolute ms/page is only trustworthy on a quiet box after warm-up;
> early "cold" runs in this study were inflated up to ~7×. For A/B, use paired
> interleaving (see `PerfHarness --analyzer duel`). For quant, the timings here
> were taken on a quiet box.

## Headline result

**Adopt Heron with backbone-only INT8 quantization.**

| Model / recipe | ms/page | Faithfulness vs FP32 | Status |
|---|--:|---|---|
| FP32 PP-DocLayoutV3 (current default) | ~468 | — | baseline |
| INT8 V3 (any recipe) | — | — | ❌ won't load |
| FP32 Heron (RT-DETRv2) | ~580–630 | — | slower than V3 |
| INT8 Heron, full-graph (per-channel or per-tensor) | "fast" | broken (0 detections) | ❌ |
| INT8 Heron, +encoder-MatMul | ~209 (2.6×) | recall 0.738 | ❌ worse |
| **INT8 Heron, backbone-only (Conv)** | **~215 (2.95×)** | **recall 0.990, IoU 0.984** | ✅ **winner** |

Backbone-only INT8 Heron is **~2.2× faster than the FP32 V3 in production today**,
at near-lossless quality. Validated numerically on 88 held-out pages and
visually on hard math-heavy / ultra-dense pages (see `overlays/`).

## Why V3 can't quantize but Heron can

- **V3** bakes dynamic-shape ops (`Range`/`Where`/`ScatterND`/`GatherND`) into
  the graph. These break ORT's strict symbolic shape inference in
  `quant_pre_process`; the `skip_symbolic` fallback then leaves `QLinearAdd`
  nodes with non-scalar scale/zero-point and **ORT refuses to load**
  (`Scale and Zero-point must be a scalar`).
- **Heron** has none of those ops. Its only quant-hostile content is the
  RT-DETR decoder (deformable attention `GridSample`×18 + an `anchors_scale`
  constant). Quantizing the decoder makes two scales go `Inf`, collapsing all
  detection scores → zero detections. **Leaving the decoder in FP32 and
  quantizing only the 85 backbone Convs avoids this entirely** — and that's
  where ~all the compute (and the speedup) lives anyway.

## Where the time goes (FP32 V3, clean)

Inference is ~91% of wall (~461 of ~505 ms/page). Rasterize/text-extract/
pre/post are all noise; managed allocation is ~6 MB/page with zero gen-2 GCs.
So model inference is the *only* meaningful lever — pipeline overlap was
measured at just 1.03× and isn't worth the threading complexity.

## Lessons (corrections made during the study)

1. **Dynamic INT8 is the wrong tool for conv-heavy models** — it emits
   `ConvInteger` (reference kernel, no VNNI) and made both models *slower*.
   Static QDQ (`QLinearConv`) is what hits the VNNI path.
2. **Always validate output validity before trusting a quant speedup.** An
   early "3.01× speedup" was measured on a model emitting NaN/garbage — fast
   because it computed nothing. The gate now checks finite outputs + real
   detections + FP32 agreement before reporting any speed number.
3. **The search converged on backbone-only.** Quantizing any MatMul (encoder or
   decoder) either broke the model or hurt recall, with no speed gain. Further
   speedups need a different class of lever (GPU EP, QAT, or a re-exported /
   smaller model) — not more ORT-recipe tuning.

## Open caveats (before adopting in production)

- **Faithfulness is measured vs FP32 Heron**, not labelled ground truth (no
  COCO/annotations on disk). It proves INT8 ≈ the model you'd otherwise ship,
  not an independent mAP. The visual overlays mitigate this.
- **Measured in Python ORT, not the .NET `HeronLayoutAnalyzer`.** Same native
  runtime, but the end-to-end .NET path is unconfirmed. Natural next step:
  load `out/heron_backbone_int8.onnx` through the real analyzer
  (the `ConfigureSession` seam added to the analyzers supports EP/threading
  tuning if needed).

## Files

| File | Purpose |
|---|---|
| `op_census.py` | per-model op-type histogram + quant-hostile op scan |
| `quantize_and_time.py` | dynamic INT8 attempt (the dead-end) |
| `static_quant_speed.py` | static full-graph QDQ speed gate (broken-model finding) |
| `compare_accuracy.py` | full-graph FP32-vs-INT8 detection agreement (synthetic→real calib) |
| `diagnose_int8.py` | NaN/Inf vs collapsed-score diagnosis of the broken graph |
| `backbone_quant.py` | **the winner**: Conv-only quant + full validity/accuracy/speed gate |
| `pertensor_quant.py` | per-tensor variants (ruled out) |
| `mixed_recipe.py` | Conv+encoder-MatMul (ruled out) |
| `render_overlays.py` | FP32-vs-INT8 side-by-side detection overlays → `overlays/` |

Bulky/generated artifacts (`rasters/`, `out/`, `*.onnx`, `*-result.txt`) are
gitignored. Rasters are produced by `PerfHarness --analyzer dump-rasters`
(real PDFium renders, so calibration matches production).

## Reproduce the winner

```bash
# 1. dump real page rasters via the .NET PDFium renderer
dotnet tools/PerfHarness/bin/Release/net10.0/PerfHarness.dll --analyzer dump-rasters
# 2. quantize backbone-only + validate (validity + 88-page agreement + speed)
python3 tools/quant-probe/backbone_quant.py
# 3. eyeball FP32 vs INT8 detections
python3 tools/quant-probe/render_overlays.py   # writes overlays/*.png
```
