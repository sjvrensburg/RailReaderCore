# onnx-fp16-export

> ⚠ **Heron FP16 GPU inference is confirmed NOT SAFE to use (2026-08-28) — this
> reproduces real field reports of frequent rail-reading misses.** PP-DocLayoutV3 FP16
> shows zero *detection-level* misses on the tested corpus, but its GPU kernel activations
> diverge from CPU just as badly as Heron's — see below before trusting it as "clean."
> Full history in memory `project-webgpu-gridsample-bug` (fifth diagnosis): a WebGPU EP
> `GridSample` (deformable-attention) kernel bug was flagged, then retracted after an
> isolated/synthetic shader check looked correct, then a real bug in
> `tools/gpu-threshold-probe` itself was found and fixed (it ran GPU inference once at a
> low confidence floor and re-filtered by score, assuming NMS-only suppression — but
> `LayoutAnalyzer.SuppressNestedBlocks`, which runs after NMS, is purely geometric and
> not confidence-aware, so a sea of low-confidence candidates produced noise boxes that
> deleted real detections outright), then a small 4-PDF/8-page academic-only corpus was
> found too narrow to catch Heron's real problem even with the tool fixed. Widening the
> corpus to 42 pages across 11 documents, including plain single-column forms/invoices,
> surfaced it clearly: **Heron FP16 shows 50 missed detections + 13-16 spurious extras**,
> worst on plain documents (7 misses on one page of a short form), while **PP-DocLayoutV3
> FP16 shows zero misses** on the same widened corpus. An `enc_score_head` fp32-promotion
> graph-surgery mitigation (targeting a retracted TopK-rounding theory) was tried twice
> against Heron and made no measurable difference either time.
>
> **A layer-by-layer CPU-vs-GPU activation diff (`tools/webgpu-diag`) then re-examined the
> original GridSample theory against real page content on both models and found it was
> right all along**: decoder `GridSample` cosine similarity collapses to 0.39-0.75 (from
> >0.9999 everywhere upstream) on every document type tried, in **both** Heron and
> PP-DocLayoutV3 — the earlier "verified correct in isolation" check simply didn't
> exercise the value distribution real decoder activations produce. Critically,
> **PP-DocLayoutV3's raw divergence is comparable to or worse than Heron's on the same
> pages**, yet it doesn't turn into visible misses — its downstream box-decode/NMS
> pipeline is for unknown reasons far more robust to this kernel noise. So: Heron GPU is
> confirmed broken end-to-end; PP-DocLayoutV3 GPU has a confirmed *upstream* kernel
> problem too, just not (yet) a confirmed *detection-level* one — don't assume it's safe
> on documents unlike the 28-page tested subset. Root cause of the kernel-level
> divergence itself, and of PP-DocLayoutV3's robustness to it, are both still unknown.
> Both exports are otherwise fine (validated against the CPU/FP32 reference on CPU) —
> this is an execution-provider correctness issue, not a flaw in either FP16 conversion.

Produces FP16 ONNX exports of the two layout-detection models that have real
PyTorch/HF Transformers source checkpoints (Heron, PP-DocLayoutV3), for use
with the native WebGPU execution provider (`RailReader.Core.Analysis.WebGpu`,
see `tools/webgpu-probe`). PP-DocLayout-S has no PyTorch/HF source — see
"What's not covered" below.

## Why this exists

A naive post-hoc FP16 conversion of the *already-exported* ONNX graphs (via
`onnxconverter_common.float16`) does not work for these models: their
detection-head postprocessing (box decode, reading-order, NMS-adjacent
arithmetic) is entangled with Cast/type-annotation bugs in that tool and,
independently, genuinely mixed-precision-sensitive. Exporting fresh from the
source PyTorch models — where the tracer keeps every op's dtype consistent —
avoids that class of problem entirely. Both scripts here run the
backbone/encoder/decoder in FP16 and keep the box-decode/sigmoid/top-k
postprocessing arithmetic in FP32, mirroring each model's own reference
`post_process_object_detection` implementation exactly (so the *only* delta
from the original FP32 ONNX is precision, not algorithm).

