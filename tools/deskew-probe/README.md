# deskew-probe

Measures automatic deskew (`CoreSettings.DeskewOcrLines`, 0.56.0) on real scanned PDFs.

Skew correction is invisible in normal use — it either quietly fixes line grouping or quietly
does nothing — so this reports the three numbers you need to tell those apart: the angle
recovered from the OCR detector's own line quads, the raw ungated evidence behind that angle,
and the line count grouping recovers with and without the correction.

```bash
dotnet run --project tools/deskew-probe -c Release -- <pdf|dir> [rasterSize=1920]
```

`DESKEWPROBE_MAXPAGES` caps pages per document (default 8). Exit code 2 if any page regressed.

Needs the PP-OCR models, which ship with the RapidOcrNet package (`OcrModelLocator.LocateDefault`
finds them beside the build output). No layout model is required: the probe treats the whole page
as one block, which keeps the measurement about *grouping* rather than about which detector found
which block.

## Example

The reporter's scan from railreader2#209:

```
test.pdf  2 page(s)  raster 1920px
page textlyr   skew° ocrlns deskewed  plain  verdict
   0   False    0.00     34       33     33  no change
       measured= 33  raw median=  0.00°  p25= -0.15°  p75=  0.00°  min=-0.44° max=0.29°
   1   False    0.72     43       42     38  +4 recovered
       measured= 43  raw median=  0.72°  p25=  0.66°  p75=  0.84°  min=0.00° max=1.11°
```

## Reading the output

**A reported `0.00` is two different things.** With a *tight* raw spread (page 0 above) the page
is genuinely square and correctly left alone — that is the dead band doing its job. With a *wide*
raw spread it was thrown out by the confidence gate, which means the detector found something
that was not a page of text lines, and is worth looking at. Printing the raw quartiles beside the
gated estimate is the only way to distinguish them.

**A tight interquartile spread on a non-zero angle is the signature of a real scan**, because a
sheet rotation is rigid — every line shares it. Page 1's 0.18° spread across 43 lines is what
that looks like. A wide spread means curved text, a rotated caption, or a figure being read as
lines.

**Expect `no change` on mildly skewed pages, and do not read it as a failure.** Merging is not a
function of angle alone: a line only reaches its neighbour's band once its drift across the
column, `width × tan(θ)`, exceeds the median glyph height that sets the split threshold. Small
glyphs in a wide column — ordinary book text — cross over below 1°, which is why page 1 loses
four lines at 0.72°. Large text needs several degrees to lose anything.

**A `REGRESSION` line is a real defect**, not noise. The correction is only ever supposed to
split bands that were wrongly merged; it should never cost a line that plain grouping found.
