namespace RailReader.Core.Services;

/// <summary>
/// A document's descriptive metadata from its Info dictionary. Each field is null when the entry is
/// absent or blank (after trimming), so a host can fall back to the file name with <c>??</c>.
/// </summary>
public sealed record PdfMetadata(
    string? Title,
    string? Author,
    string? Subject,
    string? Keywords,
    string? Creator,
    string? Producer)
{
    /// <summary>No metadata: every field null.</summary>
    public static readonly PdfMetadata Empty = new(null, null, null, null, null, null);

    /// <summary>Whether every field is null.</summary>
    public bool IsEmpty => this == Empty;
}

/// <summary>
/// Optional backend capability: a document's <see cref="PdfMetadata"/>. Like
/// <see cref="IPdfRulingService"/>, it is discovered by casting the service Core was handed
/// (<c>if (textService is IPdfMetadataService m)</c>) rather than by widening
/// <see cref="IPdfServiceFactory"/>. Stateless over the document bytes, like
/// <see cref="IPdfTextService"/>, so it works on a document opened only to extract text. A
/// decorator around a service that implements it must forward it, or the capability disappears.
/// </summary>
public interface IPdfMetadataService
{
    /// <summary>
    /// Reads the document's metadata. Never null: returns <see cref="PdfMetadata.Empty"/> when the
    /// document has none or can't be opened (a wrong password included). Thread-safe.
    /// </summary>
    PdfMetadata GetMetadata(byte[] pdfBytes, string? password = null);
}
