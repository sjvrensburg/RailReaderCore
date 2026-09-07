using RailReader.Core.Models;
using Xunit;

namespace RailReader.Core.Tests;

public class PageLayoutTests
{
    [Fact]
    public void PrefixSums_StackPagesWithGap()
    {
        var layout = new PageLayout(new (double, double)[] { (100, 200), (100, 300), (100, 150) }, gap: 10);

        Assert.Equal(0.0, layout.Top(0));
        Assert.Equal(210.0, layout.Top(1));
        Assert.Equal(520.0, layout.Top(2));
        Assert.Equal(670.0, layout.TotalHeight);
    }

    [Fact]
    public void Left_CentresNarrowerPagesInWidestColumn()
    {
        var layout = new PageLayout(new (double, double)[] { (100, 200), (200, 200), (50, 200) }, gap: 10);

        Assert.Equal(200.0, layout.MaxWidth);
        Assert.Equal(50.0, layout.Left(0));
        Assert.Equal(0.0, layout.Left(1));
        Assert.Equal(75.0, layout.Left(2));
    }

    [Fact]
    public void PageAtY_InsidePage_ReturnsThatPage()
    {
        var layout = new PageLayout(new (double, double)[] { (100, 200), (100, 200), (100, 200) }, gap: 10);
        Assert.Equal(0, layout.PageAtY(50));
        Assert.Equal(1, layout.PageAtY(210 + 50));
        Assert.Equal(2, layout.PageAtY(420 + 50));
    }

    [Fact]
    public void PageAtY_InsideGap_ReturnsNearerPage()
    {
        // Page 0: [0,200]; gap [200,210]; page 1: [210,410].
        var layout = new PageLayout(new (double, double)[] { (100, 200), (100, 200) }, gap: 10);
        Assert.Equal(0, layout.PageAtY(203)); // closer to page 0's bottom
        Assert.Equal(1, layout.PageAtY(208)); // closer to page 1's top
    }

    [Fact]
    public void PageAtY_BeyondEnds_Clamps()
    {
        var layout = new PageLayout(new (double, double)[] { (100, 200), (100, 200) }, gap: 10);
        Assert.Equal(0, layout.PageAtY(-500));
        Assert.Equal(1, layout.PageAtY(10000));
    }

    [Fact]
    public void PagesIntersecting_ReturnsOverlappingRange()
    {
        var layout = new PageLayout(new (double, double)[] { (100, 200), (100, 200), (100, 200), (100, 200) }, gap: 10);
        // Window spanning the tail of page 0 through the start of page 2.
        var (first, last) = layout.PagesIntersecting(190, 425);
        Assert.Equal(0, first);
        Assert.Equal(2, last);
    }

    [Fact]
    public void SinglePage_DegeneratesToPageExtent()
    {
        var layout = new PageLayout(new (double, double)[] { (100, 200) }, gap: 12);
        Assert.Equal(100.0, layout.MaxWidth);
        Assert.Equal(200.0, layout.TotalHeight);
        Assert.Equal(0.0, layout.Top(0));
        Assert.Equal(0.0, layout.Left(0));
    }

    [Fact]
    public void RotatedSizes_SwapWidthHeight()
    {
        // Simulates GetPageSizes(1) (odd view rotation) swapping W/H before layout construction.
        var portraitSizes = new (double, double)[] { (100, 200), (150, 300) };
        var rotated = portraitSizes.Select(s => (s.Item2, s.Item1)).ToArray();
        var layout = new PageLayout(rotated, gap: 0);

        Assert.Equal(300.0, layout.MaxWidth);
        Assert.Equal(200.0, layout.Width(0));
        Assert.Equal(100.0, layout.Height(0));
    }
}
