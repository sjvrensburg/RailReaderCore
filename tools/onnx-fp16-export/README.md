# onnx-fp16-export

> ⚠ **GPU-correctness status is under active re-evaluation (2026-08-28).** Two prior
> diagnoses here (a WebGPU EP `GridSample` kernel bug, then fp16 `TopK`/mask-threshold
> rounding sensitivity) were both measured via `tools/gpu-threshold-probe`, which had
> its own bug: it ran GPU inference once at a low confidence floor and re-filtered the
> resulting blocks by score, assuming NMS-only suppression — but `LayoutAnalyzer`'s
> `SuppressNestedBlocks` (runs after NMS) is purely geometric and not confidence-aware,
> so admitting a sea of low-confidence candidates produced large noise boxes that
> deleted real, correct, higher-confidence detections outright. That inflated this
> project's corpus "CPU-only misses" from 0 to 15 for the *unmodified* PP-DocLayoutV3
> FP16 export below. With the tool fixed (re-run GPU inference directly at each
> threshold instead of low-then-refilter), **PP-DocLayoutV3 shows zero CPU-vs-GPU
> detection misses** on the project's real-PDF corpus check. **Heron still shows a
> small residual divergence** (5 misses across 8 corpus pages) that is real, not a
> tooling artifact, but is NOT explained by the TopK/mask-threshold theory — an
> `enc_score_head` fp32 promotion made no difference to it. Both exports are otherwise
> fine (validated against the CPU/FP32 reference). See `tools/gpu-threshold-probe`
> (now fixed) and project memory `project-webgpu-gridsample-bug` (now on its third
> diagnosis) before trusting any of this file's or `WebGpuAccelerator`'s older
> correctness claims — re-validate on a larger corpus, and root-cause Heron's residual
> divergence, before changing the default GPU-acceleration recommendation.

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
