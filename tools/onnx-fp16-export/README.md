# onnx-fp16-export

> ⚠ **These FP16 exports are no longer used for GPU inference (fixed 2026-08-28) —
> `LayoutModelRegistry.Resolve` now routes GPU requests to the plain FP32 models
> instead.** Root cause, after a long diagnosis (full history in memory
> `project-webgpu-gridsample-bug`): both Heron and PP-DocLayoutV3's decoders select
> their initial queries via `TopK` over ~8400 candidate scores, and on real pages many
> scores cluster within a single FP16 ULP of the k=300 cutoff. CPU's and WebGPU's
> independently-implemented FP16 kernels accumulate just enough ordinary rounding drift
> through the backbone/encoder (measured: ~0.014 absolute disagreement, *larger* than
> the ~0.002 gap between adjacent-ranked candidates at the cutoff) to select a
> genuinely different ~10% of the query set between the two EPs — not a WebGPU kernel
> bug (`GridSample`'s own math was verified identical to the CPU reference), a discrete
> selection instability inherent to running query selection this close to a threshold in
> FP16. This reproduced as 50 missed detections on Heron across a 42-page corpus
> (matching real RailReader2 field reports) and, less often (~13x lower exposure, not
> zero), the same underlying issue in PP-DocLayoutV3. Two targeted FP32-promotion
> graph-surgery mitigations were tried and both measured to make zero difference — the
> accumulated disagreement is too large for a local patch downstream of it to close.
>
> **What actually works:** run the plain FP32 ONNX model (already published, no
> re-export needed) on the WebGPU EP instead of an FP16 export. Measured on the same
> 42-page corpus: cosSim 1.00000 at every checkpoint, 0 misses / 0 extras for both
> models, and — critically — **no meaningful speed cost**: 9.85x (Heron) / 7.98x
> (PP-DocLayoutV3) CPU→GPU speedup, matching what the FP16 exports themselves claimed
> (~9.5x / ~7.3x). GPU parallelism, not FP16's halved memory bandwidth, was already the
> dominant speedup factor for these models on the hardware tested — so there's no
> reason left to prefer FP16 for GPU use. These scripts and their exports remain useful
> for direct/manual experimentation, but are not the recommended path for GPU
> acceleration going forward.

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
