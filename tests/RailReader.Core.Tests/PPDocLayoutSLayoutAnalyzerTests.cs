using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class PPDocLayoutSLayoutAnalyzerTests
{
    // ImageNet stats — mirrored from the analyzer for test arithmetic.
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std  = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// Build a synthetic HWC RGB image where each pixel encodes its (x, y)
    /// position in the R and G channels. Lets us read back the bilinear resize
    /// + ImageNet-normalise output and verify the math.
    /// </summary>
    private static byte[] MakeGradient(int w, int h)
    {
        var buf = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                buf[o]     = (byte)(x * 255 / Math.Max(1, w - 1));
                buf[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                buf[o + 2] = 128;
            }
        return buf;
    }

    private static float Norm(byte raw, int channel)
        => (raw / 255f - Mean[channel]) / Std[channel];

    [Fact]
    public void Preprocess_ProducesCHWBufferOfTargetSize()
    {
        var src = MakeGradient(50, 80);
        float[]? buffer = null;
        var output = PPDocLayoutSLayoutAnalyzer.PreprocessImage(src, 50, 80, 480, ref buffer);

        Assert.Equal(3 * 480 * 480, output.Length);
        Assert.Same(buffer, output); // reuses buffer
    }

    [Fact]
    public void Preprocess_ReusesBufferOnRepeatCall()
    {
        var src = MakeGradient(20, 20);
        float[]? buffer = null;
        var first  = PPDocLayoutSLayoutAnalyzer.PreprocessImage(src, 20, 20, 480, ref buffer);
        var second = PPDocLayoutSLayoutAnalyzer.PreprocessImage(src, 20, 20, 480, ref buffer);

        Assert.Same(first, second);
    }

    [Fact]
    public void Preprocess_CornersSampleCornersOfSource_AndApplyImageNetNorm()
    {
        // Under PIL half-pixel-centre bilinear, an upscale's corner output
        // pixels have a single source tap — they still sample the source
        // corners exactly. ImageNet normalisation is then applied per channel.
        const int srcW = 32, srcH = 32, target = 480;
        var src = MakeGradient(srcW, srcH);
        float[]? buffer = null;
        var output = PPDocLayoutSLayoutAnalyzer.PreprocessImage(src, srcW, srcH, target, ref buffer);

        int planeSize = target * target;
        int br = target * target - 1;

        // Top-left output: src(0,0) = (R=0, G=0, B=128) → normalised per channel
        Assert.Equal(Norm(0, 0),   output[0 * planeSize + 0],  3);
        Assert.Equal(Norm(0, 1),   output[1 * planeSize + 0],  3);
        Assert.Equal(Norm(128, 2), output[2 * planeSize + 0],  3);

        // Bottom-right output: src(31,31) = (R=255, G=255, B=128) → normalised
        Assert.Equal(Norm(255, 0), output[0 * planeSize + br], 3);
        Assert.Equal(Norm(255, 1), output[1 * planeSize + br], 3);
        Assert.Equal(Norm(128, 2), output[2 * planeSize + br], 3);
    }

    [Fact]
    public void Preprocess_BlackPixelNormalisesToNegativeMeanOverStd()
    {
        // A 1×1 black input should normalise every output pixel to (-mean/std)
        // per channel — this is the well-defined value the degenerate-input
        // guard also falls back to, and a useful sanity check for the
        // normalisation math itself.
        var src = new byte[3]; // black
        float[]? buffer = null;
        var output = PPDocLayoutSLayoutAnalyzer.PreprocessImage(src, 1, 1, 8, ref buffer);

        int planeSize = 8 * 8;
        for (int c = 0; c < 3; c++)
        {
            float expected = -Mean[c] / Std[c];
            for (int i = 0; i < planeSize; i++)
                Assert.Equal(expected, output[c * planeSize + i], 4);
        }
    }

    [Fact]
    public void Preprocess_DegenerateInputProducesNormalisedBlackBuffer()
    {
        // Zero-sized source — no crash; output is filled with (-mean/std) per
        // channel (the normalised value of a black image).
        var src = Array.Empty<byte>();
        float[]? buffer = null;
        var output = PPDocLayoutSLayoutAnalyzer.PreprocessImage(src, 0, 0, 64, ref buffer);

        Assert.Equal(3 * 64 * 64, output.Length);
        int planeSize = 64 * 64;
        for (int c = 0; c < 3; c++)
        {
            float expected = -Mean[c] / Std[c];
            for (int i = 0; i < planeSize; i++)
                Assert.Equal(expected, output[c * planeSize + i], 4);
        }
    }

    // End-to-end inference is not tested here. The PP-DocLayout-S ONNX (~4.7 MB)
    // is not bundled with this repo or downloaded as part of `dotnet test`. The
    // existing LayoutAnalyzer / HeronLayoutAnalyzer tests follow the same
    // convention — only pure helpers (preprocessing arithmetic, NMS, IoU, nested
    // suppression) are unit-tested. Live integration is verified through the
    // consumer app (railreader2 + the Python raildla prototype, which uses the
    // same ONNX I/O contract this analyzer mirrors).
}
