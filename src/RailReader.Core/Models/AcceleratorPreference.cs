namespace RailReader.Core.Models;

/// <summary>
/// A caller's preferred inference backend, used to route model selection
/// (<see cref="Services.LayoutModelRegistry.Resolve"/>) to the precision/export
/// variant that backend wants — CPU wants INT8/FP32, a GPU execution provider
/// (e.g. the native WebGPU EP in <c>RailReader.Core.Analysis.WebGpu</c>) wants
/// FP16/FP32. Purely a routing hint: it selects <em>which model file</em> to
/// load, not <em>how</em> to run it — actually enabling a GPU execution
/// provider is a separate step the caller takes via that package's
/// <c>WebGpuAccelerator</c>, since <c>Core</c> has no ONNX Runtime dependency.
/// </summary>
public enum AcceleratorPreference
{
    /// <summary>CPU inference — the default, always available.</summary>
    Cpu,

    /// <summary>GPU inference via a native execution provider, if one is available.</summary>
    Gpu,
}
