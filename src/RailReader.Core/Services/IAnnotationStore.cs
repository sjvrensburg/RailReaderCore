using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Persistence boundary for annotation files. Implementations decide where
/// and how to persist (filesystem JSON on desktop, IndexedDB on web, etc.).
/// All methods are called from the UI thread.
/// </summary>
public interface IAnnotationStore
{
    /// <summary>
    /// Load annotations for a PDF, or null if none exist. <paramref name="password"/>
    /// unlocks an encrypted source PDF for stores that read annotations out of the
    /// document itself (e.g. the native /Annots store); sidecar stores ignore it.
    /// </summary>
    AnnotationFile? Load(string pdfPath, string? password = null);

    /// <summary>Persist annotations for a PDF. Returns true on success.</summary>
    bool Save(string pdfPath, AnnotationFile annotations, string? password = null);

    /// <summary>Remove any persisted annotations for a PDF. Returns true on success.</summary>
    bool Delete(string pdfPath, string? password = null);
}
