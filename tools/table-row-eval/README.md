# table-row-eval

Quantitative validation for **table-row line detection** (`CoreSettings.TableRowReading`)
and **cell-level column detection** (`CoreSettings.CellNavigation`): scores `LineDetector`'s
per-row and per-cell output for `Table` blocks against the
[SynFinTabs](https://huggingface.co/datasets/ethanbradley/synfintabs) financial-table
dataset's ground-truth rows and cells.

Unlike `tools/line-eval` (which needs a PDF, PDFium, ONNX and a Surya oracle), this
harness runs `LineDetector` **directly** on word boxes synthesised from the dataset, so
it only references `RailReader.Core`. SynFinTabs ships per-word, per-cell and per-row
bounding boxes (all `[left, top, right, bottom]` in image pixels — a direct map onto
`CharBox`/`BBox`), which makes it a turnkey **row and cell oracle** — no Surya required.

## Run

```bash
# 1. Fetch a sample of the test split (no auth; MIT-licensed dataset).
python3 fetch_synfintabs.py --n 300 --split test     # -> data/synfintabs-test.json

# 2. Score production LineDetector against the ground-truth rows.
dotnet run -c Release -- data/synfintabs-test.json
```

`data/` is git-ignored.

## Metrics

### Rows (`TableRowReading`)

- **precision / recall / F1** — greedy 1-D Y-IoU match (≥0.5) of detected lines vs GT rows.
- **coverage-recall** — fraction of GT rows reachable (≥1 detected line overlaps). The
  key metric for a rail reader: every row should be steppable.
- **lines/row split** — mean detected lines per GT row. `1.0` = perfect; `>1` quantifies
  over-segmentation from multi-line wrapped cells (a known limitation).
- **exact row count / over-seg / under-seg** — per-example count agreement.

GT rows with no words (spacer / rule rows the detector cannot see) are excluded and
reported separately. `TABLEEVAL_IOU` overrides the match threshold.

### Cells (`CellNavigation`)

Scored over **word-bearing GT cells** only (empty grid cells aren't reading targets).
Detected cells are pooled into the GT row their detected line's centre falls in, then each
GT cell's horizontal ink span is matched against them (`>50%` ink overlap):

- **cell coverage** — fraction of GT cells reachable (some detected cell covers >50% of the
  column's ink). The key cell-nav metric: every column value should be landable.
- **clean 1:1** — GT cell ↔ exactly one detected cell.
- **merged (under-seg)** — GT cell fused with a neighbour into one detected cell. This is
  the gap-threshold failure mode (`CellGapMultiplier`): two columns whose whitespace gap is
  narrower than ~1× the glyph height read as a single cell.
- **split (over-seg)** — GT cell broken across more than one detected cell.
- **cell precision** — detected cells that overlap some GT cell (no phantom cells).
- **cells/row ratio** — detected cells per GT cell (`1.0` = exact).

Latest run (300 test tables, 6 158 rows, 15 642 word-bearing cells): **cell coverage
1.000, clean 1:1 0.974, merged 0.023, split 0.003, precision 0.996, cells/row 0.996** —
every column is reachable, ~97 % are cleanly separable, and the ~2 % residual is adjacent
columns whose whitespace gap is narrower than the split threshold.
