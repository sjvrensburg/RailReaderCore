using LightGBMNet.Tree;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core.Analysis.LightGbm;

/// <summary>
/// Pure-managed text-only layout analyzer modelled on huridocs's fast
/// pipeline: PDF text layer → line tokens → two LightGBM stages
/// (per-token classification, then per-pair paragraph grouping) → page
/// segments. Targets Lite/web/mobile consumers that cannot ship PDFium
/// or ONNX Runtime.
///
/// <para>
/// <b>Status: scaffolding only.</b> The two LightGBM models load
/// successfully via <see cref="LightGBMNet.Tree.OvaPredictor"/> /
/// <see cref="LightGBMNet.Tree.BinaryPredictor"/>, and
/// <see cref="LineTokenizer"/> produces line tokens from a
/// <c>PdfPig.Content.Page</c>. The two feature engineers
/// (<c>TokenFeatures</c> equivalent + paragraph-pair features) and the
/// sliding-window composition that produces the model's input vectors
/// are <b>not yet ported</b> — calling <see cref="RunAnalysis"/> throws
/// <see cref="NotImplementedException"/>. Tracking follow-up issue:
/// <a href="https://github.com/sjvrensburg/RailReaderCore/issues/16">#16</a>.
/// </para>
///
/// <para>
/// The decision to ship scaffolding first is deliberate: silently
/// shipping a faithful-looking port without a Python-comparison
/// validation harness risks subtle feature-order bugs that produce
/// shape-correct-but-semantically-wrong predictions. The follow-up
/// issue specifies the validation infrastructure (curated test PDFs +
/// expected outputs from huridocs's Python pipeline) needed before the
/// port can be shipped with confidence.
/// </para>
/// </summary>
public sealed class LightGbmLayoutAnalyzer : ITextLayoutAnalyzer
{
    private readonly IPredictorWithFeatureWeights<double[]> _tokenTypeModel;
    private readonly IPredictorWithFeatureWeights<double> _paragraphModel;

    public LayoutModelCapabilities Capabilities => DocLayNetRoles.Capabilities;

    /// <summary>
    /// Loads the two LightGBM text-dump models that huridocs publishes
    /// at <c>HURIDOCS/pdf-document-layout-analysis</c> on Hugging Face.
    /// Use the <c>scripts/download-model.sh lightgbm</c> entry to fetch
    /// them locally.
    /// </summary>
    /// <param name="tokenTypeModelPath">Path to
    /// <c>token_type_lightgbm.model</c> (11-class softmax multiclass).</param>
    /// <param name="paragraphModelPath">Path to
    /// <c>paragraph_extraction_lightgbm.model</c> (binary, "are these
    /// in the same paragraph?").</param>
    public LightGbmLayoutAnalyzer(string tokenTypeModelPath, string paragraphModelPath)
    {
        _tokenTypeModel = OvaPredictor.FromFile(tokenTypeModelPath);
        _paragraphModel = BinaryPredictor.FromFile(paragraphModelPath);
    }

    public PageAnalysis RunAnalysis(byte[] pdfBytes, int pageIndex, CancellationToken ct = default)
    {
        // Pipeline shape (when the FE port lands):
        //
        //   open PdfPig document
        //   page = doc.GetPage(pageIndex + 1)
        //   tokens = LineTokenizer.Tokenize(page)           // ✓ ready
        //   pad tokens on both sides by context_size
        //   for each real token, build a sliding-window feature vector  // ⨯
        //   token.Type = argmax(tokenTypeModel.GetOutput(features))
        //   for each consecutive pair, build paragraph-pair feature vector // ⨯
        //   split tokens into paragraphs when paragraphModel says ≠same
        //   collapse each paragraph into a LayoutBlock using the
        //       majority TokenType → BlockRole via DocLayNetRoles
        //
        // Steps marked ⨯ are the feature engineering port that the
        // follow-up issue tracks. See the class-level XML comment for
        // why this is staged.
        throw new NotImplementedException(
            "Feature engineering for the LightGBM layout pipeline is " +
            "not yet ported. See the LightGbmLayoutAnalyzer class summary " +
            "and the tracking issue. The model loaders and the line " +
            "tokeniser are functional and can be exercised independently.");
    }

    /// <summary>
    /// Internal diagnostic: returns the number of inputs each loaded
    /// model expects. Useful for verifying the feature engineering port
    /// produces vectors of the expected shape once it lands.
    /// </summary>
    internal (int TokenTypeInputs, int ParagraphInputs) GetModelInputCounts()
        => (_tokenTypeModel.NumInputs, _paragraphModel.NumInputs);
}
