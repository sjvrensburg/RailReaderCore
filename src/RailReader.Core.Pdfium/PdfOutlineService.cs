using System.Runtime.InteropServices;
using RailReader.Core;
using RailReader.Core.Models;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>
/// Extracts PDF bookmarks/outline using PDFium's native API.
/// Relies on PDFtoImage having already loaded the native pdfium library.
/// </summary>
public sealed class PdfOutlineService : IPdfOutlineService
{

    public List<OutlineEntry> Extract(byte[] pdfBytes, string? password = null)
    {
        lock (PdfiumGate.Lock)
        {
            // Outline extraction can be the first PDFium touch (headless/library
            // use before any render) — initialise defensively like the sibling
            // services; cheap flag check after the first call.
            PdfiumResolver.EnsureLibraryInitialized();

            IntPtr doc = IntPtr.Zero;
            GCHandle pinned = default;
            try
            {
                pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
                // Read path is fail-soft: a wrong/missing password yields IntPtr.Zero and an
                // empty outline rather than throwing (the open boundary already validated the
                // password). The write/open paths use LoadDocumentChecked to throw instead.
                doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, password);
                if (doc == IntPtr.Zero)
                    return [];

                return ExtractFromDocument(doc);
            }
            catch (Exception ex)
            {
                RailReaderLogging.Logger.Error("[Outline] Failed to extract", ex);
                return [];
            }
            finally
            {
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                if (pinned.IsAllocated) pinned.Free();
            }
        }
    }

    /// <summary>
    /// Walks the bookmark tree of an already-open document. Lets a caller that has loaded
    /// the document for another reason (e.g. <c>SkiaPdfService</c> reading the page count in
    /// the same pass) read the outline without a second full load. Mirrors the PdfPig
    /// backend's <c>Extract(PdfDocument)</c> overload. Caller holds <see cref="PdfiumGate.Lock"/>.
    /// </summary>
    internal static List<OutlineEntry> ExtractFromDocument(IntPtr doc)
    {
        var result = new List<OutlineEntry>();
        var root = FPDFBookmark_GetFirstChild(doc, IntPtr.Zero);
        ReadBookmarks(doc, root, result);
        return result;
    }

    private static void ReadBookmarks(IntPtr doc, IntPtr bookmark, List<OutlineEntry> entries)
    {
        while (bookmark != IntPtr.Zero)
        {
            var entry = new OutlineEntry
            {
                Title = GetBookmarkTitle(bookmark),
                Page = GetBookmarkPage(doc, bookmark),
            };

            var child = FPDFBookmark_GetFirstChild(doc, bookmark);
            if (child != IntPtr.Zero)
                ReadBookmarks(doc, child, entry.Children);

            entries.Add(entry);
            bookmark = FPDFBookmark_GetNextSibling(doc, bookmark);
        }
    }

    private static string GetBookmarkTitle(IntPtr bookmark)
    {
        // First call gets required buffer size (in bytes, including null terminator)
        int len = (int)FPDFBookmark_GetTitle(bookmark, IntPtr.Zero, 0);
        if (len <= 0) return "";

        var buffer = Marshal.AllocHGlobal(len);
        try
        {
            FPDFBookmark_GetTitle(bookmark, buffer, (uint)len);
            // PDFium returns UTF-16LE
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? GetBookmarkPage(IntPtr doc, IntPtr bookmark)
    {
        var dest = FPDFBookmark_GetDest(doc, bookmark);
        if (dest == IntPtr.Zero)
        {
            var action = FPDFBookmark_GetAction(bookmark);
            if (action != IntPtr.Zero)
                dest = FPDFAction_GetDest(doc, action);
        }
        if (dest == IntPtr.Zero) return null;

        int page = FPDFDest_GetDestPageIndex(doc, dest);
        return page >= 0 ? page : null;
    }
}
