using System.Reflection;
using System.Runtime.InteropServices;

namespace RailReader.Core.Services;

/// <summary>
/// Registers a DllImport resolver for the "pdfium" native library.
/// On Linux/macOS the NuGet package ships as libpdfium.so/.dylib in
/// runtimes/{rid}/native/. .NET's default probing normally finds it
/// (tries lib prefix + platform extension), but this resolver provides
/// an explicit fallback to guarantee resolution on all platforms.
///
/// Call <see cref="Initialize"/> once at startup, before any P/Invoke
/// into PDFium (PdfOutlineExtractor, PdfTextService).
/// PDFtoImage must be referenced so the native asset is deployed.
/// </summary>
public static class PdfiumResolver
{
    // Both flags are guarded by PdfiumGate.Lock — deliberately the ONLY lock used
    // here. Callers reach this method both while already holding the gate
    // (PdfTextService, PdfLinkService, PdfAnnotationWriter, …) and before taking
    // it (PdfAnnotationReader); a second dedicated init lock would create two
    // possible acquisition orders and an ABBA deadlock during the pre-init window.
    // The gate is reentrant, so gate-holding callers nest safely. The flags are
    // read via Volatile on the fast path and written only after the work completes,
    // so a thread that observes true is guaranteed the init actually finished.
    private static bool s_initialized;
    private static bool s_libraryInitialized;

    public static void Initialize()
    {
        if (Volatile.Read(ref s_initialized)) return;
        lock (PdfiumGate.Lock)
        {
            if (s_initialized) return;
            NativeLibrary.SetDllImportResolver(
                typeof(PdfiumResolver).Assembly,
                ResolvePdfium);
            Volatile.Write(ref s_initialized, true);
        }
    }

    /// <summary>
    /// Registers the DLL resolver and ensures the PDFium native library is
    /// initialised. Normally PDFtoImage (in the renderer package) initialises
    /// PDFium on first use, but Core.Pdfium services that touch PDFium directly —
    /// e.g. <see cref="PdfAnnotationReader"/> via the annotation store — may run
    /// before any render. This makes initialisation explicit and order-independent.
    /// <c>FPDF_InitLibrary</c> is idempotent, so this is safe alongside PDFtoImage.
    /// Thread-safe; may be called with or without <see cref="PdfiumGate"/> held.
    /// </summary>
    public static void EnsureLibraryInitialized()
    {
        Initialize();
        if (Volatile.Read(ref s_libraryInitialized)) return;
        lock (PdfiumGate.Lock)
        {
            if (s_libraryInitialized) return;
            PdfiumNative.FPDF_InitLibrary();
            Volatile.Write(ref s_libraryInitialized, true);
        }
    }

    private static IntPtr ResolvePdfium(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "pdfium")
            return IntPtr.Zero; // fall back to default resolution

        // Try default probing first (handles most cases)
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        // Explicit platform-specific fallback
        string ext, prefix;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ext = ".dll";
            prefix = "";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            ext = ".dylib";
            prefix = "lib";
        }
        else
        {
            ext = ".so";
            prefix = "lib";
        }

        string libFile = $"{prefix}pdfium{ext}";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory,
                "runtimes", RuntimeInformation.RuntimeIdentifier, "native", libFile),
            Path.Combine(AppContext.BaseDirectory, libFile),
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
