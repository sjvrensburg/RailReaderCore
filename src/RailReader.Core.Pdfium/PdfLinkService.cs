using System.Runtime.InteropServices;
using RailReader.Core.Models;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>
/// Extracts clickable link regions from PDF pages via PDFium P/Invoke.
/// Returns links in page-point space (origin top-left, Y-down).
/// </summary>
public sealed class PdfLinkService : IPdfLinkService
{

    private static readonly List<PdfLink> s_empty = [];

    public List<PdfLink> ExtractPageLinks(byte[] pdfBytes, int pageIndex, string? password = null)
    {
        lock (PdfiumGate.Lock)
        {
            IntPtr doc = IntPtr.Zero;
            IntPtr page = IntPtr.Zero;
            GCHandle pinned = default;

            try
            {
                pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
                // Read path is fail-soft: a wrong/missing password yields IntPtr.Zero and an
                // empty result rather than throwing (the open boundary validated the password).
                // The write/open paths use LoadDocumentChecked to throw instead.
                doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, password);
                if (doc == IntPtr.Zero) return s_empty;

                page = FPDF_LoadPage(doc, pageIndex);
                if (page == IntPtr.Zero) return s_empty;

                var tx = GetPageTransform(page);
                var links = new List<PdfLink>();
                // Destinations can target other pages with their own /Rotate; cache
                // each target page's transform so repeated links stay cheap.
                var destTransforms = new Dictionary<int, PageTransform> { [pageIndex] = tx };

                int startPos = 0;
                while (FPDFLink_Enumerate(page, ref startPos, out IntPtr linkAnnot))
                {
                    if (!FPDFLink_GetAnnotRect(linkAnnot, out FsRectF fsRect))
                        continue;

                    var rect = ToPageRect(fsRect, tx);

                    var dest = ResolveDestination(doc, linkAnnot, destTransforms);
                    if (dest is null) continue;

                    links.Add(new PdfLink { Rect = rect, Destination = dest });
                }

                return links;
            }
            catch (Exception ex)
            {
                RailReaderLogging.Logger.Error($"[PdfLink] Failed to extract links for page {pageIndex}", ex);
                return s_empty;
            }
            finally
            {
                if (page != IntPtr.Zero) FPDF_ClosePage(page);
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                if (pinned.IsAllocated) pinned.Free();
            }
        }
    }

    private static PdfLinkDestination? ResolveDestination(IntPtr doc, IntPtr link,
        Dictionary<int, PageTransform> destTransforms)
    {
        // Try direct destination first (most internal links)
        IntPtr dest = FPDFLink_GetDest(doc, link);
        if (dest != IntPtr.Zero)
        {
            int pageIdx = FPDFDest_GetDestPageIndex(doc, dest);
            if (pageIdx >= 0)
                return MakePageDestination(doc, dest, pageIdx, destTransforms);
        }

        // Fall back to action
        IntPtr action = FPDFLink_GetAction(link);
        if (action == IntPtr.Zero) return null;

        uint actionType = FPDFAction_GetType(action);
        switch (actionType)
        {
            case PDFACTION_GOTO:
                dest = FPDFAction_GetDest(doc, action);
                if (dest != IntPtr.Zero)
                {
                    int pageIdx = FPDFDest_GetDestPageIndex(doc, dest);
                    if (pageIdx >= 0)
                        return MakePageDestination(doc, dest, pageIdx, destTransforms);
                }
                break;

            case PDFACTION_URI:
                uint len = FPDFAction_GetURIPath(doc, action, IntPtr.Zero, 0);
                if (len > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)len);
                    try
                    {
                        FPDFAction_GetURIPath(doc, action, buf, len);
                        string uri = Marshal.PtrToStringAnsi(buf, (int)len - 1) ?? "";
                        if (!string.IsNullOrWhiteSpace(uri))
                            return new UriDestination { Uri = uri };
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                break;
        }

        return null;
    }

    private static PageDestination MakePageDestination(IntPtr doc, IntPtr dest, int pageIdx,
        Dictionary<int, PageTransform> destTransforms)
    {
        float? pdfX = null, pdfY = null;
        if (FPDFDest_GetLocationInPage(dest, out int hasX, out int hasY, out _,
                out float x, out float y, out _))
        {
            if (hasX != 0) pdfX = x;
            if (hasY != 0) pdfY = y;
        }

        // Pre-resolve the target position into the destination page's page-point
        // space (CropBox- and /Rotate-aware) so navigation needs no PDF-space math.
        float? pageX = null, pageY = null;
        if ((pdfX is not null || pdfY is not null) &&
            TryGetTransform(doc, pageIdx, destTransforms, out var tx))
        {
            var (px, py) = tx.PdfToPage(pdfX ?? 0, pdfY ?? 0);
            // On 90°/270° pages each displayed axis derives from the other PDF axis;
            // only publish an axis whose source coordinate was actually specified.
            bool haveForX = tx.AxesSwapped ? pdfY is not null : pdfX is not null;
            bool haveForY = tx.AxesSwapped ? pdfX is not null : pdfY is not null;
            if (haveForX) pageX = px;
            if (haveForY) pageY = py;
        }

        return new PageDestination
        {
            PageIndex = pageIdx, PdfX = pdfX, PdfY = pdfY, PageX = pageX, PageY = pageY,
        };
    }

    private static bool TryGetTransform(IntPtr doc, int pageIdx,
        Dictionary<int, PageTransform> cache, out PageTransform tx)
    {
        if (cache.TryGetValue(pageIdx, out tx)) return true;

        IntPtr page = FPDF_LoadPage(doc, pageIdx);
        if (page == IntPtr.Zero) return false;
        try
        {
            tx = GetPageTransform(page);
            cache[pageIdx] = tx;
            return true;
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// Converts an FsRectF from PDF user space (Y-up, unrotated MediaBox) to a
    /// normalized RectF in displayed page-point space.
    /// </summary>
    private static RectF ToPageRect(FsRectF fsRect, PageTransform tx)
    {
        var (l, t, r, b) = tx.PdfRectToPage(fsRect.Left, fsRect.Bottom, fsRect.Right, fsRect.Top);
        return new RectF(l, t, r, b).Normalized();
    }
}
