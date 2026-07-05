using System.Runtime.InteropServices;

namespace RailReader.Core.Services;

/// <summary>
/// Centralised PDFium P/Invoke declarations.
/// </summary>
internal static class PdfiumNative
{
    private const string Lib = "pdfium";

    // Library lifecycle. FPDF_InitLibrary is idempotent in PDFium (guarded by a
    // global flag), so calling it is safe even when PDFtoImage has already
    // initialised the library.
    [DllImport(Lib)] internal static extern void FPDF_InitLibrary();

    // Document
    [DllImport(Lib)] internal static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);
    [DllImport(Lib)] internal static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Lib)] internal static extern int FPDF_GetSignatureCount(IntPtr document);

    // FPDF_GetLastError returns the failure reason for the most recent load on this
    // thread. Only meaningful immediately after a load returned IntPtr.Zero.
    // Declared as uint, NOT ulong: PDFium's C signature is `unsigned long`, which is
    // 32-bit on Windows (LLP64) and 64-bit on Linux/macOS (LP64). The error codes are
    // tiny and returned in EAX, so reading 32 bits is correct on both ABIs — whereas
    // marshalling as a 64-bit ulong on Windows reads the undefined upper half of RAX
    // and can make the == FPDF_ERR_PASSWORD comparison spuriously fail.
    [DllImport(Lib)] internal static extern uint FPDF_GetLastError();

    // FPDF_ERR_* codes (subset). 4 = the document is password-protected and the
    // supplied password was missing or incorrect.
    internal const uint FPDF_ERR_PASSWORD = 4;

    /// <summary>
    /// Loads a document from pinned bytes, translating a PDFium password failure into
    /// a <see cref="PdfPasswordRequiredException"/>. Returns <see cref="IntPtr.Zero"/>
    /// for every other load failure so the caller can apply its own fallback (graceful
    /// empty result vs. throw). Must be called inside <see cref="PdfiumGate.Lock"/>.
    /// </summary>
    internal static IntPtr LoadDocumentChecked(IntPtr data, int size, string? password, string? filePath = null)
    {
        var doc = FPDF_LoadMemDocument(data, size, password);
        if (doc == IntPtr.Zero && FPDF_GetLastError() == FPDF_ERR_PASSWORD)
            throw new PdfPasswordRequiredException(!string.IsNullOrEmpty(password), filePath);
        return doc;
    }

    // Pages
    [DllImport(Lib)] internal static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport(Lib)] internal static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport(Lib)] internal static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] internal static extern double FPDF_GetPageWidth(IntPtr page);
    [DllImport(Lib)] internal static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport(Lib)] internal static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Lib)] internal static extern bool FPDFPage_GetCropBox(IntPtr page,
        ref float left, ref float bottom, ref float right, ref float top);
    [DllImport(Lib)] internal static extern bool FPDFPage_GetMediaBox(IntPtr page,
        ref float left, ref float bottom, ref float right, ref float top);

    // Bookmarks
    [DllImport(Lib)] internal static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] internal static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] internal static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, IntPtr buffer, uint buflen);
    [DllImport(Lib)] internal static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] internal static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
    [DllImport(Lib)] internal static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);
    [DllImport(Lib)] internal static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

    // Links
    [DllImport(Lib)] internal static extern bool FPDFLink_Enumerate(IntPtr page, ref int startPos, out IntPtr linkAnnot);
    [DllImport(Lib)] internal static extern bool FPDFLink_GetAnnotRect(IntPtr linkAnnot, out FsRectF rect);
    [DllImport(Lib)] internal static extern IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);
    [DllImport(Lib)] internal static extern IntPtr FPDFLink_GetAction(IntPtr link);
    [DllImport(Lib)] internal static extern uint FPDFAction_GetType(IntPtr action);
    [DllImport(Lib)] internal static extern uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, IntPtr buffer, uint buflen);
    [DllImport(Lib)] internal static extern IntPtr FPDFLink_GetLinkAtPoint(IntPtr page, double x, double y);
    [DllImport(Lib)] internal static extern bool FPDFDest_GetLocationInPage(IntPtr dest,
        out int hasXVal, out int hasYVal, out int hasZoomVal,
        out float x, out float y, out float zoom);

    // PDFium action types
    internal const uint PDFACTION_GOTO = 1;      // Internal "go to destination"
    internal const uint PDFACTION_REMOTEGOTO = 2; // Remote "go to destination" (another PDF)
    internal const uint PDFACTION_URI = 3;        // Open a URI
    internal const uint PDFACTION_LAUNCH = 4;     // Launch an application

    [StructLayout(LayoutKind.Sequential)]
    internal struct FsRectF
    {
        public float Left;
        public float Bottom;
        public float Right;
        public float Top;
    }

    /// <summary>
    /// Computes the PDF-user-space ↔ page-point-space transform for a loaded page,
    /// honouring both the CropBox offset and the page's /Rotate attribute.
    /// PDFium geometry APIs (char boxes, annotation rects, link rects) return
    /// coordinates in <b>unrotated</b> MediaBox space, while the rendered pixmap
    /// (and PDFtoImage's GetPageSize) honour /Rotate — so the transform must fold
    /// the rotation in for overlays to line up.
    /// <paramref name="extraQuarterTurns"/> composes an additional user-requested
    /// view rotation (clockwise quarter-turns) on top of the page's own /Rotate.
    /// </summary>
    internal static PageTransform GetPageTransform(IntPtr page, int extraQuarterTurns = 0)
    {
        float left = 0, bottom = 0, right = 0, top = 0;
        float cropLeft = 0, cropBottom = 0;
        double visibleWidth, visibleHeight;
        if (FPDFPage_GetCropBox(page, ref left, ref bottom, ref right, ref top) ||
            FPDFPage_GetMediaBox(page, ref left, ref bottom, ref right, ref top))
        {
            cropLeft = left;
            cropBottom = bottom;
            visibleWidth = right - left;
            visibleHeight = top - bottom;
        }
        else
        {
            // No boxes at all: fall back to the page dimensions, un-rotating them
            // (FPDF_GetPageWidth/Height return display dimensions, i.e. post-/Rotate).
            double w = FPDF_GetPageWidth(page), h = FPDF_GetPageHeight(page);
            bool odd = (FPDFPage_GetRotation(page) & 1) != 0;
            visibleWidth = odd ? h : w;
            visibleHeight = odd ? w : h;
        }

        int rotation = ((FPDFPage_GetRotation(page) + extraQuarterTurns) % 4 + 4) % 4;
        return new PageTransform(cropLeft, cropBottom, (float)visibleWidth, (float)visibleHeight, rotation);
    }

    // Document creation & page copying
    [DllImport(Lib)] internal static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Lib)] internal static extern bool FPDF_ImportPages(IntPtr destDoc, IntPtr srcDoc,
        [MarshalAs(UnmanagedType.LPStr)] string? pageRange, int insertIndex);
    [DllImport(Lib)] internal static extern bool FPDF_SaveAsCopy(
        IntPtr document, ref FpdfFileWrite fileWrite, uint flags);
    [DllImport(Lib)] internal static extern bool FPDFPage_RemoveAnnot(IntPtr page, int index);

    // FPDF_SaveAsCopy flags
    internal const uint FPDF_INCREMENTAL = 1;
    internal const uint FPDF_NO_INCREMENTAL = 2;
    internal const uint FPDF_REMOVE_SECURITY = 3;

    // Annotation creation
    [DllImport(Lib)] internal static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Lib)] internal static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_SetRect(IntPtr annot, ref FsRectF rect);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_SetColor(IntPtr annot, int colorType,
        uint r, uint g, uint b, uint a);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_SetBorder(IntPtr annot,
        float horizontalRadius, float verticalRadius, float borderWidth);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_SetStringValue(IntPtr annot,
        [MarshalAs(UnmanagedType.LPStr)] string key, IntPtr value);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_AppendAttachmentPoints(
        IntPtr annot, ref FsQuadPointsF quadPoints);
    [DllImport(Lib)] internal static extern int FPDFAnnot_AddInkStroke(
        IntPtr annot, FsPointF[] points, nuint pointCount);

    // Annotation reading (PR 1 — view native annotations)
    [DllImport(Lib)] internal static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Lib)] internal static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Lib)] internal static extern int FPDFPage_GetAnnotIndex(IntPtr page, IntPtr annot);
    [DllImport(Lib)] internal static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Lib)] internal static extern int FPDFAnnot_GetFlags(IntPtr annot);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_GetRect(IntPtr annot, out FsRectF rect);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_GetColor(IntPtr annot, int colorType,
        out uint r, out uint g, out uint b, out uint a);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_GetBorder(IntPtr annot,
        out float horizontalRadius, out float verticalRadius, out float borderWidth);
    [DllImport(Lib)] internal static extern nuint FPDFAnnot_CountAttachmentPoints(IntPtr annot);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_GetAttachmentPoints(IntPtr annot,
        nuint quadIndex, out FsQuadPointsF quadPoints);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_HasKey(IntPtr annot,
        [MarshalAs(UnmanagedType.LPStr)] string key);
    [DllImport(Lib)] internal static extern uint FPDFAnnot_GetStringValue(IntPtr annot,
        [MarshalAs(UnmanagedType.LPStr)] string key, IntPtr buffer, uint buflen);
    [DllImport(Lib)] internal static extern bool FPDFAnnot_GetNumberValue(IntPtr annot,
        [MarshalAs(UnmanagedType.LPStr)] string key, out float value);
    [DllImport(Lib)] internal static extern int FPDFAnnot_GetInkListCount(IntPtr annot);
    [DllImport(Lib)] internal static extern uint FPDFAnnot_GetInkListPath(IntPtr annot,
        uint pathIndex, [Out] FsPointF[]? buffer, uint length);
    [DllImport(Lib)] internal static extern IntPtr FPDFAnnot_GetLinkedAnnot(IntPtr annot,
        [MarshalAs(UnmanagedType.LPStr)] string key);

    /// <summary>
    /// Reads a string-valued annotation entry (e.g. /Contents, /T, /M, /NM, /Subj)
    /// and decodes PDFium's UTF-16LE buffer. Returns null when the key is absent or empty.
    /// </summary>
    internal static string? ReadAnnotString(IntPtr annot, string key)
    {
        // First call with a null buffer to learn the required length (UTF-16LE bytes,
        // including the 2-byte NUL terminator).
        uint len = FPDFAnnot_GetStringValue(annot, key, IntPtr.Zero, 0);
        if (len <= 2) return null; // empty string is just the terminator

        var buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            FPDFAnnot_GetStringValue(annot, key, buffer, len);
            return Marshal.PtrToStringUni(buffer, (int)(len / 2) - 1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Annotation subtype constants (PDFium FPDF_ANNOTATION_SUBTYPE)
    internal const int FPDF_ANNOT_TEXT = 1;
    internal const int FPDF_ANNOT_LINK = 2;
    internal const int FPDF_ANNOT_FREETEXT = 3;
    internal const int FPDF_ANNOT_SQUARE = 5;
    internal const int FPDF_ANNOT_HIGHLIGHT = 9;
    internal const int FPDF_ANNOT_UNDERLINE = 10;
    internal const int FPDF_ANNOT_SQUIGGLY = 11;
    internal const int FPDF_ANNOT_STRIKEOUT = 12;
    internal const int FPDF_ANNOT_CARET = 14;
    internal const int FPDF_ANNOT_INK = 15;
    internal const int FPDF_ANNOT_POPUP = 16;
    internal const int FPDF_ANNOT_FLAG_PRINT = 4;
    internal const int FPDFANNOT_COLORTYPE_COLOR = 0;
    internal const int FPDFANNOT_COLORTYPE_INTERIOR = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FsQuadPointsF
    {
        public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FsPointF { public float X, Y; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int WriteBlockDelegate(IntPtr self, IntPtr data, uint size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FpdfFileWrite
    {
        public int Version;
        public IntPtr WriteBlock;
    }

    // (Point conversions live on PageTransform; see GetPageTransform.)

    // Text
    [DllImport(Lib)] internal static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] internal static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Lib)] internal static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Lib)] internal static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Lib)] internal static extern bool FPDFText_GetCharBox(IntPtr textPage, int index,
        ref double left, ref double right, ref double bottom, ref double top);
    [DllImport(Lib)] internal static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);
    [DllImport(Lib)] internal static extern bool FPDFText_GetRect(IntPtr textPage, int rectIndex,
        ref double left, ref double top, ref double right, ref double bottom);
}
