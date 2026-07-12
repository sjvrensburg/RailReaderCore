using RailReader.Core.Analysis;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Pins <see cref="BilinearResampler"/> to PIL.Image.BILINEAR semantics
/// (half-pixel centres, support scaled by the reduction factor, Pillow's
/// fixed-point arithmetic). Expected vectors were generated with Pillow 12.3.0
/// (<c>Image.fromarray(src).resize((n, n), Image.BILINEAR)</c>) — the resampler
/// must stay byte-exact against them; a full 1920×1409→480/640 raster was
/// additionally verified byte-exact during the 2026-07-12 audit follow-up.
/// </summary>
public class BilinearResamplerTests
{
    private struct RawSink(byte[] outBuf) : BilinearResampler.IPixelSink
    {
        public void Write(int dstIdx, float r, float g, float b)
        {
            outBuf[dstIdx * 3] = (byte)r;
            outBuf[dstIdx * 3 + 1] = (byte)g;
            outBuf[dstIdx * 3 + 2] = (byte)b;
        }
    }

    private static byte[] Resize(byte[] src, int srcW, int srcH, int target)
    {
        var actual = new byte[target * target * 3];
        var sink = new RawSink(actual);
        BilinearResampler.Resize(src, srcW, srcH, target, ref sink);
        return actual;
    }

    [Fact]
    public void Downscale_MatchesPillowByteExact()
    {
        // src[i] = (i * 37) % 256 over an 8×6 HWC RGB buffer, resized to 3×3.
        // A >2× reduction, so every output pixel area-averages a multi-pixel
        // window — the case where point-sampled bilinear diverges from PIL.
        var src = new byte[8 * 6 * 3];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(i * 37 % 256);

        var expected = new byte[]
        {
            136,  93, 113, 127, 143, 106,  96, 122, 151,
            154, 140, 107, 113, 149, 152, 109, 109, 146,
            139, 144,  97, 101, 136, 150, 133,  95, 132,
        };
        Assert.Equal(expected, Resize(src, 8, 6, 3));
    }

    [Fact]
    public void Upscale_MatchesPillowByteExact()
    {
        // 3×2 RGB source resized to 5×5 (upscale: support stays 1, but the
        // half-pixel-centre mapping still differs from align-corners away from
        // the corners).
        var src = new byte[]
        {
            10, 20, 30, 100, 110, 120, 200, 210, 220,
            5, 15, 25, 95, 105, 115, 195, 205, 215,
        };
        var expected = new byte[]
        {
            10, 20, 30, 46, 56, 66, 100, 110, 120, 160, 170, 180, 200, 210, 220,
            10, 20, 30, 46, 56, 66, 100, 110, 120, 160, 170, 180, 200, 210, 220,
            8, 18, 28, 44, 54, 64, 98, 108, 118, 158, 168, 178, 198, 208, 218,
            5, 15, 25, 41, 51, 61, 95, 105, 115, 155, 165, 175, 195, 205, 215,
            5, 15, 25, 41, 51, 61, 95, 105, 115, 155, 165, 175, 195, 205, 215,
        };
        Assert.Equal(expected, Resize(src, 3, 2, 5));
    }

    [Fact]
    public void Downscale_ThinStrokesSurvive()
    {
        // A single dark row in a white 16×16 source must still darken the 4×4
        // output under area-averaging (point sampling could miss it entirely
        // depending on phase). This is the PP-S 1920→480 small-text guarantee.
        var src = new byte[16 * 16 * 3];
        Array.Fill(src, (byte)255);
        for (int x = 0; x < 16; x++)
            for (int c = 0; c < 3; c++)
                src[(5 * 16 + x) * 3 + c] = 0; // row y=5, off the 4× sample phase

        var output = Resize(src, 16, 16, 4);
        // Output row 1 covers source rows 4–7; the stroke must darken it.
        int rowMin = 255;
        for (int x = 0; x < 4; x++) rowMin = Math.Min(rowMin, output[(1 * 4 + x) * 3]);
        Assert.True(rowMin < 220, $"stroke vanished: min={rowMin}");
    }
}
