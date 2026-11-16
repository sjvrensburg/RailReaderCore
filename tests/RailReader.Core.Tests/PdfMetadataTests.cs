using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>#203: document metadata through the PDFium backend's <see cref="IPdfMetadataService"/>.</summary>
public class PdfMetadataTests
{
    private static byte[] Pdf(SKDocumentPdfMetadata metadata)
    {
        using var stream = new MemoryStream();
        using (var doc = SKDocument.CreatePdf(stream, metadata))
        {
            var canvas = doc.BeginPage(200, 200);
            using var paint = new SKPaint { Color = SKColors.Black };
            using var font = new SKFont { Size = 12 };
            canvas.DrawText("hello", 20, 40, font, paint);
            doc.EndPage();
        }
        return stream.ToArray();
    }

    private static IPdfMetadataService Service()
        => Assert.IsAssignableFrom<IPdfMetadataService>(TestFixtures.CreatePdfFactory().CreatePdfTextService());

    [Fact]
    public void ReadsTheInfoDictionary()
    {
        var bytes = Pdf(new SKDocumentPdfMetadata
        {
            Title = "Day-ahead photovoltaic forecasting",
            Author = "Ada Lovelace, Émilie du Châtelet",
            Subject = "Short-term solar",
            Keywords = "forecasting; PV",
            Creator = "TestFixtures",
        });

        var m = Service().GetMetadata(bytes);

        Assert.Equal("Day-ahead photovoltaic forecasting", m.Title);
        Assert.Equal("Ada Lovelace, Émilie du Châtelet", m.Author);   // non-ASCII survives UTF-16
        Assert.Equal("Short-term solar", m.Subject);
        Assert.Equal("forecasting; PV", m.Keywords);
        Assert.Equal("TestFixtures", m.Creator);
        Assert.False(m.IsEmpty);
    }

    [Fact]
    public void MissingAndBlankEntriesAreNull()
    {
        var m = Service().GetMetadata(Pdf(new SKDocumentPdfMetadata { Title = "   ", Producer = null }));
        Assert.Null(m.Title);
        Assert.Null(m.Author);
        Assert.Null(m.Keywords);
    }

    [Fact]
    public void UnreadableDocumentGivesEmpty()
    {
        Assert.Same(PdfMetadata.Empty, Service().GetMetadata("not a pdf"u8.ToArray()));
        var encrypted = File.ReadAllBytes(TestFixtures.GetEncryptedTestPdfPath());
        Assert.Same(PdfMetadata.Empty, Service().GetMetadata(encrypted, "wrong"));
        Assert.NotNull(Service().GetMetadata(encrypted, TestFixtures.EncryptedPdfPassword));
    }

    [Fact]
    public void WorksAlongsideTextExtractionOnTheSameBytes()
    {
        var bytes = Pdf(new SKDocumentPdfMetadata { Title = "Shared" });
        var text = TestFixtures.CreatePdfFactory().CreatePdfTextService();
        Assert.Contains("hello", text.ExtractPageText(bytes, 0).Text);
        Assert.Equal("Shared", ((IPdfMetadataService)text).GetMetadata(bytes).Title);
        Assert.Contains("hello", text.ExtractPageText(bytes, 0).Text);
    }

    [Fact]
    public void ReadingAnotherDocumentsMetadataLeavesTheOpenDocumentCached()
    {
        var open = Pdf(new SKDocumentPdfMetadata { Title = "Open" });
        var other = Pdf(new SKDocumentPdfMetadata { Title = "Other" });
        var text = TestFixtures.CreatePdfFactory().CreatePdfTextService();
        text.ExtractPageText(open, 0);

        Assert.Equal("Other", ((IPdfMetadataService)text).GetMetadata(other).Title);
        Assert.Equal("Open", ((IPdfMetadataService)text).GetMetadata(open).Title);
        Assert.Contains("hello", text.ExtractPageText(open, 0).Text);
    }
}
