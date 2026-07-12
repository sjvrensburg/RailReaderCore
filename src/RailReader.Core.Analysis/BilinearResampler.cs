namespace RailReader.Core.Analysis;

/// <summary>
/// Shared bilinear-resize sampling geometry used by the Heron and PP-DocLayout-S
/// preprocessors. One implementation of the fiddly coordinate mapping / clamping /
/// 4-tap blend so a geometry fix lands in both analyzers at once; the per-pixel
/// output stage (uint8 round-and-clamp vs float ImageNet normalisation) is
/// supplied by the caller as a struct sink, which the JIT specialises and
/// inlines — no per-pixel delegate cost.
///
/// <para>
/// <b>Sampling semantics (and a known divergence from PIL).</b> The mapping is
/// align-corners — output (x, y) samples source
/// (x·(srcW−1)/(target−1), y·(srcH−1)/(target−1)) — and each output pixel blends
/// only the 4 nearest source pixels. This is classic point-sampled bilinear.
/// It is <em>not</em> equivalent to <c>PIL.Image.BILINEAR</c> (used by the
/// Python <c>raildla</c> reference), which since Pillow 2.7 uses half-pixel
/// centres and scales the filter support by the reduction factor, i.e.
/// area-averages on downscale. At PP-S's real 1920→480 path (~4× reduction)
/// point sampling reads only ~1 of every 4 source rows/columns, so 1–2 px
/// strokes (small text, hairline rules) can alias or drop out phase-dependently,
/// whereas PIL would average them in. Switching to an area-averaging downscale
/// here would change the model inputs and therefore detection outputs; do that
/// only alongside validation against the live models (see the audit note on
/// PPDocLayoutSLayoutAnalyzer.PreprocessImage).
/// </para>
/// </summary>
internal static class BilinearResampler
{
    /// <summary>Per-pixel output stage. Implement as a struct for JIT specialisation.</summary>
    internal interface IPixelSink
    {
        /// <summary>Receives the blended (un-normalised, 0–255 range) RGB sample for one output pixel.</summary>
        void Write(int dstIdx, float r, float g, float b);
    }

    /// <summary>
    /// Resizes an HWC RGB byte buffer of <paramref name="srcW"/>×<paramref name="srcH"/>
    /// to <paramref name="target"/>×<paramref name="target"/> (aspect ratio is
    /// deliberately distorted — both models expect that), pushing each output
    /// pixel through <paramref name="sink"/>. Callers must handle degenerate
    /// (srcW/srcH &lt;= 0) inputs themselves — their fill values differ.
    /// </summary>
    internal static void Resize<TSink>(byte[] rgbBytes, int srcW, int srcH, int target, ref TSink sink)
        where TSink : struct, IPixelSink
    {
        // Map output (x, y) → source (srcX, srcY) so the LAST output pixel
        // samples at exactly (srcW-1, srcH-1).
        float xScale = srcW > 1 ? (float)(srcW - 1) / (target - 1) : 0f;
        float yScale = srcH > 1 ? (float)(srcH - 1) / (target - 1) : 0f;

        for (int y = 0; y < target; y++)
        {
            float fy = y * yScale;
            int y0 = (int)fy;
            int y1 = Math.Min(y0 + 1, srcH - 1);
            float wy = fy - y0;

            for (int x = 0; x < target; x++)
            {
                float fx = x * xScale;
                int x0 = (int)fx;
                int x1 = Math.Min(x0 + 1, srcW - 1);
                float wx = fx - x0;

                int o00 = (y0 * srcW + x0) * 3;
                int o01 = (y0 * srcW + x1) * 3;
                int o10 = (y1 * srcW + x0) * 3;
                int o11 = (y1 * srcW + x1) * 3;

                int dstIdx = y * target + x;
                float r =
                    rgbBytes[o00]     * (1 - wx) * (1 - wy) +
                    rgbBytes[o01]     * wx * (1 - wy) +
                    rgbBytes[o10]     * (1 - wx) * wy +
                    rgbBytes[o11]     * wx * wy;
                float g =
                    rgbBytes[o00 + 1] * (1 - wx) * (1 - wy) +
                    rgbBytes[o01 + 1] * wx * (1 - wy) +
                    rgbBytes[o10 + 1] * (1 - wx) * wy +
                    rgbBytes[o11 + 1] * wx * wy;
                float b =
                    rgbBytes[o00 + 2] * (1 - wx) * (1 - wy) +
                    rgbBytes[o01 + 2] * wx * (1 - wy) +
                    rgbBytes[o10 + 2] * (1 - wx) * wy +
                    rgbBytes[o11 + 2] * wx * wy;
                sink.Write(dstIdx, r, g, b);
            }
        }
    }
}
