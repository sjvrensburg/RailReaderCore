# order-eval — reading-order evaluation harness

Measures `IReadingOrderResolver` quality against **PP-DocLayout-V3's own
reading order** as a reference, on a corpus of real PDFs.

## Why V3 as reference

To isolate *reading order* from *detection*, the harness runs **V3 as the
detector** and orders the **same** detected blocks two (or more) ways:

- **V3** — the model's own reading-order hints (`ModelOrderResolver`).
- **ours** — `XYCutPlusPlusResolver` on the raw blocks.
- **merge** — "super-block merge-then-order" (see below), swept over a gap
  threshold.

Divergence is reported as **Kendall-τ distance** (fraction of block pairs
ordered differently; 0 = identical). V3 is a strong reference, **not** ground
truth, so always eyeball the worst pages before trusting a delta. Page-furniture
ordering (Header/Footer/PageNumber) is excluded for the "body-only" τ since its
order is cosmetic.

## Run

```bash
dotnet run -c Release --project tools/order-eval -- \
  <path-to-PP-DocLayoutV3.onnx> /tmp/oe <pdf-dir> [<pdf-dir> ...]
# env: ORDEREVAL_MAXPAGES (default 12)
```

Writes `<prefix>_raw.json` and `<prefix>_merge<gap>.json` per swept gap, then:

```bash
python3 tools/order-eval/analyze2.py /tmp/oe_merge6.json   # body-only τ + buckets
python3 tools/order-eval/analyze3.py /tmp/oe_merge6.json   # column-interleaving breakdown
```

## Super-block merge-then-order

Group vertically-adjacent, horizontally-overlapping text blocks into column-run
"super-blocks"; order the super-blocks with the production resolver on their
union bboxes; expand each super-block top-to-bottom. The super-blocks are a
**transient ordering scaffold** — the output is the original elements with an
improved `Order`, so per-element consumers (VLM crops, Markdown export,
PeekIndex, line detection, `structure` CLI) are unaffected. Barriers (kept as
singletons): Figure/Table/Chart/Header/Footer/PageNumber and any block
≥0.55×page-width. Merge rule: x-overlap ≥0.5 of min width AND vertical gap ≤
`gap`.

## Validated findings (2026-06-01, 1,195-page corpus)

Gap sweep, merge vs raw XYCut++ (0.10.1):

| gap | body-τ | exact pages | group purity |
|----:|-------:|------------:|-------------:|
| raw |  0.0713 |        134 |          —  |
| **6** | **0.0696** | **152** | **86.2%** |
|  10 |  0.0780 |        156 |       80.0% |
|  14 |  0.0796 |        158 |       77.6% |
|  30 |  0.0809 |        150 |       67.3% |

**gap = 6pt** is the sweet spot: body-τ dips below baseline while
column-interleaving pages drop **108 → 43 (−60%)** and body exact-match rises
**31.9% → 39.7%**. Larger gaps over-merge (purity falls, τ rises). The mean τ
barely moves; the win is in the distribution — far more pages perfectly ordered
and far fewer catastrophic interleaves, which is what matters for rail-mode
navigation.

Residual ~14% over-merged groups motivate role-aware **soft barriers** (break a
run at Footnote/Reference transitions; gap relative to local line-height) — the
next refinement to prototype here before productionising the pass.
