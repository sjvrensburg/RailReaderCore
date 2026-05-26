using RailReader.Core.Analysis.LightGbm;
using RailReader.Core.Models;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Smoke tests for the LightGBM analyzer scaffolding. The full
/// feature-engineering pipeline isn't ported yet, so these only cover
/// what currently compiles and runs: the DocLayNet → BlockRole
/// mapping, the analyzer constructor (model loading via
/// LightGBMNet.Tree), and the expected NotImplementedException from
/// <c>RunAnalysis</c>.
///
/// <para>
/// Model files come from <c>scripts/download-model.sh lightgbm</c>.
/// If they are not present in <c>./models/</c> the model-loading tests
/// pass trivially with no assertions — keeps CI green on a clean
/// checkout while still exercising the path locally when developers
/// have run the download script.
/// </para>
/// </summary>
public class LightGbmAnalyzerSmokeTests
{
    private static string ModelsDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models");

    private static string TokenTypeModelPath
        => Path.Combine(ModelsDir, "token_type_lightgbm.model");

    private static string ParagraphModelPath
        => Path.Combine(ModelsDir, "paragraph_extraction_lightgbm.model");

    private static bool ModelsPresent()
        => File.Exists(TokenTypeModelPath) && File.Exists(ParagraphModelPath);

    [Fact]
    public void DocLayNetRoles_has_eleven_classes_with_unique_ids()
    {
        var classes = DocLayNetRoles.Classes;
        Assert.Equal(11, classes.Count);
        Assert.Equal(classes.Count, classes.Select(c => c.Id).Distinct().Count());
        Assert.Equal(classes.Count, classes.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void DocLayNetRoles_RoleForName_maps_known_classes()
    {
        Assert.Equal(BlockRole.Heading,     DocLayNetRoles.RoleForName("section_header"));
        Assert.Equal(BlockRole.DisplayMath, DocLayNetRoles.RoleForName("formula"));
        Assert.Equal(BlockRole.Title,       DocLayNetRoles.RoleForName("title"));
        Assert.Null(DocLayNetRoles.RoleForName("not_a_real_class"));
    }

    [Fact]
    public void DocLayNetRoles_capabilities_advertise_no_rasterisation_and_no_reading_order()
    {
        Assert.Equal(0, DocLayNetRoles.Capabilities.InputSize);
        Assert.False(DocLayNetRoles.Capabilities.ProvidesReadingOrder);
    }

    [Fact]
    public void Analyzer_constructs_and_reports_capabilities_when_models_are_present()
    {
        if (!ModelsPresent()) return; // skip locally without models

        var analyzer = new LightGbmLayoutAnalyzer(TokenTypeModelPath, ParagraphModelPath);
        Assert.Same(DocLayNetRoles.Capabilities, analyzer.Capabilities);

        var (tokenTypeInputs, paragraphInputs) = analyzer.GetModelInputCounts();
        // Trained huridocs models (probed at NumInputs):
        //   token_type:           1968 features
        //     = context_size 4 × 2 pairs/side × 246 features/pair
        //   paragraph_extraction:  536 features
        //     = the paragraph-pair features + one-hot token type tail
        // These are load-bearing checks for the future feature-engineering
        // port — a shape mismatch will trip here immediately and prevent
        // shipping silently-wrong predictions.
        Assert.Equal(1968, tokenTypeInputs);
        Assert.Equal(536, paragraphInputs);
    }

    [Fact]
    public void RunAnalysis_throws_NotImplementedException_until_feature_engineering_lands()
    {
        if (!ModelsPresent()) return; // skip locally without models

        var analyzer = new LightGbmLayoutAnalyzer(TokenTypeModelPath, ParagraphModelPath);
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());

        var ex = Assert.Throws<NotImplementedException>(
            () => analyzer.RunAnalysis(bytes, 0));
        Assert.Contains("feature engineering", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
