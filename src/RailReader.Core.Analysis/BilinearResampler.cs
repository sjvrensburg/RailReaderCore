namespace RailReader.Core.Analysis;

/// <summary>
/// Shared bilinear-resize sampling geometry used by the Heron and PP-DocLayout-S
/// preprocessors. One implementation of the fiddly coordinate mapping / clamping /
/// filter blend so a geometry fix lands in both analyzers at once; the per-pixel
/// output stage (uint8 CHW layout vs float ImageNet normalisation) is supplied by
/// the caller as a struct sink, which the JIT specialises and inlines — no
/// per-pixel delegate cost.
///
/// <para>
/// <b>Sampling semantics: PIL-equivalent bilinear.</b> This mirrors
/// <c>PIL.Image.BILINEAR</c> (Pillow ≥ 2.7), the resampler used by the Python
/// <c>raildla</c> reference: half-pixel centres, a triangle filter whose support
/// scales with the reduction factor (so a downscale area-averages every source
/// pixel instead of point-sampling 1 in N), two separable passes
/// (horizontal → vertical), and uint8 quantisation between and after passes.
/// At PP-S's real 1920→480 path (~4× reduction) this keeps 1–2 px strokes
/// (small text, hairline rules) contributing to the output instead of dropping
/// out phase-dependently. Validated byte-exact against Pillow and
/// detection-compared against the live PP-S and Heron models (2026-07-12).
/// </para>
/// </summary>
internal static class BilinearResampler
{
    /// <summary>Per-pixel output stage. Implement as a struct for JIT specialisation.</summary>
    internal interface IPixelSink
    {
        /// <summary>Receives the resampled (uint8-quantised, 0–255) RGB sample for one output pixel.</summary>
        void Write(int dstIdx, float r, float g, float b);
    }

    /// <summary>
    /// Resizes an HWC RGB byte buffer of <paramref name="srcW"/>×<paramref name="srcH"/>
    /// to <paramref name="target"/>×<paramref name="target"/> (aspect ratio is
    /// deliberately distorted — both models expect that), pushing each output
    /// pixel through <paramref name="sink"/>. Callers must handle degenerate
    /// (srcW/srcH &lt;= 0) inputs themselves — their fill values differ.
    /// </summary>
    // Pillow's fixed-point weight precision for uint8 images
    // (Resample.c: PRECISION_BITS = 32 - 8 - 2). Byte-exactness with PIL
    // depends on reproducing this arithmetic, not approximating it in float.
    private const int PrecisionBits = 32 - 8 - 2;

    internal static void Resize<TSink>(byte[] rgbBytes, int srcW, int srcH, int target, ref TSink sink)
        where TSink : struct, IPixelSink
    {
        var (xBounds, xWeights, xTaps) = BuildFilter(srcW, target);
        var (yBounds, yWeights, yTaps) = BuildFilter(srcH, target);

        // Horizontal pass: srcH rows × target columns, quantised to uint8 like
        // Pillow's intermediate image (required for byte-exact equivalence).
        var mid = new byte[srcH * target * 3];
        for (int y = 0; y < srcH; y++)
        {
            int rowBase = y * srcW * 3;
            for (int x = 0; x < target; x++)
            {
                int start = xBounds[x];
                int wBase = x * xTaps;
                int r = 1 << (PrecisionBits - 1);
                int g = r, b = r;
                for (int t = 0; t < xTaps; t++)
                {
                    int w = xWeights[wBase + t];
                    if (w == 0) continue;
                    int o = rowBase + (start + t) * 3;
                    r += rgbBytes[o] * w;
                    g += rgbBytes[o + 1] * w;
                    b += rgbBytes[o + 2] * w;
                }
                int m = (y * target + x) * 3;
                mid[m] = Clip8(r);
                mid[m + 1] = Clip8(g);
                mid[m + 2] = Clip8(b);
            }
        }

        // Vertical pass over the intermediate, straight into the sink.
        for (int y = 0; y < target; y++)
        {
            int start = yBounds[y];
            int wBase = y * yTaps;
            for (int x = 0; x < target; x++)
            {
                int r = 1 << (PrecisionBits - 1);
                int g = r, b = r;
                for (int t = 0; t < yTaps; t++)
                {
                    int w = yWeights[wBase + t];
                    if (w == 0) continue;
                    int o = ((start + t) * target + x) * 3;
                    r += mid[o] * w;
                    g += mid[o + 1] * w;
                    b += mid[o + 2] * w;
                }
                sink.Write(y * target + x, Clip8(r), Clip8(g), Clip8(b));
            }
        }
    }

    /// <summary>
    /// Precomputes per-output-pixel filter windows and normalised triangle
    /// weights, mirroring Pillow's <c>precompute_coeffs</c>: half-pixel centres,
    /// support = reduction factor on downscale (1 on upscale), weights
    /// normalised in double then converted to fixed-point ints.
    /// </summary>
    private static (int[] Bounds, int[] Weights, int Taps) BuildFilter(int srcSize, int dstSize)
    {
        double scale = (double)srcSize / dstSize;
        double filterScale = Math.Max(scale, 1.0);
        double support = filterScale; // triangle filter radius 1.0 × scale
        int taps = (int)Math.Ceiling(support) * 2 + 1;

        var bounds = new int[dstSize];
        var weights = new int[dstSize * taps];
        for (int x = 0; x < dstSize; x++)
        {
            double center = (x + 0.5) * scale;
            int min = (int)Math.Max(center - support + 0.5, 0);
            int max = (int)Math.Min(center + support + 0.5, srcSize);
            bounds[x] = min;

            double sum = 0;
            Span<double> raw = stackalloc double[taps];
            for (int i = min; i < max; i++)
            {
                double t = Math.Abs((i - center + 0.5) / filterScale);
                double w = t < 1.0 ? 1.0 - t : 0.0;
                raw[i - min] = w;
                sum += w;
            }
            for (int t = 0; t < max - min; t++)
                weights[x * taps + t] = sum > 0 ? (int)(raw[t] / sum * (1 << PrecisionBits) + 0.5) : 0;
        }
        return (bounds, weights, taps);
    }

    private static byte Clip8(int acc)
    {
        int v = acc >> PrecisionBits;
        return (byte)Math.Clamp(v, 0, 255);
    }
}
