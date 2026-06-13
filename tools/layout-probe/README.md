# layout-probe

Runs a single PDF page through the **real** analysis pipeline and dumps the
geometry rail mode actually navigates, so you can see layout/chunk bugs without
the desktop app. Built to investigate the *"rail frame spans both columns"* bug
(symptom 2 of the drop-cap report).

Pipeline (mirrors `AnalysisWorker`):

```
rasterise (Skia) → char boxes (PDFium) → ILayoutAnalyzer → reading order
(ModelOrderResolver if the model provides it, else XYCutPlusPlusResolver)
→ BlockPostProcessor (overlap trim + line detection) → RailNav chunk build
```

It then prints every block (role, bbox, line count, tallest detected line) and
each navigation **chunk** with its union bbox, flagging anything whose
horizontal extent straddles the page centre — i.e. spans both columns.

## Usage

```bash
dotnet run --project tools/layout-probe -c Release -- <pdf> <modelPath> <heron|v3|pps> [page]
```

- `heron` → `HeronLayoutAnalyzer` (default model; XY-Cut++ ordering)
- `v3`    → `LayoutAnalyzer` / PP-DocLayoutV3 (model-supplied ordering)
- `pps`   → `PPDocLayoutSLayoutAnalyzer`

Example:

```bash
dotnet run --project tools/layout-probe -c Release -- \
  paper.pdf ~/models/docling-layout-heron-int8.onnx heron 0
```

## Reading the output

- A block row marked `<-- BOTH COLS` straddles the page centre. For a *single*
  full-width title/abstract that is expected; for a column body it is a bug.
- A chunk row marked `<-- CHUNK SPANS BOTH COLS` is what rail mode frames. A
  chunk that mixes a full-width block with a single column (so reading the
  column frames the camera across the gutter) is the symptom-2 bug. After the
  `SameChunk` spanner-barrier fix, full-width blocks become their own chunks and
  column bodies stay column-width.
- `tallestLine` far above the body text height flags the symptom-1 drop-cap line
  collapse (a glyph inflating its line's band).

The probe is read-only and depends only on Core + Analysis + Pdfium + Renderer.Skia.
