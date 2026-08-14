using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Pure-data consistency checks for <see cref="OcrModelRegistry"/>, plus (when the files have
/// actually been downloaded via <c>scripts/download-ocr-model.sh</c>) an end-to-end check that
/// a registry entry resolves and recognizes text — the same seam a multilingual language-pack
/// picker would use (railreader2#209).
/// </summary>
public class OcrModelRegistryTests
{
    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void Descriptor_FilePathsMatchItsOwnModelSet(OcrModelDescriptor d)
    {
        // The registry hand-writes RelativePath as a documentation/API-shape choice, but it
        // must always agree with the RapidOcrModelSet it claims to resolve — a mismatch here
        // would mean OcrModelLocator probes for one filename while the download script (which
        // reads the same descriptor) fetches another.
        Assert.Equal(d.ModelSet.DetModelPath, d.Det.RelativePath);
        Assert.Equal(d.ModelSet.RecModelPath, d.Rec.RelativePath);
        Assert.Equal(d.ModelSet.KeysPath, d.Dict.RelativePath);
    }

    // Full-pass milliseconds measured with tools/ocr-cost-probe on the railreader2#209 scan,
    // page 0 at 1920px, best of two passes on a 20-core box (issue #100). Absolute numbers belong
    // to that machine; the RATIOS are what the descriptors publish and what these tests pin.
    private const double TinyFullMs = 2254, SmallFullMs = 4484, MediumFullMs = 67518;

    [Fact]
    public void Descriptors_PublishedCostsReproduceTheMeasuredFullPassRatios()
    {
        // RelativeFullCost is derived from the two published components, so if the components or
        // the blend drift, the single "how much slower?" figure a picker shows stops being the
        // number the user actually waits through. 2% covers the rounding in the published values.
        AssertRatio(TinyFullMs / TinyFullMs, OcrModelRegistry.PPOCRv6Tiny.RelativeFullCost);
        AssertRatio(SmallFullMs / TinyFullMs, OcrModelRegistry.PPOCRv6Small.RelativeFullCost);
        AssertRatio(MediumFullMs / TinyFullMs, OcrModelRegistry.PPOCRv6Medium.RelativeFullCost);

        static void AssertRatio(double measured, double published) =>
            Assert.True(Math.Abs(published - measured) / measured <= 0.02,
                $"published {published:F2}x vs measured {measured:F2}x — re-measure with tools/ocr-cost-probe");
    }

    [Fact]
    public void Descriptors_AnchorAtTinyAndRiseWithTier()
    {
        // Tiny is the baseline the other two are quoted against; a set that claimed to be cheaper
        // than the baseline would mean the anchor moved and every published figure is stale.
        Assert.Equal(1.0, OcrModelRegistry.PPOCRv6Tiny.RelativeDetectionCost);
        Assert.Equal(1.0, OcrModelRegistry.PPOCRv6Tiny.RelativeRecognitionCost);

        var tiers = new[]
        {
            OcrModelRegistry.PPOCRv6Tiny,
            OcrModelRegistry.PPOCRv6Small,
            OcrModelRegistry.PPOCRv6Medium,
        };
        for (int i = 1; i < tiers.Length; i++)
        {
            Assert.True(tiers[i].RelativeDetectionCost > tiers[i - 1].RelativeDetectionCost);
            Assert.True(tiers[i].RelativeRecognitionCost > tiers[i - 1].RelativeRecognitionCost);
            Assert.True(tiers[i].ApproxSizeMb > tiers[i - 1].ApproxSizeMb);
        }

        // The point of publishing cost at all: it is not predictable from download size. Medium is
        // ~23x Tiny's bytes but far more than that in recognition, which is the trap in #100.
        double sizeRatio = (double)OcrModelRegistry.PPOCRv6Medium.ApproxSizeMb
                           / OcrModelRegistry.PPOCRv6Tiny.ApproxSizeMb;
        Assert.True(OcrModelRegistry.PPOCRv6Medium.RelativeRecognitionCost > sizeRatio * 2);
    }

    [Theory]
    [MemberData(nameof(AllDescriptors))]
    public void Descriptor_HashesAreWellFormedSha256(OcrModelDescriptor d)
    {
        foreach (var hash in new[] { d.Det.Sha256, d.Rec.Sha256, d.Dict.Sha256 })
        {
            Assert.Equal(64, hash.Length);
            Assert.Matches("^[0-9a-f]{64}$", hash);
        }
    }

    [Fact]
    public void ById_FindsKnownEntriesCaseInsensitively()
    {
        Assert.Same(OcrModelRegistry.PPOCRv6Tiny, OcrModelRegistry.ById("PPOCRV6-TINY"));
        Assert.Same(OcrModelRegistry.PPOCRv6Small, OcrModelRegistry.ById("ppocrv6-small"));
        Assert.Same(OcrModelRegistry.PPOCRv6Medium, OcrModelRegistry.ById("ppocrv6-medium"));
        Assert.Null(OcrModelRegistry.ById("nope"));
    }

    [Fact]
    public void Default_IsTiny()
    {
        Assert.Same(OcrModelRegistry.PPOCRv6Tiny, OcrModelRegistry.Default);
    }

    [Fact]
    public void All_DoesNotIncludeTheBundledLatinSet()
    {
        // PPOCRv5Latin ships with the RapidOcrNet package and needs no download — it isn't
        // something this registry's caller would ever fetch, so it must not appear here.
        Assert.DoesNotContain(OcrModelRegistry.All, d => d.Id.Contains("latin", StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<object[]> AllDescriptors() =>
        OcrModelRegistry.All.Select(d => new object[] { d });

    [OcrV6ModelFact]
    public void PPOCRv6Tiny_ResolvesAndRecognizesText()
    {
        var located = OcrModelLocator.Locate(OcrModelRegistry.PPOCRv6Tiny.ModelSet);
        Assert.NotNull(located);

        var (rgb, w, h) = RenderText(["Hello multilingual OCR"]);
        using var ocr = new RapidOcrService(OcrModelRegistry.PPOCRv6Tiny.ModelSet);
        var page = ocr.Recognize(rgb, w, h, OcrMode.Full);

        Assert.NotEmpty(page.Lines);
        var text = string.Join(" ", page.Lines.Select(l => l.Text));
        Assert.Contains("multilingual", text, StringComparison.OrdinalIgnoreCase);
    }

    private static (byte[] Rgb, int W, int H) RenderText(string[] lines, float textSize = 48f,
        int width = 900, int height = 200)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, textSize);
        for (int i = 0; i < lines.Length; i++)
            canvas.DrawText(lines[i], 40f, 80f + i * (textSize * 1.6f), font, paint);

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        var rgb = new byte[width * height * 3];
        var pixels = bitmap.Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            rgb[i * 3] = pixels[i].Red;
            rgb[i * 3 + 1] = pixels[i].Green;
            rgb[i * 3 + 2] = pixels[i].Blue;
        }
        return (rgb, width, height);
    }
}

/// <summary>
/// A [Fact] that is skipped (with a visible reason) when the PP-OCRv6-Tiny language pack has
/// not been downloaded — it is an opt-in extra (<c>scripts/download-ocr-model.sh</c>), not part
/// of a normal checkout, so its absence must not fail CI.
/// </summary>
public sealed class OcrV6ModelFactAttribute : FactAttribute
{
    public OcrV6ModelFactAttribute()
    {
        if (OcrModelLocator.Locate(OcrModelRegistry.PPOCRv6Tiny.ModelSet) is null)
            Skip = "PP-OCRv6-Tiny models not found; run scripts/download-ocr-model.sh tiny to enable this test.";
    }
}
