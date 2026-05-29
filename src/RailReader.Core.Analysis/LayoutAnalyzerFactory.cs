using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core.Analysis;

/// <summary>
/// Maps a <see cref="LayoutModelArchitecture"/> (or a full
/// <see cref="LayoutModelDescriptor"/>) to the concrete
/// <see cref="ILayoutAnalyzer"/> that can run it, loading the ONNX from the
/// given path. Centralises the architecture→analyzer wiring so consumers stop
/// hard-coding which analyzer class pairs with which model file — which is the
/// bug a quantized variant exposes (a Heron file must go to
/// <see cref="HeronLayoutAnalyzer"/>, never the V3 <see cref="LayoutAnalyzer"/>).
/// </summary>
public static class LayoutAnalyzerFactory
{
    /// <summary>
    /// Constructs the analyzer for <paramref name="architecture"/> from the ONNX
    /// at <paramref name="modelPath"/>, using that architecture's default
    /// capability/class table.
    /// </summary>
    public static ILayoutAnalyzer Create(LayoutModelArchitecture architecture, string modelPath) =>
        architecture switch
        {
            LayoutModelArchitecture.Heron => new HeronLayoutAnalyzer(modelPath),
            LayoutModelArchitecture.PPDocLayoutS => new PPDocLayoutSLayoutAnalyzer(modelPath),
            LayoutModelArchitecture.PPDocLayoutV3 => new LayoutAnalyzer(modelPath),
            _ => throw new System.ArgumentOutOfRangeException(
                nameof(architecture), architecture, "Unknown layout-model architecture"),
        };

    /// <summary>Constructs the analyzer for <paramref name="descriptor"/>'s architecture.</summary>
    public static ILayoutAnalyzer Create(LayoutModelDescriptor descriptor, string modelPath) =>
        Create(descriptor.Architecture, modelPath);

    /// <summary>
    /// Returns the static <see cref="LayoutModelCapabilities"/> for an
    /// architecture without loading a model — useful for wiring an
    /// <c>AnalysisWorker</c> (which needs InputSize/ProvidesReadingOrder before
    /// the model finishes loading) ahead of the analyzer construction.
    /// </summary>
    public static LayoutModelCapabilities CapabilitiesFor(LayoutModelArchitecture architecture) =>
        architecture switch
        {
            LayoutModelArchitecture.Heron => DoclingHeronRoles.Capabilities,
            LayoutModelArchitecture.PPDocLayoutS => PPDocLayoutSRoles.Capabilities,
            LayoutModelArchitecture.PPDocLayoutV3 => PPDocLayoutV3Roles.Capabilities,
            _ => throw new System.ArgumentOutOfRangeException(
                nameof(architecture), architecture, "Unknown layout-model architecture"),
        };
}
