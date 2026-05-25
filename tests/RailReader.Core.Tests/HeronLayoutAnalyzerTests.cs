using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class HeronLayoutAnalyzerTests
{
    /// <summary>
    /// Build a synthetic HWC RGB image where each pixel encodes its (x, y)
    /// position in the R and G channels. This lets us read the bilinear
    /// resize output and verify it samples from the expected source pixels.
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

    [Fact]
    public void Preprocess_ProducesCHWBufferOfTargetSize()
    {
        var src = MakeGradient(50, 80);
        byte[]? buffer = null;
        var output = HeronLayoutAnalyzer.PreprocessImage(src, 50, 80, 640, ref buffer);

        Assert.Equal(3 * 640 * 640, output.Length);
        Assert.Same(buffer, output); // reuses buffer
    }

    [Fact]
    public void Preprocess_ReusesBufferOnRepeatCall()
    {
        var src = MakeGradient(20, 20);
        byte[]? buffer = null;
        var first = HeronLayoutAnalyzer.PreprocessImage(src, 20, 20, 640, ref buffer);
        var second = HeronLayoutAnalyzer.PreprocessImage(src, 20, 20, 640, ref buffer);

        Assert.Same(first, second);
    }

    [Fact]
    public void Preprocess_CornersSampleCornersOfSource()
    {
        // Bilinear resize with the (src-1)/(target-1) mapping anchors the
        // last output pixel at the last source pixel — i.e. the output corners
        // sample the source corners exactly.
        const int srcW = 32, srcH = 32, target = 640;
        var src = MakeGradient(srcW, srcH);
        byte[]? buffer = null;
        var output = HeronLayoutAnalyzer.PreprocessImage(src, srcW, srcH, target, ref buffer);

        int planeSize = target * target;
        // Top-left output pixel — should match source (0, 0): R=0, G=0
        Assert.Equal(0, output[0 * planeSize + 0]);            // R
        Assert.Equal(0, output[1 * planeSize + 0]);            // G
        // Bottom-right output pixel — should match source (31, 31): R=255, G=255
        int br = target * target - 1;
        Assert.Equal(255, output[0 * planeSize + br]);         // R
        Assert.Equal(255, output[1 * planeSize + br]);         // G
        // Blue plane is constant 128 across all pixels
        Assert.Equal(128, output[2 * planeSize + 0]);
        Assert.Equal(128, output[2 * planeSize + br]);
    }

    [Fact]
    public void Preprocess_DegenerateInputProducesZeroBuffer()
    {
        // Zero-sized source — no crash, output is all zeros.
        var src = Array.Empty<byte>();
        byte[]? buffer = null;
        var output = HeronLayoutAnalyzer.PreprocessImage(src, 0, 0, 64, ref buffer);

        Assert.Equal(3 * 64 * 64, output.Length);
        Assert.All(output, b => Assert.Equal(0, b));
    }

    // End-to-end inference is not tested here. Heron ONNX (~164 MB) is not
    // bundled with this repo or downloaded as part of `dotnet test`. The
    // existing LayoutAnalyzer tests follow the same convention — only pure
    // helpers (NMS, IoU, suppression, preprocessing arithmetic) are unit-tested
    // and live integration is verified through the consumer app.
}
