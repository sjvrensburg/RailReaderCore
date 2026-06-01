# line-eval — line-detection evaluation harness

Scores our `LineDetector` output against a **Surya text-line-detection oracle**
(SOTA neural line detector, run offline in Python as ground truth — same role
PP-DocLayout-V3 played for reading order in `tools/order-eval`).

## Pieces

1. **`LineEval` (C#)** — runs the production line path (detect → resolve →
   `BlockPostProcessor`, i.e. real `block.Lines`) and dumps per-page blocks +
   lines (page points) to JSON.
   ```bash
   LINEEVAL_MAXPAGES=6 dotnet run --project tools/line-eval -c Release -- \
     <model.onnx> <Heron|PPDocLayoutV3|PPDocLayoutS> /tmp/ours.json <pdf-dir> ...
   ```
2. **`line_oracle.py`** — renders the same pages (pypdfium2) and runs Surya
   `DetectionPredictor`, dumping line boxes in page points.
   ```bash
   # venv: pip install surya-ocr pypdfium2  (CUDA torch wheel installs by default on Linux)
   TORCH_DEVICE=cuda LINEEVAL_MAXPAGES=6 LINE_RENDER_SCALE=2.5 \
     python tools/line-eval/line_oracle.py /tmp/oracle.json <pdf-dir> ...
   ```
3. **`compare_lines.py`** — per non-atomic text block, the oracle lines whose
   centre falls inside the block are ground truth; a 1-D (Y) coverage match
   (≥0.5 of oracle line height) gives precision/recall/F1, count-delta, and
   matched Y-IoU. Born-digital vs scanned reported separately.
   ```bash
   python tools/line-eval/compare_lines.py /tmp/ours.json /tmp/oracle.json
   ```

Surya: code Apache-2.0, weights Open-RAIL-M (free for research/personal/<$5M);
used **offline as an oracle only** — never shipped. Detection is a standalone
~154 MB EfficientViT-Segformer; CPU works (~13 s/page) but GPU is ~2 s/page.

## Baseline (2026-06-01, production Heron-INT8 detector, 633 pages)

| segment | blocks | precision | recall | F1 | exact-count | Y-IoU |
|---------|-------:|----------:|-------:|---:|------------:|------:|
| ALL     | 8,619 | 0.962 | 0.967 | **0.964** | 85.3% | 0.862 |
| born-digital | 8,551 | 0.962 | 0.967 | 0.964 | 85.2% | 0.862 |
| scanned | 68 | 1.000 | 0.970 | 0.985 | 88.2% | 0.860 |

**Conclusion:** the heuristic LineDetector is ~96% aligned with SOTA — a neural
ONNX detector is **not** justified for production (gap doesn't pay for 40–154 MB
+ heatmap→boxes reimplementation). Dominant failure mode: **`DisplayMath`
over-segmentation** (char-clustering splits a single equation into 3–4 lines
where Surya sees 1). Corpus is 99% born-digital, so the pixel-projection/scanned
path is under-sampled here (only 68 blocks).
