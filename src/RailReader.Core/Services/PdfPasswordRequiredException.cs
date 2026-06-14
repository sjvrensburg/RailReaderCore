namespace RailReader.Core.Services;

/// <summary>
/// Thrown when a PDF cannot be opened because it is encrypted and the supplied
/// password was missing or incorrect. Consumers should catch this at the
/// document-open boundary (e.g. <see cref="DocumentController.CreateDocument"/>),
/// prompt the user for a password, and retry the open with it.
///
/// <para><see cref="WrongPassword"/> distinguishes the two cases: <c>false</c>
/// means no password was supplied for an encrypted document (prompt for one);
/// <c>true</c> means a password was supplied but rejected (prompt again /
/// surface "incorrect password").</para>
/// </summary>
public sealed class PdfPasswordRequiredException : Exception
{
    /// <summary>The PDF path or display name, when known.</summary>
    public string? FilePath { get; }

    /// <summary>
    /// True when a non-empty password was supplied but PDFium still rejected the
    /// document (incorrect password); false when no password was supplied for an
    /// encrypted document.
    /// </summary>
    public bool WrongPassword { get; }

    public PdfPasswordRequiredException(bool wrongPassword, string? filePath = null)
        : base(wrongPassword
            ? $"The password supplied for '{filePath ?? "the PDF"}' is incorrect."
            : $"'{filePath ?? "The PDF"}' is password-protected and requires a password to open.")
    {
        WrongPassword = wrongPassword;
        FilePath = filePath;
    }
}
