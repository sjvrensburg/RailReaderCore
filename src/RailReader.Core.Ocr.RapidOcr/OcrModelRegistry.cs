using RapidOcrNet;

namespace RailReader.Core.Ocr.RapidOcr;

/// <summary>
/// Additional OCR recognition model sets RailReader knows how to download, beyond the
/// PP-OCRv5-Latin set the RapidOcrNet NuGet package bundles and <see cref="OcrModelLocator.LocateDefault"/>
/// resolves with no download at all.
///
/// <para>
/// The bundled default only recognizes Latin-script text (railreader2#209): its detector finds
/// text regions in any script, but the Latin-only recognizer reads non-Latin regions as
/// garbage or empty. The PP-OCRv6 sets here are RapidOCR's multilingual recognizers (Latin +
/// CJK and more in one model, per <see href="https://github.com/BobLd/RapidOcrNet">RapidOcrNet</see>'s
/// own documentation) in three size/accuracy tiers. A caller picks one, resolves its files with
/// <see cref="OcrModelLocator.Locate"/> after downloading them (e.g. via
/// <c>scripts/download-ocr-model.sh</c>), and passes <see cref="OcrModelDescriptor.ModelSet"/>
/// into <see cref="RapidOcrService"/>'s constructor — the same seam <c>PPOCRv5Latin</c> already
/// goes through, just with a set that resolves to files on disk instead of ones the package
/// copied there for you.
/// </para>
/// <para>
/// URLs, filenames and SHA-256 hashes are sourced from
/// <see href="https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml">
/// RapidOCR's own model manifest</see> (the upstream project both <c>RapidOcrNet</c> and this
/// package build on) — verified 2026-08-11 by downloading each file and independently
/// re-hashing it, not by trusting the manifest text alone.
/// </para>
/// </summary>
public static class OcrModelRegistry
{
    private const string Base = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2";

    /// <summary>
    /// Smallest/fastest multilingual set (~6 MB). Recommended starting point for an opt-in
    /// language pack: cheapest to download and load, at some accuracy cost versus Small/Medium.
    /// </summary>
    public static OcrModelDescriptor PPOCRv6Tiny { get; } = new(
        Id: "ppocrv6-tiny",
        DisplayName: "PP-OCRv6 Tiny — multilingual (smallest)",
        LanguageCoverage: "Latin + CJK and more",
        ModelSet: RapidOcrModelSet.PPOCRv6Tiny,
        Det: new(
            RapidOcrModelSet.PPOCRv6Tiny.DetModelPath,
            $"{Base}/onnx/PP-OCRv6/det/PP-OCRv6_det_tiny.onnx",
            "f42c0fbd294d95eac1a550e131b277dac97462c8025fa4b6c3cec1b7894bd3d5"),
        Rec: new(
            RapidOcrModelSet.PPOCRv6Tiny.RecModelPath,
            $"{Base}/onnx/PP-OCRv6/rec/PP-OCRv6_rec_tiny.onnx",
            "e16e242de5937ad92609223f19bc2aff3727ee40b095f996907c24749bad251b"),
        Dict: new(
            RapidOcrModelSet.PPOCRv6Tiny.KeysPath,
            $"{Base}/paddle/PP-OCRv6/rec/PP-OCRv6_rec_tiny/ppocrv6_tiny_dict.txt",
            "c5cbe34ef40c29c4df07ed012bf96569cb69a2d2a01a07027e9f13cb832bd9cd"),
        ApproxSizeMb: 6);

    /// <summary>Mid-size multilingual set (~31 MB); better accuracy than Tiny.</summary>
    public static OcrModelDescriptor PPOCRv6Small { get; } = new(
        Id: "ppocrv6-small",
        DisplayName: "PP-OCRv6 Small — multilingual",
        LanguageCoverage: "Latin + CJK and more",
        ModelSet: RapidOcrModelSet.PPOCRv6Small,
        Det: new(
            RapidOcrModelSet.PPOCRv6Small.DetModelPath,
            $"{Base}/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx",
            "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f"),
        Rec: new(
            RapidOcrModelSet.PPOCRv6Small.RecModelPath,
            $"{Base}/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx",
            "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884"),
        Dict: new(
            RapidOcrModelSet.PPOCRv6Small.KeysPath,
            $"{Base}/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt",
            "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d"),
        ApproxSizeMb: 31);

    /// <summary>Largest/most accurate multilingual set (~138 MB).</summary>
    public static OcrModelDescriptor PPOCRv6Medium { get; } = new(
        Id: "ppocrv6-medium",
        DisplayName: "PP-OCRv6 Medium — multilingual (most accurate)",
        LanguageCoverage: "Latin + CJK and more",
        ModelSet: RapidOcrModelSet.PPOCRv6Medium,
        Det: new(
            RapidOcrModelSet.PPOCRv6Medium.DetModelPath,
            $"{Base}/onnx/PP-OCRv6/det/PP-OCRv6_det_medium.onnx",
            "92078b7355007ccfffcd4c8cd441a3afd4538904d06881b29a155e1e679907c2"),
        Rec: new(
            RapidOcrModelSet.PPOCRv6Medium.RecModelPath,
            $"{Base}/onnx/PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx",
            "eef444829dbbe18d7fea59a3f6eb75647518d2b3a9568d27c92e42940204894b"),
        Dict: new(
            RapidOcrModelSet.PPOCRv6Medium.KeysPath,
            $"{Base}/paddle/PP-OCRv6/rec/PP-OCRv6_rec_medium/ppocrv6_dict.txt",
            // Medium shares its dict with Small (both key off ppocrv6_dict.txt); same file,
            // same hash.
            "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d"),
        ApproxSizeMb: 138);

    /// <summary>Recommended starting point for a caller adding multilingual OCR (smallest/fastest).</summary>
    public static OcrModelDescriptor Default => PPOCRv6Tiny;

    /// <summary>All known downloadable sets, default first. Does not include the bundled
    /// PP-OCRv5-Latin set, which needs no download — see <see cref="OcrModelLocator.LocateDefault"/>.</summary>
    public static IReadOnlyList<OcrModelDescriptor> All { get; } = [PPOCRv6Tiny, PPOCRv6Small, PPOCRv6Medium];

    /// <summary>Looks up a descriptor by its <see cref="OcrModelDescriptor.Id"/>; null if unknown.</summary>
    public static OcrModelDescriptor? ById(string id)
    {
        foreach (var d in All)
            if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }
}
