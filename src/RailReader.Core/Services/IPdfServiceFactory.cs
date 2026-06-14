namespace RailReader.Core.Services;

/// <summary>
/// Factory for creating platform-specific PDF service implementations.
/// Injected into DocumentController to decouple Core from a specific PDF library.
/// </summary>
public interface IPdfServiceFactory
{
    /// <summary>
    /// Opens the PDF at <paramref name="filePath"/>. For an encrypted document,
    /// pass the user or owner <paramref name="password"/>. Throws
    /// <see cref="PdfPasswordRequiredException"/> when the document is encrypted and
    /// the password is missing or incorrect.
    /// </summary>
    IPdfService CreatePdfService(string filePath, string? password = null);
    IPdfTextService CreatePdfTextService();
    IPdfLinkService CreatePdfLinkService();
}
