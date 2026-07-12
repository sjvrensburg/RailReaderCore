using System.Runtime.InteropServices;

namespace RailReader.Core.Services;

/// <summary>
/// A single-entry (MRU-of-1) cache of a loaded PDFium document handle, keyed by
/// the <b>identity</b> of the source byte array plus the password. Callers such as
/// <see cref="PdfTextService"/> and <see cref="PdfLinkService"/> are handed the same
/// <c>byte[]</c> instance for every page of an open document, so per-page bursts
/// (analysis sweeps extract text page-by-page) reuse one parsed document instead of
/// paying a full xref/trailer parse per call — all while holding the global
/// <see cref="PdfiumGate"/>.
///
/// <para><b>Threading/lifetime contract:</b> every member must be called inside
/// <c>lock (PdfiumGate.Lock)</c>, and the returned handle must only be used within
/// that same lock scope and never closed by the caller. Reference-identity keying is
/// safe because PDF bytes are never mutated in place — a re-read or edited document
/// arrives as a new array and simply misses the cache (the stale handle is closed
/// before the new load). The finalizer closes any remaining handle when the owning
/// service becomes unreachable; it serialises through the gate, and all use sites
/// hold the gate for the full duration of handle use, so the finalizer can never
/// close a document out from under a live call. Closing from the finalizer thread is
/// safe because nothing in this codebase ever calls <c>FPDF_DestroyLibrary</c>.</para>
/// </summary>
internal sealed class CachedPdfDocument
{
    private byte[]? _bytes;
    private string? _password;
    private GCHandle _pinned;
    private IntPtr _doc;

    /// <summary>
    /// Returns a document handle for <paramref name="pdfBytes"/>, reusing the cached
    /// handle when the same array instance (and password) was loaded previously.
    /// Returns <see cref="IntPtr.Zero"/> on load failure (failures are not cached, so
    /// <c>FPDF_GetLastError</c> semantics for the caller are unchanged).
    /// Must be called inside <c>lock (PdfiumGate.Lock)</c>.
    /// </summary>
    public IntPtr GetOrLoad(byte[] pdfBytes, string? password)
    {
        if (_doc != IntPtr.Zero && ReferenceEquals(_bytes, pdfBytes) && _password == password)
            return _doc;

        CloseCore();

        var pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
        var doc = PdfiumNative.FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, password);
        if (doc == IntPtr.Zero)
        {
            pinned.Free();
            return IntPtr.Zero;
        }

        _bytes = pdfBytes;
        _password = password;
        _pinned = pinned;
        _doc = doc;
        return doc;
    }

    private void CloseCore()
    {
        if (_doc != IntPtr.Zero)
        {
            PdfiumNative.FPDF_CloseDocument(_doc);
            _doc = IntPtr.Zero;
        }
        if (_pinned.IsAllocated) _pinned.Free();
        _bytes = null;
        _password = null;
    }

    ~CachedPdfDocument()
    {
        try
        {
            lock (PdfiumGate.Lock)
            {
                CloseCore();
            }
        }
        catch
        {
            // Never throw from a finalizer.
        }
    }
}
