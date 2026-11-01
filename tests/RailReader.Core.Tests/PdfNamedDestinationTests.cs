using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

public class PdfNamedDestinationTests
{
    private static byte[] MakePdf(params (int Page, string Name, float X, float Y)[] dests)
    {
        using var ms = new MemoryStream();
        using (var doc = SKDocument.CreatePdf(ms))
        {
            int pages = dests.Length == 0 ? 1 : dests.Max(d => d.Page) + 1;
            for (int p = 0; p < pages; p++)
            {
                var canvas = doc.BeginPage(612, 792);
                foreach (var d in dests.Where(d => d.Page == p))
                    canvas.DrawNamedDestinationAnnotation(new SKPoint(d.X, d.Y), d.Name);
                doc.EndPage();
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void ExtractNamedDestinations_ReturnsNamePageAndTopDownPosition()
    {
        var pdf = MakePdf((0, "RRc0s", 117.5f, 72f), (1, "RRi12", 300f, 400f));

        var dests = new PdfLinkService().ExtractNamedDestinations(pdf)
            .ToDictionary(d => d.Name, d => d.Destination);

        Assert.Equal(2, dests.Count);
        var a = dests["RRc0s"];
        Assert.Equal(0, a.PageIndex);
        Assert.Equal(117.5f, a.PageX!.Value, 0.5f);
        Assert.Equal(72f, a.PageY!.Value, 0.5f);   // page-point space: Y-down, as drawn
        Assert.Equal(792f - 72f, a.PdfY!.Value, 0.5f); // PDF user space: Y-up

        var b = dests["RRi12"];
        Assert.Equal(1, b.PageIndex);
        Assert.Equal(300f, b.PageX!.Value, 0.5f);
        Assert.Equal(400f, b.PageY!.Value, 0.5f);
    }

    [Fact]
    public void ExtractNamedDestinations_NoneInDocument_ReturnsEmpty()
    {
        Assert.Empty(new PdfLinkService().ExtractNamedDestinations(MakePdf()));
    }

    [Fact]
    public void ExtractNamedDestinations_UnreadableBytes_ReturnsEmpty()
    {
        Assert.Empty(new PdfLinkService().ExtractNamedDestinations("not a pdf"u8.ToArray()));
    }
}
