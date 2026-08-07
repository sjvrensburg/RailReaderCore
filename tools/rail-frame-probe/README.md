# rail-frame-probe

Measures **rail-mode horizontal camera stranding**: how far the camera's snap target
leaves the current line's own bounds from the viewport. Built to verify the v0.51.0
`RailNav.KeepLineInFrame` fix (PR #90) against the document that reported it.

The stranding case is a short or indented line chunk-merged with a much wider
neighbour: `GetFramingBounds` frames the camera on the whole chunk union, so a line
whose own start sits well right of the chunk's left edge lands far into (or past) the
viewport, leaving the near half of the screen empty.

Pipeline is the same as `layout-probe` (rasterise → char boxes → `ILayoutAnalyzer` →
reading order → `BlockPostProcessor` → `RailNav`), then for every navigable **line** it
computes where that line's own bounds land on screen under two formulas:

- `BASE` — the pre-0.51.0 target: frame on the chunk (or the block, for a self-framed
  display unit), left-align/centre, hard-clamp into the scroll range.
- `NOW` — `RailNav.ComputeSnapTarget`, i.e. `BASE` plus `KeepLineInFrame`'s nudge.

## Usage

```bash
dotnet run --project tools/rail-frame-probe -c Release -- \
  <pdf> <modelPath> <heron|v3|pps> [first] [last] [zoom] [winW] [winH]
```

With no `zoom`/`winW`/`winH` the probe sweeps a built-in set of (zoom, window)
combinations. **Sweep rather than guess** — the bug is a function of how many windows
wide the chunk is, so it is invisible at a wide viewport and severe at a narrow one.

```bash
dotnet run --project tools/rail-frame-probe -c Release -- \
  book.pdf ~/.config/railreader2/models/PP-DocLayoutV3.onnx v3 25 36
```

## Reading the output

Per (page, config) line:

- `stranded` — lines whose own bounds land entirely off-screen.
- `far-right` — lines whose **start** lands past the window midpoint. This is the
  reported symptom (half the screen empty, long scroll right to find the text) and the
  metric that matters; `base` vs `now` is the before/after of the fix.
- `nudged` — how many lines `KeepLineInFrame` actually moved.
- `slack` — on nudged lines, how far left of the nudged landing the camera may *still*
  travel before `IsAtHardEdge`'s backward test fires. The nudge corrects only the snap
  target, while `ClampX` / `IsAtHardEdge` / `ComputeHorizontalFraction` still bound the
  camera by the chunk, so this is the residual disagreement between the two — see
  `docs/`-adjacent discussion on generalising `FrameOnOwnBlock` instead.

Baseline measured on *Kernel Methods and Machine Learning*, pages 25–36, PP-DocLayoutV3:
0 far-right lines at a 1600px window at any zoom; 159 of 342 at 10× on a 1000px window
pre-fix, 0 post-fix, with an average residual slack of 550px (max 805px).

The probe is read-only and depends only on Core + Analysis + Pdfium + Renderer.Skia.
