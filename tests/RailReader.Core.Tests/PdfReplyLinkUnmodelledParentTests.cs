using System.Text;
using System.Text.RegularExpressions;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Replies whose parent is an annotation the reader does not model (a Circle here — likewise Stamp,
/// Line, Polygon, FileAttachment, …). The model cannot hold the parent, so the writer used to treat
/// the reply as parentless and clear its /IRT on every save.
/// </summary>
public class PdfReplyLinkUnmodelledParentTests
{
    /// <summary>
    /// One page: a Circle (object 4, with or without /NM), a Text reply to it (object 5) and a Highlight
    /// reply to it (object 6), both linked by /IRT. Hand-built, classic xref.
    /// </summary>
    private static byte[] CirclePdf(bool circleHasNm = true, string replyType = "R")
    {
        string nm = circleHasNm ? "/NM (circle-1) " : "";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 7 0 R /Annots [4 0 R 5 0 R 6 0 R] >>",
            $"<< /Type /Annot /Subtype /Circle /Rect [20 20 80 80] /C [1 0 0] {nm}/T (Acro) /Contents (circled) /F 4 >>",
            $"<< /Type /Annot /Subtype /Text /Rect [90 60 114 84] /C [1 1 0] /NM (reply-1) /T (Bob) /Contents (why this?) /F 4 /IRT 4 0 R /RT /{replyType} >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [72 134 152 150] /QuadPoints [72 150 152 150 72 134 152 134] /C [1 1 0] /NM (hl-1) /T (Bob) /F 4 /IRT 4 0 R /RT /R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static Annotation ById(AnnotationFile f, string id) => f.Pages.Values.SelectMany(l => l).Single(a => a.NativeId == id);

    [Fact]
    public void An_Unchanged_Resave_Keeps_A_Reply_To_An_Unmodelled_Parent()
    {
        var source = CirclePdf();
        var model = new PdfAnnotationReader().Read(source);
        Assert.Equal("circle-1", ById(model, "reply-1").InReplyTo);   // the reader names it…
        Assert.DoesNotContain(model.Pages[0], a => a.NativeId == "circle-1");   // …but doesn't model it

        var saved = new PdfAnnotationWriter().WriteReconciled(source, model);

        var back = new PdfAnnotationReader().Read(saved);
        Assert.Equal("circle-1", ById(back, "reply-1").InReplyTo);
        Assert.Equal("circle-1", ById(back, "hl-1").InReplyTo);
    }

    [Fact]
    public void A_Group_Link_To_An_Unmodelled_Parent_Keeps_Its_Type()
    {
        var source = CirclePdf(replyType: "Group");
        var model = new PdfAnnotationReader().Read(source);

        var back = new PdfAnnotationReader().Read(new PdfAnnotationWriter().WriteReconciled(source, model));

        Assert.Equal(AnnotationReplyType.Group, ById(back, "reply-1").ReplyType);
        Assert.Equal("circle-1", ById(back, "reply-1").InReplyTo);
    }

    [Fact]
    public void A_Link_To_An_Annotation_With_No_NM_Is_Left_Alone()
    {
        // Nothing to name the parent by, so the model has no InReplyTo — the link still isn't stale.
        var source = CirclePdf(circleHasNm: false);
        var model = new PdfAnnotationReader().Read(source);
        Assert.Null(ById(model, "reply-1").InReplyTo);

        var saved = new PdfAnnotationWriter().WriteReconciled(source, model);

        var live = Encoding.Latin1.GetString(new PdfAnnotationWriter().WriteReconciled(saved, new PdfAnnotationReader().Read(saved)));
        Assert.Equal(2, Regex.Matches(live, @"/IRT\s*\d+\s+\d+\s+R").Count);
    }

    [Fact]
    public void Editing_A_Reply_To_An_Unmodelled_Parent_Restores_Its_Link()
    {
        // A changed highlight is recreated, which drops /IRT; the link must be rewritten to the circle.
        var source = CirclePdf();
        var model = new PdfAnnotationReader().Read(source);
        ById(model, "hl-1").Color = "#00FF00";
        ById(model, "reply-1").Contents = "edited too";

        var back = new PdfAnnotationReader().Read(new PdfAnnotationWriter().WriteReconciled(source, model));

        Assert.Equal("circle-1", ById(back, "hl-1").InReplyTo);
        Assert.Equal("circle-1", ById(back, "reply-1").InReplyTo);
        Assert.Equal("edited too", ById(back, "reply-1").EffectiveContents);
    }

    [Fact]
    public void Unlinking_A_Reply_From_A_Modelled_Parent_Still_Clears_The_Link()
    {
        var parent = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], NativeId = "p" };
        var reply = new TextNoteAnnotation { X = 72, Y = 120, Text = "re", NativeId = "r", InReplyTo = "p" };
        var writer = new PdfAnnotationWriter();
        var first = writer.WriteReconciled(AnnotationTestHelpers.PlainPdfBytes(), AnnotationTestHelpers.OnePage(parent, reply));

        reply.InReplyTo = null;
        var second = writer.WriteReconciled(first, AnnotationTestHelpers.OnePage(parent, reply));

        Assert.Null(ById(new PdfAnnotationReader().Read(second), "r").InReplyTo);
    }

    private static byte[] Build(string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The object number of the (one) Circle in <paramref name="pdf"/>'s live objects.</summary>
    private static int CircleObject(string pdf)
    {
        var m = Regex.Matches(pdf, @"(\d+)\s+0\s+obj\s*<<(?:(?!endobj).)*?/Subtype\s*/Circle", RegexOptions.Singleline);
        return int.Parse(m[^1].Groups[1].Value);
    }

    private static string Live(byte[] pdf)
        => Encoding.Latin1.GetString(new PdfAnnotationWriter().WriteReconciled(pdf, new PdfAnnotationReader().Read(pdf)));

    [Fact]
    public void A_Recreated_Reply_Keeps_Its_Link_To_A_Parent_With_No_NM()
    {
        // The model can't name the parent (InReplyTo is null), so the link is carried over from the old
        // reply's own /IRT before it is deleted and recreated.
        var source = CirclePdf(circleHasNm: false);
        var model = new PdfAnnotationReader().Read(source);
        ById(model, "hl-1").Color = "#00FF00";

        var saved = new PdfAnnotationWriter().WriteReconciled(source, model);

        var live = Live(saved);
        int circle = CircleObject(live);
        Assert.Equal(2, Regex.Matches(live, $@"/IRT\s*{circle}\s+0\s+R").Count);
        Assert.Equal("#00FF00", ById(new PdfAnnotationReader().Read(saved), "hl-1").Color, ignoreCase: true);
        Assert.DoesNotContain("RRM", live);
    }

    [Fact]
    public void A_Carried_Link_Keeps_Its_Group_Type()
    {
        var source = Build(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 6 0 R /Annots [4 0 R 5 0 R] >>",
            "<< /Type /Annot /Subtype /Circle /Rect [20 20 80 80] /C [1 0 0] /F 4 >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [72 134 152 150] /QuadPoints [72 150 152 150 72 134 152 134] /C [1 1 0] /NM (hl-1) /F 4 /IRT 4 0 R /RT /Group >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        ]);
        var model = new PdfAnnotationReader().Read(source);
        ById(model, "hl-1").Color = "#00FF00";

        var live = Live(new PdfAnnotationWriter().WriteReconciled(source, model));

        // PDFium orders keys alphabetically, so look for both inside the highlight's dictionary.
        var highlight = Regex.Match(live, @"<<[^>]*/Subtype\s*/Highlight[^>]*>>").Value;
        Assert.Matches($@"/IRT\s*{CircleObject(live)}\s+0\s+R", highlight);
        Assert.Matches(@"/RT\s*/Group", highlight);
    }

    [Fact]
    public void A_Reply_Keeps_Its_Link_To_An_Unmodelled_Parent_On_A_Page_The_Model_Does_Not_Hold()
    {
        // Page 1 holds only the Circle, so the model has no page 1; the parent is still found there.
        var source = Build(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 7 0 R /Annots [6 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 7 0 R /Annots [5 0 R] >>",
            "<< /Type /Annot /Subtype /Circle /Rect [20 20 80 80] /C [1 0 0] /NM (circle-1) /F 4 >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [72 134 152 150] /QuadPoints [72 150 152 150 72 134 152 134] /C [1 1 0] /NM (hl-1) /F 4 /IRT 5 0 R /RT /R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        ]);
        var model = new PdfAnnotationReader().Read(source);
        Assert.False(model.Pages.TryGetValue(1, out var p1) && p1.Count > 0);
        Assert.Equal("circle-1", ById(model, "hl-1").InReplyTo);
        ById(model, "hl-1").Color = "#00FF00";

        var saved = new PdfAnnotationWriter().WriteReconciled(source, model);

        Assert.Equal("circle-1", ById(new PdfAnnotationReader().Read(saved), "hl-1").InReplyTo);
        Assert.DoesNotContain("RRM", Live(saved));
    }
}
