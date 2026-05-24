using RailReader.Core.Analysis;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class PPDocLayoutV3RolesTests
{
    [Fact]
    public void Capabilities_AdvertiseInputSizeAndReadingOrder()
    {
        var caps = PPDocLayoutV3Roles.Capabilities;
        Assert.Equal(800, caps.InputSize);
        Assert.True(caps.ProvidesReadingOrder);
    }

    [Fact]
    public void Capabilities_Has25Classes_IndexedContiguously()
    {
        var caps = PPDocLayoutV3Roles.Capabilities;
        Assert.Equal(25, caps.Classes.Count);
        for (int i = 0; i < caps.Classes.Count; i++)
            Assert.Equal(i, caps.Classes[i].Id);
    }

    [Theory]
    [InlineData("text",             BlockRole.Text)]
    [InlineData("display_formula",  BlockRole.DisplayMath)]
    [InlineData("inline_formula",   BlockRole.InlineMath)]
    [InlineData("algorithm",        BlockRole.Algorithm)]
    [InlineData("table",            BlockRole.Table)]
    [InlineData("image",            BlockRole.Figure)]
    [InlineData("chart",            BlockRole.Chart)]
    [InlineData("doc_title",        BlockRole.Title)]
    [InlineData("paragraph_title",  BlockRole.Heading)]
    [InlineData("figure_title",     BlockRole.Caption)]
    [InlineData("footnote",         BlockRole.Footnote)]
    public void RoleForName_MapsKnownClasses(string name, BlockRole expected)
    {
        Assert.Equal(expected, PPDocLayoutV3Roles.RoleForName(name));
    }

    [Fact]
    public void RoleForName_UnknownName_ReturnsNull()
    {
        Assert.Null(PPDocLayoutV3Roles.RoleForName("not_a_real_class"));
    }
}
