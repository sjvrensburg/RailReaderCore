using RailReader.Core.Analysis;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class PPDocLayoutSRolesTests
{
    [Fact]
    public void Capabilities_AdvertiseInputSizeAndNoReadingOrder()
    {
        var caps = PPDocLayoutSRoles.Capabilities;
        // InputSize is the rasterisation hint (1920), not the model's spatial input (480).
        Assert.Equal(1920, caps.InputSize);
        Assert.False(caps.ProvidesReadingOrder);
    }

    [Fact]
    public void ModelInputSize_Is480()
    {
        Assert.Equal(480, PPDocLayoutSRoles.ModelInputSize);
    }

    [Fact]
    public void Capabilities_Has23Classes_IndexedContiguously()
    {
        var caps = PPDocLayoutSRoles.Capabilities;
        Assert.Equal(23, caps.Classes.Count);
        for (int i = 0; i < caps.Classes.Count; i++)
            Assert.Equal(i, caps.Classes[i].Id);
    }

    [Fact]
    public void Capabilities_ClassNamesAreUnique()
    {
        var names = PPDocLayoutSRoles.Capabilities.Classes.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Capabilities_EveryClassHasARole()
    {
        foreach (var c in PPDocLayoutSRoles.Capabilities.Classes)
            Assert.True(Enum.IsDefined(typeof(BlockRole), c.Role),
                $"Class '{c.Name}' maps to undefined BlockRole");
    }

    [Theory]
    [InlineData("text",            BlockRole.Text)]
    [InlineData("doc_title",       BlockRole.Title)]
    [InlineData("paragraph_title", BlockRole.Heading)]
    [InlineData("figure_title",    BlockRole.Caption)]
    [InlineData("table_title",     BlockRole.Caption)]
    [InlineData("chart_title",     BlockRole.Caption)]
    [InlineData("formula",         BlockRole.DisplayMath)]
    [InlineData("algorithm",       BlockRole.Algorithm)]
    [InlineData("table",           BlockRole.Table)]
    [InlineData("image",           BlockRole.Figure)]
    [InlineData("chart",           BlockRole.Chart)]
    [InlineData("footnote",        BlockRole.Footnote)]
    [InlineData("reference",       BlockRole.Reference)]
    [InlineData("aside_text",      BlockRole.Aside)]
    [InlineData("number",          BlockRole.PageNumber)]
    public void RoleForName_MapsKnownClasses(string name, BlockRole expected)
    {
        Assert.Equal(expected, PPDocLayoutSRoles.RoleForName(name));
    }

    [Fact]
    public void RoleForName_UnknownName_ReturnsNull()
    {
        Assert.Null(PPDocLayoutSRoles.RoleForName("not_a_real_class"));
    }

    [Fact]
    public void NoInlineFormulaClass_OnlyDisplayFormula()
    {
        // PP-S has no "inline_formula" — only "formula" → DisplayMath. Important
        // for any code that previously branched on PP-V3's separate inline class.
        Assert.Null(PPDocLayoutSRoles.RoleForName("inline_formula"));
        Assert.Equal(BlockRole.DisplayMath, PPDocLayoutSRoles.RoleForName("formula"));
    }
}