Full write-up, including the two bugs each export needed fixing (documented
inline in the scripts too), is in project memory
`project-onnx-gpu-ep-investigation` (2026-08-25 entries).

## Setup

Each script needs its own venv — Heron's checkpoint needs `transformers`
4.53.x (the version its HF port was authored against); V3's `pp_doclayout_v3`
architecture is only registered starting in `transformers` 5.x. Do not share
one venv between them.

```bash
# Heron
python3 -m venv .venv-heron
source .venv-heron/bin/activate
pip install -r requirements-heron.txt
pip install torch==2.5.1 --index-url https://download.pytorch.org/whl/cu124  # or /cpu for a CPU-only export

# PP-DocLayoutV3
python3 -m venv .venv-v3
source .venv-v3/bin/activate
pip install -r requirements-v3.txt
pip install torch==2.5.1 --index-url https://download.pytorch.org/whl/cu124  # or /cpu
```

A CUDA GPU is strongly recommended for the export itself (tracing a `.half()`
model): plain `torch==2.5.1+cpu` lacks some fp16 CPU kernels these
architectures hit. The exported *file* has no CUDA dependency — it runs fine
on CPU or WebGPU afterward; CUDA is only needed to author it.

## Usage

```bash
python export_heron_fp16.py docling-project/docling-layout-heron heron-fp16.onnx
python export_v3_fp16.py PaddlePaddle/PP-DocLayoutV3_safetensors v3-fp16.onnx
```

Both scripts are drop-in replacements for the existing FP32 `.onnx` files —
same input/output tensor names, shapes, and dtypes as
`docling-layout-heron.onnx` / `PP-DocLayoutV3.onnx` — so no C# changes are
needed; point `HeronLayoutAnalyzer`/`LayoutAnalyzer` at the new file.

## Validate before trusting a new export

Both were validated against the **real RailReaderCore pipeline**, not just a
synthetic tensor comparison — a synthetic-noise check on a square (H==W)
image can hide an axis-order bug that only shows up on a real, non-square
page (this happened once during development; see the W/H-order comment in
`export_heron_fp16.py`). Use `tools/webgpu-probe`:

```bash
dotnet run --project ../webgpu-probe -c Release -- \
  <a real PDF> heron <original heron.onnx path> <new heron-fp16.onnx path> 0 10
dotnet run --project ../webgpu-probe -c Release -- \
  <a real PDF> v3 <original PP-DocLayoutV3.onnx path> <new v3-fp16.onnx path> 0 10
```

It reports a speedup number and a block-centroid correctness comparison
against the original FP32 model on CPU. Expect the block count to match (or
be off by one on a borderline near-duplicate detection — this is normal FP16
rounding noise, the same pattern seen when running the INT8 Heron model on
WebGPU) and centroids to match to within a couple of pixels. Anything more
different than that is a real bug, not precision noise — investigate before
using the export.

## What's not covered

**PP-DocLayout-S** has no published PyTorch/HF Transformers checkpoint (only
Paddle-native `inference.pdiparams`) — nothing to re-export from with this
approach. A prior attempt at post-hoc `onnxconverter_common` conversion for
it loaded but crashed at runtime with a corrupted shape dimension; converting
it properly would need either a Paddle-native FP16 export path or a
PyTorch/HF port of PicoDet/GFL, neither of which currently exists.

## Measured results (spike, 2026-08-25, Intel Iris Xe iGPU via Vulkan — not a
rigorous benchmark)

| Model | CPU (FP32) median | WebGPU (FP16) median | Speedup | Correctness |
|---|---|---|---|---|
| Heron | 4520ms | 475ms | ~9.5x | 13-14/14 blocks match across pages, one borderline near-dup difference |
| PP-DocLayoutV3 | 5888ms | 804ms | ~7.3x | 14/14 blocks match, sub-pixel differences only |
