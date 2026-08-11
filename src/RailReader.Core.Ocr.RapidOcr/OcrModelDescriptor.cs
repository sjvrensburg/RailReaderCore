using RapidOcrNet;

namespace RailReader.Core.Ocr.RapidOcr;

/// <summary>
/// One file a download helper needs to fetch for an <see cref="OcrModelDescriptor"/>: the
/// canonical on-disk path (relative, matching the corresponding <see cref="RapidOcrModelSet"/>
/// path so <see cref="OcrModelLocator"/> finds it once placed), its download URL, and its
/// SHA-256 for post-download integrity verification.
/// </summary>
/// <param name="RelativePath">Matches the model set's own path, e.g. <c>models/v6/PP-OCRv6_det_tiny.onnx</c>.</param>
/// <param name="DownloadUrl">Direct-download URL.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file at <paramref name="DownloadUrl"/>.</param>
public sealed record OcrModelFile(string RelativePath, string DownloadUrl, string Sha256);

/// <summary>
/// Self-describing entry for an OCR recognition model set RailReader knows how to download:
/// which <see cref="RapidOcrModelSet"/> preset it resolves to, what script coverage it offers,
/// and the detector/recognizer/dictionary files a download helper needs to fetch.
///
/// <para>
/// Every set's text-line orientation classifier is the one bundled by the RapidOcrNet NuGet
/// package itself (<c>models/v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx</c> — see
/// <see cref="RapidOcrModelSet.PPOCRv6Tiny"/> and its siblings, which all reuse that path), so
/// it is never listed here as something to download separately.
/// </para>
/// <para>
/// Pure data (no ONNX session, no filesystem I/O), so a caller can list/pick a set before
/// paying for a download. Resolve a descriptor to files on disk with
/// <see cref="OcrModelLocator"/>, and to a running <see cref="RapidOcrService"/> by passing
/// <see cref="ModelSet"/> to its constructor.
/// </para>
/// </summary>
/// <param name="Id">Stable lookup key, e.g. <c>"ppocrv6-tiny"</c>.</param>
/// <param name="DisplayName">Human-facing name for a language/model picker UI.</param>
/// <param name="LanguageCoverage">Short human-facing description of script/language coverage.</param>
/// <param name="ModelSet">The <see cref="RapidOcrModelSet"/> preset this descriptor downloads for.</param>
/// <param name="Det">Detector model file.</param>
/// <param name="Rec">Recognizer model file.</param>
/// <param name="Dict">Recognizer dictionary (character set) file.</param>
/// <param name="ApproxSizeMb">Approximate combined download size in MB, for UI/progress.</param>
public sealed record OcrModelDescriptor(
    string Id,
    string DisplayName,
    string LanguageCoverage,
    RapidOcrModelSet ModelSet,
    OcrModelFile Det,
    OcrModelFile Rec,
    OcrModelFile Dict,
    int ApproxSizeMb);
