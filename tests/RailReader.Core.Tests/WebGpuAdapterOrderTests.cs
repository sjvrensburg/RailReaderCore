using RailReader.Core.Analysis.WebGpu;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #182: the WebGPU plugin picks its physical GPU by an index into Dawn's adapter
/// enumeration, which ranks discrete before integrated, while <see cref="WebGpuAccelerator.AvailableDevices"/>
/// follows ORT's (OS) order. <see cref="WebGpuAccelerator.DawnAdapterOrder"/> translates one into the other.
/// Placement itself needs real hardware and was measured by hand (see the type doc).
/// </summary>
public class WebGpuAdapterOrderTests
{
    [Fact]
    public void HybridLaptop_IntegratedListedFirst_MapsDiscreteToDawnZero()
    {
        // The dev box: ORT lists Intel Iris Xe, then NVIDIA RTX A2000 (Discrete=1).
        // Measured: adapterIndex 0 ran on NVIDIA, 1 on Intel.
        Assert.Equal([1, 0], WebGpuAccelerator.DawnAdapterOrder([false, true]));
    }

    [Fact]
    public void DiscreteListedFirst_KeepsOrder()
    {
        Assert.Equal([0, 1], WebGpuAccelerator.DawnAdapterOrder([true, false]));
    }

    [Fact]
    public void SameKindKeepsOrtOrderWithinGroup()
    {
        Assert.Equal([2, 0, 3, 1], WebGpuAccelerator.DawnAdapterOrder([false, true, false, true]));
    }

    [Fact]
    public void SingleAndEmpty()
    {
        Assert.Equal([0], WebGpuAccelerator.DawnAdapterOrder([false]));
        Assert.Equal([0], WebGpuAccelerator.DawnAdapterOrder([true]));
        Assert.Empty(WebGpuAccelerator.DawnAdapterOrder([]));
    }
}
