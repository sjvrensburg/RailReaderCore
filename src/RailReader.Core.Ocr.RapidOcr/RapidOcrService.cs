using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RapidOcrNet;
using SkiaSharp;

namespace RailReader.Core.Ocr.RapidOcr;

/// <summary>
/// <see cref="IOcrService"/> backed by <see href="https://github.com/BobLd/RapidOcrNet">RapidOcrNet</see>
/// (PaddleOCR PP-OCR ONNX models). Recovers text lines — and, in
/// <see cref="OcrMode.Full"/>, the text itself with per-character boxes — from pages that
/// have no text layer, so scanned documents take the same reading path as born-digital ones.
///
/// <para>
/// <b>Resolution matters.</b> The service is handed the pixmap the analysis worker already
/// rendered for the layout model, which avoids a second rasterisation but means OCR quality
/// tracks that model's input size: at the 800&#160;px longest edge PP-DocLayoutV3 and Heron
/// ask for, body text is only a handful of pixels tall and recognition suffers. Pair OCR
/// with PP-DocLayout-S (1920&#160;px) for usable transcription, or use
/// <see cref="OcrMode.Lines"/> — line <i>detection</i> degrades far more gracefully than
/// recognition does.
/// </para>
/// <para>
/// Not thread-safe: the analysis worker owns one instance and calls it from its own thread,
/// which is the only supported usage.
/// </para>
/// </summary>
public sealed class RapidOcrService : IOcrService
{
    private readonly RapidOcrNet.RapidOcr _engine = new();
    private readonly RapidOcrOptions _lineOptions;
    private readonly RapidOcrOptions _fullOptions;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>The resolved model files this instance loaded.</summary>
    public RapidOcrModelSet Models { get; }

    /// <summary>
    /// Loads the OCR models. Throws <see cref="FileNotFoundException"/> when they cannot be
    /// found — the analysis worker catches that and continues without OCR, so a consumer
    /// that wires this optimistically still runs on a machine with no models installed.
    /// </summary>
    /// <param name="models">
    /// Model set to load; defaults to the bundled PP-OCRv5 Latin models. Paths are resolved
    /// through <see cref="OcrModelLocator"/>, so the relative paths on
    /// <see cref="RapidOcrModelSet"/>'s presets work regardless of working directory.
    /// </param>
    /// <param name="options">
    /// Engine tuning. Defaults to <see cref="RapidOcrOptions.Default"/> for PP-OCRv5 and
    /// <see cref="RapidOcrOptions.PPOCRv6"/> for a v6 set — mixing those up starves the v6
    /// detector of resolution. Word-box flags are set per call and need not be supplied.
    /// </param>
    /// <param name="configureSession">
    /// Runs after this package's conservative CPU defaults (see <see cref="OcrSessionOptions"/>),
    /// so it can override any of them.
    /// </param>
    public RapidOcrService(
        RapidOcrModelSet? models = null,
        RapidOcrOptions? options = null,
        Action<SessionOptions>? configureSession = null,
        ILogger? logger = null)
    {
        _logger = logger ?? RailReaderLogging.Logger;

        var requested = models ?? RapidOcrModelSet.PPOCRv5Latin;
        Models = OcrModelLocator.Locate(requested)
            ?? throw new FileNotFoundException(
                $"OCR models not found. Looked for '{requested.DetModelPath}' and its siblings beside the " +
                "application, in the user data directory, and under the working directory.");

        var baseOptions = options ?? DefaultOptionsFor(requested);
        // Detection-only: recognition-stage settings are ignored on that path anyway, but
        // the word-box flags are what make the difference in cost on the full path.
        _lineOptions = baseOptions with { ReturnWordBox = false, ReturnSingleCharBox = false };
        // Per-character boxes are the whole point of the full path: they are what let a
        // scanned page reuse char-clustering line detection, table cells, and the
        // reading-order text tie-break.
        _fullOptions = baseOptions with { ReturnWordBox = true, ReturnSingleCharBox = true };

        using var sessionOptions = OcrSessionOptions.Create(configureSession);
        // Loads detector, classifier and recogniser. RapidOcrNet exposes no detector-only
        // initialisation on this type, so Lines mode pays the classifier/recogniser load
        // (~9 MB) even though it never runs them; only the per-page inference is saved.
        _engine.InitModels(Models, sessionOptions);
        _logger.Debug($"[OCR] RapidOcrNet ready ({Path.GetFileName(Models.DetModelPath)})");
    }

    /// <summary>
    /// v6 detectors are exported for a different preprocessing contract than v5 (short-side
    /// adaptive resize vs. long-side cap plus white border), so the option preset has to
    /// follow the model set.
    /// </summary>
    private static RapidOcrOptions DefaultOptionsFor(RapidOcrModelSet models)
        => models.DetModelPath.Contains("v6", StringComparison.OrdinalIgnoreCase)
            ? RapidOcrOptions.PPOCRv6
            : RapidOcrOptions.Default;

