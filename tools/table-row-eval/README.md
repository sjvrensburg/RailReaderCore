# table-row-eval

Quantitative validation for **table-row line detection** (`CoreSettings.TableRowReading`):
scores `LineDetector`'s per-row output for `Table` blocks against the
[SynFinTabs](https://huggingface.co/datasets/ethanbradley/synfintabs) financial-table
dataset's ground-truth rows.

Unlike `tools/line-eval` (which needs a PDF, PDFium, ONNX and a Surya oracle), this
harness runs `LineDetector` **directly** on word boxes synthesised from the dataset, so
it only references `RailReader.Core`. SynFinTabs ships per-word, per-cell and per-row
bounding boxes (all `[left, top, right, bottom]` in image pixels — a direct map onto
`CharBox`/`BBox`), which makes it a turnkey **row oracle** — no Surya required.

## Run

```bash
# 1. Fetch a sample of the test split (no auth; MIT-licensed dataset).
python3 fetch_synfintabs.py --n 300 --split test     # -> data/synfintabs-test.json

# 2. Score production LineDetector against the ground-truth rows.
dotnet run -c Release -- data/synfintabs-test.json
```

`data/` is git-ignored.

## Metrics

- **precision / recall / F1** — greedy 1-D Y-IoU match (≥0.5) of detected lines vs GT rows.
- **coverage-recall** — fraction of GT rows reachable (≥1 detected line overlaps). The
  key metric for a rail reader: every row should be steppable.
- **lines/row split** — mean detected lines per GT row. `1.0` = perfect; `>1` quantifies
  over-segmentation from multi-line wrapped cells (the known Phase-1 limitation that
  Phase-2 column tracks will address).
- **exact row count / over-seg / under-seg** — per-example count agreement.

GT rows with no words (spacer / rule rows the detector cannot see) are excluded and
reported separately. `TABLEEVAL_IOU` overrides the match threshold.
