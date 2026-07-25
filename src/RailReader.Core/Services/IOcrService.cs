using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// How much OCR work to do on a page that has no text layer. Higher modes cost more:
/// detection is one pass over the page, transcription is a further pass per detected line.
/// </summary>
public enum OcrMode
{
    /// <summary>No OCR. Scanned pages keep the pixel-projection line fallback.</summary>
    Off = 0,

    /// <summary>
    /// Detect text lines but do not transcribe them. Gives rail mode real line geometry on
    /// scanned pages — strictly better than pixel projection — for the cost of the detection
    /// model alone, with no per-line recognition. Produces no text, so search, export and
    /// VLM prompts are unaffected.
    /// </summary>
    Lines = 1,

    /// <summary>
    /// Detect and transcribe. Yields per-character boxes and page text, so a scanned page
    /// takes the same char-clustering line detection, reading-order tie-break, table
    /// row/cell detection, search and export paths as a born-digital one. Substantially
    /// more expensive than <see cref="Lines"/>.
    /// </summary>
    Full = 2,
}

/// <summary>
/// Optical character recognition for pages with no usable text layer.
///
/// <para>
/// Implementations live in sibling packages (see <c>RailReader.Core.Ocr.RapidOcr</c>) so
/// Core stays free of native and model dependencies. The analysis worker owns the instance
/// and calls it from its own thread only, so implementations need not be thread-safe — but
/// they must not touch PDFium or any UI state.
/// </para>
/// </summary>
public interface IOcrService : IDisposable
{
    /// <summary>
    /// Recognises text in an RGB pixmap (3 bytes per pixel, row-major, no padding) — the
    /// same buffer shape the layout analyzers consume, so the caller can reuse the pixmap it
    /// already rendered.
    /// </summary>
    /// <param name="mode">
    /// Work to perform. <see cref="OcrMode.Off"/> must return <see cref="OcrPage.Empty"/>.
    /// </param>
    /// <returns>Lines in the pixmap's own pixel space; never null.</returns>
    OcrPage Recognize(byte[] rgbBytes, int width, int height, OcrMode mode, CancellationToken ct = default);
}
