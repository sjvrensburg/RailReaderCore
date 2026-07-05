# Rotation fixtures

LaTeX-generated PDFs for page-rotation and sideways-text testing. The compiled
PDFs are committed so tests and CI need no LaTeX; rebuild with `./build.sh`
(pdflatex) after editing a `.tex` source.

| Fixture | Contents | Exercises |
|---|---|---|
| `rotate-suite.pdf` | Four pages with a byte-identical portrait content stream and `/Rotate` 0/90/180/270 (`\pdfpageattr`), plus an internal link from page 1 to a target on the rotated page 2 | Phase 0: `/Rotate`-aware coordinate transforms, annotation round-trip, cross-rotation PDF-space quad invariant, rotated link destinations |
| `landscape-scan.pdf` | Portrait page whose whole body is `\rotatebox{90}`'d, **no** `/Rotate` — imitates a sideways scan | Phase 1: manual view rotation; Phase 2: glyph angles (all content chars report 270°) |
| `sideways-table.pdf` | Upright prose above and below a `\rotatebox{90}` table (the common academic pattern) | Phase 2: sideways-block detection, atomic line collapse, upright VLM crops |

Ground-truth measurements against these fixtures live in `tools/rotation-probe`
(PDFium/Skia backend) and `tools/rotation-probe-pdfpig` (PdfPig backend, which
must run as a separate process — PDFium and PdfPig cannot coexist in-process).
The probes report an ink-coverage metric: the fraction of dark rendered pixels
falling inside the extracted char boxes when mapped the way the app maps them
(≈1.0 = geometry and raster in the same frame). `rotation-probe` can also emit
a copy of the suite with a highlight authored over the marker on every page
(second CLI arg = output path) for independent-viewer fidelity checks
(Poppler `pdftoppm`).

Regression tests consuming these fixtures: `RotationTests`, `PageTransformTests`,
`PdfPigRotationTests`, `ViewRotationTests`, `SidewaysBlockTests`.