    /// <inheritdoc/>
    public OcrPage Recognize(byte[] rgbBytes, int width, int height, OcrMode mode, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mode == OcrMode.Off || width <= 0 || height <= 0) return OcrPage.Empty;
        if (rgbBytes is null || rgbBytes.Length < (long)width * height * 3) return OcrPage.Empty;

        ct.ThrowIfCancellationRequested();
        using var bitmap = ToBitmap(rgbBytes, width, height);

        // The engine has no cancellation hook of its own, so cancellation is observed at the
        // stage boundary: a request abandoned mid-page still finishes its current stage.
        if (mode == OcrMode.Lines)
        {
            var boxes = _engine.DetectBoxes(bitmap, _lineOptions);
            ct.ThrowIfCancellationRequested();
            var lines = new List<OcrLine>(boxes.Count);
            foreach (var box in boxes)
                lines.Add(new OcrLine(Bounds(box.BoxPoints), Confidence: box.Score));
            return new OcrPage(lines);
        }

        var result = _engine.Detect(bitmap, _fullOptions);
        ct.ThrowIfCancellationRequested();

        var recognised = new List<OcrLine>(result.TextBlocks.Length);
        foreach (var block in result.TextBlocks)
            recognised.Add(ToLine(block));
        return new OcrPage(recognised);
    }

    private static OcrLine ToLine(TextBlock block)
    {
        var bounds = Bounds(block.BoxPoints);
        string text = block.Text;
        if (string.IsNullOrEmpty(text)) return new OcrLine(bounds, Confidence: block.BoxScore);

        return new OcrLine(bounds, text, CharBoxesFor(block, text), block.BoxScore);
    }

    /// <summary>
    /// Maps the engine's per-word/per-char polygons onto offsets in the line's own text.
    ///
    /// <para>
    /// The polygons are matched into <paramref name="text"/> by sequential search rather
    /// than by position, because the recogniser's word list omits the spaces between words:
    /// rebuilding the line by concatenating them would silently drop every space, and using
    /// polygon order as a character index would then misplace every box after the first
    /// space. Searching forward from a cursor keeps the authoritative text intact and yields
    /// exact indices; a word that cannot be located is skipped rather than guessed at.
    /// </para>
    /// </summary>
    private static List<CharBox>? CharBoxesFor(TextBlock block, string text)
    {
        if (block.WordResults is not { Length: > 0 } words) return null;

        var boxes = new List<CharBox>(words.Length);
        int cursor = 0;
        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word.Text)) continue;

            int at = text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
            if (at < 0) continue;
            cursor = at + word.Text.Length;

            var b = Bounds(word.BoxPoints);
            if (word.Text.Length == 1)
            {
                boxes.Add(new CharBox(at, b.X, b.Y, b.X + b.W, b.Y + b.H));
                continue;
            }

            // A multi-character polygon (CJK runs, or a recogniser that grouped a word) is
            // divided evenly: the engine reports no intra-polygon glyph positions, and equal
            // slices keep every character's box inside the word and in the right order,
            // which is all the downstream clustering needs.
            float slice = b.W / word.Text.Length;
            for (int i = 0; i < word.Text.Length; i++)
            {
                float x = b.X + slice * i;
                boxes.Add(new CharBox(at + i, x, b.Y, x + slice, b.Y + b.H));
            }
        }

        return boxes.Count > 0 ? boxes : null;
    }

    /// <summary>Axis-aligned bounds of a detection quad, in the source pixmap's pixel space.</summary>
    private static BBox Bounds(SKPointI[] points)
    {
        if (points is not { Length: > 0 }) return new BBox(0, 0, 0, 0);

        int minX = points[0].X, maxX = minX, minY = points[0].Y, maxY = minY;
        for (int i = 1; i < points.Length; i++)
        {
            var p = points[i];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new BBox(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Converts the analysis pixmap (tightly packed 8-bit RGB — the same buffer the layout
    /// analyzers read) into the bitmap the engine expects, opaque throughout.
    ///
    /// <para>
    /// The colour type is not incidental: RapidOcrNet's normalisation step accepts only
    /// <see cref="SKColorType.Bgra8888"/> or <c>Gray8</c> and throws on anything else, so the
    /// channels are swapped here rather than handed over in source order.
    /// </para>
    /// </summary>
    private static SKBitmap ToBitmap(byte[] rgbBytes, int width, int height)
    {
        var bgra = new byte[(long)width * height * 4];
        for (int i = 0, src = 0, dst = 0; i < width * height; i++, src += 3, dst += 4)
        {
            bgra[dst] = rgbBytes[src + 2];      // B
            bgra[dst + 1] = rgbBytes[src + 1];  // G
            bgra[dst + 2] = rgbBytes[src];      // R
            bgra[dst + 3] = 255;                // A — the page is opaque
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        try
        {
            Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        return bitmap;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Dispose();
    }
}
