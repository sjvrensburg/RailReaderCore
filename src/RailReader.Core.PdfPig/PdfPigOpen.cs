using UglyToad.PdfPig;

namespace RailReader.Core.PdfPig;

/// <summary>
/// Shared helper for opening (possibly encrypted) PDFs with PdfPig. Returns null
/// when no password is supplied so the default open path is unchanged; otherwise
/// builds <see cref="ParsingOptions"/> carrying the password (PdfPig also tries it
/// as both the user and owner password).
/// </summary>
internal static class PdfPigOpen
{
    public static ParsingOptions? Options(string? password)
        => string.IsNullOrEmpty(password) ? null : new ParsingOptions { Password = password };
}
