using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Accelerator-aware routing: <see cref="LayoutModelRegistry.Resolve"/> and
/// <see cref="LayoutModelRegistry.DefaultFor"/> pick the right descriptor for a
/// (architecture, accelerator) pair without any filesystem/ONNX involvement.
/// </summary>
public class LayoutModelRegistryTests
{
    [Theory]
    [InlineData(LayoutModelArchitecture.Heron, AcceleratorPreference.Cpu, "heron-int8")]
    [InlineData(LayoutModelArchitecture.Heron, AcceleratorPreference.Gpu, "heron")]
    [InlineData(LayoutModelArchitecture.PPDocLayoutV3, AcceleratorPreference.Cpu, "ppdoclayoutv3")]
    [InlineData(LayoutModelArchitecture.PPDocLayoutV3, AcceleratorPreference.Gpu, "ppdoclayoutv3")]
    public void Resolve_PicksTheExpectedDescriptor(
        LayoutModelArchitecture architecture, AcceleratorPreference accelerator, string expectedId)
    {
        var descriptor = LayoutModelRegistry.Resolve(architecture, accelerator);
        Assert.Equal(expectedId, descriptor.Id);
        Assert.Equal(architecture, descriptor.Architecture);
    }

    [Fact]
    public void Resolve_FallsBackToTheCpuDescriptorWhenNoGpuExportExists()
    {
        // PP-DocLayout-S has no FP16 export yet -- a GPU request must still return a
        // usable (CPU) descriptor rather than throwing or returning something null.
        var descriptor = LayoutModelRegistry.Resolve(LayoutModelArchitecture.PPDocLayoutS, AcceleratorPreference.Gpu);
        Assert.Equal(LayoutModelRegistry.PPDocLayoutS.Id, descriptor.Id);
    }

    [Fact]
    public void DefaultFor_RoutesThroughHeronForBothAccelerators()
    {
        // Switching accelerators shouldn't also switch the model's class taxonomy
        // underneath the caller -- both preferences route through Heron.
        Assert.Equal(LayoutModelArchitecture.Heron, LayoutModelRegistry.DefaultFor(AcceleratorPreference.Cpu).Architecture);
        Assert.Equal(LayoutModelArchitecture.Heron, LayoutModelRegistry.DefaultFor(AcceleratorPreference.Gpu).Architecture);
        Assert.Equal(LayoutModelRegistry.HeronInt8.Id, LayoutModelRegistry.DefaultFor(AcceleratorPreference.Cpu).Id);
        Assert.Equal(LayoutModelRegistry.Heron.Id, LayoutModelRegistry.DefaultFor(AcceleratorPreference.Gpu).Id);
    }

    [Fact]
    public void Resolve_EveryArchitectureIsHandled()
    {
        // Every enum member must resolve for both accelerators without throwing --
        // regression guard for adding a new architecture without updating Resolve.
        foreach (LayoutModelArchitecture architecture in Enum.GetValues<LayoutModelArchitecture>())
        {
            LayoutModelRegistry.Resolve(architecture, AcceleratorPreference.Cpu);
            LayoutModelRegistry.Resolve(architecture, AcceleratorPreference.Gpu);
        }
    }
}
