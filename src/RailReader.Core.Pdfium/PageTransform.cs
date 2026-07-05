namespace RailReader.Core.Services;

/// <summary>
/// Bidirectional transform between PDF user space (bottom-left origin, Y-up,
/// unrotated MediaBox coordinates — what PDFium's geometry APIs speak) and
/// page-point space (top-left origin, Y-down, in the page's <b>displayed</b>
/// orientation — what the rendered pixmap, the camera, and all overlays speak).
///
/// Folds in two things the old crop-offset/Y-flip transform ignored:
/// the page /Rotate attribute and (optionally) an extra user-requested view
/// rotation, both expressed as clockwise quarter-turns. The mapping constants
/// were established empirically against PDFtoImage-rendered pixmaps
/// (tools/rotation-probe): ink coverage 1.000 for all four rotations.
/// </summary>
internal readonly struct PageTransform
{
    /// <summary>CropBox (or MediaBox) origin offset in PDF user space.</summary>
    public readonly float CropLeft, CropBottom;

    /// <summary>Visible page size in the <b>unrotated</b> frame, in points.</summary>
    public readonly float VisibleWidth, VisibleHeight;

    /// <summary>Total clockwise quarter-turns applied at display time (0–3).</summary>
    public readonly int Rotation;

    public PageTransform(float cropLeft, float cropBottom,
        float visibleWidth, float visibleHeight, int rotation)
    {
        CropLeft = cropLeft;
        CropBottom = cropBottom;
        VisibleWidth = visibleWidth;
        VisibleHeight = visibleHeight;
        Rotation = ((rotation % 4) + 4) % 4;
    }

    /// <summary>Displayed page width in points (axes swap on 90°/270°).</summary>
    public float DisplayWidth => (Rotation & 1) == 0 ? VisibleWidth : VisibleHeight;

    /// <summary>Displayed page height in points (axes swap on 90°/270°).</summary>
    public float DisplayHeight => (Rotation & 1) == 0 ? VisibleHeight : VisibleWidth;

    /// <summary>
    /// True when the displayed X axis derives from PDF-space Y (90°/270° turns).
    /// Used when only one axis of a coordinate pair is known (link destinations).
    /// </summary>
    public bool AxesSwapped => (Rotation & 1) != 0;

    /// <summary>PDF user space → displayed page-point space.</summary>
    public (float X, float Y) PdfToPage(double pdfX, double pdfY)
    {
        float x = (float)(pdfX - CropLeft);
        float y = (float)(pdfY - CropBottom);
        return Rotation switch
        {
            1 => (y, x),
            2 => (VisibleWidth - x, y),
            3 => (VisibleHeight - y, VisibleWidth - x),
            _ => (x, VisibleHeight - y),
        };
    }

    /// <summary>Displayed page-point space → PDF user space (inverse of <see cref="PdfToPage"/>).</summary>
    public (float X, float Y) PageToPdf(float pageX, float pageY)
    {
        var (x, y) = Rotation switch
        {
            1 => (pageY, pageX),
            2 => (VisibleWidth - pageX, pageY),
            3 => (VisibleWidth - pageY, VisibleHeight - pageX),
            _ => (pageX, VisibleHeight - pageY),
        };
        return (x + CropLeft, y + CropBottom);
    }

    /// <summary>
    /// PDF user-space rect (any corner order) → displayed page-point rect
    /// as (Left, Top, Right, Bottom) with Left ≤ Right and Top ≤ Bottom.
    /// </summary>
    public (float Left, float Top, float Right, float Bottom) PdfRectToPage(
        double left, double bottom, double right, double top)
    {
        var (x1, y1) = PdfToPage(left, bottom);
        var (x2, y2) = PdfToPage(right, top);
        return (Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    /// <summary>
    /// Displayed page-point rect (X, Y = top-left, W, H) → PDF user-space rect
    /// as (Left, Bottom, Right, Top) with Left ≤ Right and Bottom ≤ Top.
    /// </summary>
    public (float Left, float Bottom, float Right, float Top) PageRectToPdf(
        float x, float y, float w, float h)
    {
        var (x1, y1) = PageToPdf(x, y);
        var (x2, y2) = PageToPdf(x + w, y + h);
        return (Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }
}
