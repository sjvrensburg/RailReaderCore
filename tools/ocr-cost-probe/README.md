# ocr-cost-probe

Measures what a page of OCR actually costs, per model tier.

`OcrModelRegistry`'s descriptors advertise download size only, so "Medium — most accurate,
138 MB" reads as a bandwidth decision when it is really a minutes-per-page one. This probe
supplies the missing number (issue #100), split into the two stages that scale differently:

- **det** — one detector pass over the whole page. This is all `OcrMode.Lines` pays.
- **rec** — everything `OcrMode.Full` adds on top: per-line recognition, so it scales with how
  much text the page carries, not with the page.

The analysis worker runs OCR ahead of layout inference for the same page, so **full** is the
stall a scanned page imposes before its ~1 s of layout analysis can even start.

```bash
dotnet run --project tools/ocr-cost-probe -c Release -- <pdf> [page|first-last] [rasterSize=1920]
```

| Env | Meaning |
|---|---|
| `OCRCOST_TIERS` | comma-separated subset of `v5-latin,v6-tiny,v6-small,v6-medium` |
| `OCRCOST_THREADS` | comma-separated intra-op thread caps to sweep (default: the shipping cap) |
| `OCRCOST_REPEATS` | timed passes per (tier, thread cap); the best is reported (default 1) |
| `OCRCOST_BACKENDS` | comma-separated subset of `cpu,gpu` (default: `cpu`) |
| `OCRCOST_GPU_DEVICE` | index into `WebGpuAccelerator.AvailableDevices` to use for `gpu` rows (default `0`) |

`OCRCOST_BACKENDS=cpu,gpu` adds a WebGPU row per tier/thread-cap via `RailReader.Core.Analysis.WebGpu`'s
`WebGpuAccelerator.TryBuildSessionHook()` (issue #121). If no WebGPU-capable device is found the
`gpu` rows are reported and skipped, never silently downgraded to CPU. This probe measures speed
only — see `WebGpuAccelerator.cs`'s "OCR spot-check" doc comment for a one-page CPU-vs-GPU text
correctness diff (byte-for-byte identical across v5-latin/Small/Medium on an Intel Iris Xe iGPU).

On a machine with more than one WebGPU-capable device — this dev box has both an NVIDIA GPU and
an Intel iGPU visible over Vulkan — the probe lists them with indices (vendor, device ID) and
`OCRCOST_GPU_DEVICE=<n>` picks which one the `gpu` rows use; with no device found at that index the
row is reported and skipped, same as no device at all.

### GPU results (issue #121, Intel Iris Xe iGPU, one scanned page, 28-30 lines)

```
tier       backend threads page lines  chars   det ms    rec ms   full ms   ms/line
v5-latin       cpu default    0    28    677     7915      2929     10845       105
v5-latin       gpu default    0    28    677     6560      2367      8927        85
v6-tiny        cpu default    0    30    680    15708      1828     17537        65
v6-tiny        gpu default    0    30    680    15006      3600     18606       129
v6-small       cpu default    0    28    679    24403      4881     29284       174
v6-small       gpu default    0    28    679    15767      4505     20272       161
v6-medium      cpu default    0    30    682   138283    135770    274054      4526
v6-medium      gpu default    0    30    682    17649      3847     21496       128
```

The tier gap that makes Medium impractical on CPU (~274 s/page) collapses on GPU (~21 s/page,
~13x) — a wash for Small (~1.4x) and Tiny (no clear win; small models don't have enough work to
amortise dispatch overhead). GPU is where the *expensive* multilingual tiers become usable, not
where the cheap default gets meaningfully faster. Single machine, single page — re-run before
trusting the multiplier on different hardware or content.

The bundled PP-OCRv5 Latin models ship with the RapidOcrNet package and need no download. The
PP-OCRv6 tiers are opt-in — `scripts/download-ocr-model.sh {tiny,small,medium}` puts them where
`OcrModelLocator` finds them. A tier whose files are missing is reported and skipped.

Rasterisation happens once per page and is reused across tiers, mirroring the worker: OCR is
handed the pixmap already rendered for the layout model, so rasterisation is not OCR's cost.

## Example

The reporter's scan from railreader2#209 — a 34-line page at 1920 px on a 20-core box,
`OCRCOST_REPEATS=2`:

```
tier       threads page lines  chars   det ms    rec ms   full ms   ms/line
v5-latin   default    0    35   2585      825      1002      1827        29
v6-tiny    default    0    35   2586     1591       663      2254        19
v6-small   default    0    35   2586     2589      1894      4484        56
v6-medium  default    0    35   2586    15277     52241     67518      1536
```

Three things that shape the fix in #100:

- Small → Medium is a **~15× wall-clock jump for a 4.5× download** (Tiny → Medium is ~30×). The
  tier gap is the trap, not the OCR path as such.
- `OcrMode.Lines` is **not** a safe harbour at Medium: its detector alone is ~15 s.
- The two stages do **not** scale together. Tiny's detector is nearly 2× the bundled v5-Latin
  set's while its recogniser is cheaper, so a single "how fast is this set?" number would hide
  which mode it is fast in. That is why `OcrModelDescriptor` publishes detection and recognition
  cost separately, both anchored on Tiny = 1; these are the numbers those fields carry.

Sweeping `OCRCOST_THREADS=4,8,16` on the same page moves Medium's full pass 71 s → 65 s → 63 s
(and makes detection *worse*: 16 s → 27 s). The intra-op cap in `OcrSessionOptions` is not the
bottleneck — raising it is not a fix.

These are single runs on a machine that was not quiet: read them as magnitudes, not benchmarks.
For anything finer, use `OCRCOST_REPEATS` on an idle box (see the perf caveats in `CLAUDE.md`).
