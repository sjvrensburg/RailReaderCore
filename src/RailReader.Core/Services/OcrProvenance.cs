namespace RailReader.Core.Services;

/// <summary>
/// The signatures of a RailReader OCR text layer baked into a PDF (issue #155), shared
/// by the writer (<c>PdfTextLayerWriter</c>) and every text backend so they agree on
/// when read-back text is flagged <see cref="Models.PageText.FromOcr"/>.
///
/// <para>A page is OCR-derived when it carries text that is either inside a
/// marked-content sequence tagged <see cref="MarkName"/>, or invisible (render mode 3)
/// and set in the <see cref="FontName"/> glyphless font — the fallback for a producer
/// that strips marked content. Both backends apply exactly this rule. Invisible text
/// from other producers is not matched: it cannot be told apart from deliberately
/// hidden native text, and a backend that guessed would report a different
/// provenance for the same file than one that didn't.</para>
/// </summary>
public static class OcrProvenance
{
    /// <summary>Marked-content tag stamped on every OCR text object.</summary>
    public const string MarkName = "RailReaderOCR";

    /// <summary>BaseFont name of the synthesised glyphless OCR font (matched by substring, so a subset prefix is tolerated).</summary>
    public const string FontName = "RailReaderGlyphless";
}
