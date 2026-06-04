using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Regression tests for issue #34 — native PDF sticky notes (Text annotations) read into
/// <see cref="Annotation.Contents"/> rendered an empty popup because the renderer keyed off
/// the legacy <see cref="TextNoteAnnotation.Text"/> field (empty for <see cref="AnnotationSource.InPdf"/>
/// notes). The render path now uses <see cref="Annotation.EffectiveContents"/>, the same notion
/// the writer and equivalence already share.
/// </summary>
public class TextNoteContentsRenderTests
{
    /// <summary>Counts non-white pixels in a region after drawing the annotation.</summary>
    private static int NonWhitePixels(Annotation ann, int w, int h, (int X0, int Y0, int X1, int Y1) region)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            AnnotationRenderer.DrawAnnotation(canvas, ann, isSelected: false);
            canvas.Flush();
        }
        int n = 0;
        for (int y = region.Y0; y < region.Y1; y++)
            for (int x = region.X0; x < region.X1; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 250 || c.Green < 250 || c.Blue < 250) n++;
            }
        return n;
    }

    // The icon sits around [32,48]×[32,48] for a note at (40,40); the popup is drawn down-right
    // of it, so this region captures only the popup, never the icon.
    private static readonly (int, int, int, int) PopupRegion = (54, 50, 190, 110);

    [Fact]
    public void NoteFromPdf_WithContentsOnly_RendersPopup()
    {
        // Mirrors what PdfAnnotationReader produces for an Acrobat sticky note: body in
        // /Contents, legacy Text empty, Source.InPdf.
        var note = new TextNoteAnnotation
        {
            X = 40,
            Y = 40,
            Contents = "what does this mean?",
            Source = AnnotationSource.InPdf,
            IsExpanded = true,
        };

        Assert.Equal("", note.Text);
        Assert.True(NonWhitePixels(note, 220, 140, PopupRegion) > 10,
            "an in-PDF note whose body is only in /Contents must render its popup");
    }

    [Fact]
    public void NoteAuthoredInRailReader_WithTextOnly_StillRendersPopup()
    {
        // The legacy path must keep working: RailReader-authored notes carry the body in Text.
        var note = new TextNoteAnnotation { X = 40, Y = 40, Text = "a sticky note", IsExpanded = true };

        Assert.Null(note.Contents);
        Assert.True(NonWhitePixels(note, 220, 140, PopupRegion) > 10);
    }

    [Fact]
    public void EmptyNote_RendersNoPopup()
    {
        // Neither field set → only the icon draws, no popup. Guards against over-rendering.
        var note = new TextNoteAnnotation { X = 40, Y = 40, IsExpanded = true };

        Assert.Equal(0, NonWhitePixels(note, 220, 140, PopupRegion));
    }

    [Theory]
    [InlineData(null, "", "")]                       // nothing set
    [InlineData(null, "from-text", "from-text")]     // RailReader-authored: Text fallback
    [InlineData("from-contents", "", "from-contents")]
    [InlineData("from-contents", "from-text", "from-contents")] // Contents wins
    public void EffectiveContents_PrefersContents_FallsBackToText(string? contents, string text, string expected)
    {
        var note = new TextNoteAnnotation { Contents = contents, Text = text };
        Assert.Equal(expected, note.EffectiveContents);
    }

    [Fact]
    public void EffectiveContents_OnBaseAnnotation_IsContentsOrEmpty()
    {
        Assert.Equal("body", new FreeTextAnnotation { Contents = "body" }.EffectiveContents);
        Assert.Equal("", new FreeTextAnnotation().EffectiveContents);
    }
}
