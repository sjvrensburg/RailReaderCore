using RailReader.Core.Analysis;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class DoclingHeronRolesTests
{
    [Fact]
    public void Capabilities_AdvertiseInputSizeAndNoReadingOrder()
    {
        var caps = DoclingHeronRoles.Capabilities;
        Assert.Equal(640, caps.InputSize);
        Assert.False(caps.ProvidesReadingOrder);
    }

    [Fact]
    public void Capabilities_Has17Classes_IndexedContiguously()
    {
        var caps = DoclingHeronRoles.Capabilities;
        Assert.Equal(17, caps.Classes.Count);
        for (int i = 0; i < caps.Classes.Count; i++)
            Assert.Equal(i, caps.Classes[i].Id);
    }

    [Fact]
    public void Capabilities_ClassNamesAreUnique()
    {
        var names = DoclingHeronRoles.Capabilities.Classes.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Capabilities_EveryClassHasARole()
    {
        // Every entry must map to *some* BlockRole — Unknown is fine but only
        // intentionally; flag here so any future class added without a
        // deliberate mapping is caught.
        foreach (var c in DoclingHeronRoles.Capabilities.Classes)
            Assert.True(Enum.IsDefined(typeof(BlockRole), c.Role),
                $"Class '{c.Name}' maps to undefined BlockRole");
    }

    [Theory]
    [InlineData("text",            BlockRole.Text)]
    [InlineData("title",           BlockRole.Title)]
    [InlineData("section_header",  BlockRole.Heading)]
    [InlineData("caption",         BlockRole.Caption)]
    [InlineData("footnote",        BlockRole.Footnote)]
    [InlineData("page_header",     BlockRole.Header)]
    [InlineData("page_footer",     BlockRole.Footer)]
    [InlineData("picture",         BlockRole.Figure)]
    [InlineData("table",           BlockRole.Table)]
    [InlineData("formula",         BlockRole.DisplayMath)]
    [InlineData("code",            BlockRole.Algorithm)]
    [InlineData("list_item",       BlockRole.Text)]
    [InlineData("document_index",  BlockRole.Text)]
    public void RoleForName_MapsKnownClasses(string name, BlockRole expected)
    {
        Assert.Equal(expected, DoclingHeronRoles.RoleForName(name));
    }

    [Fact]
    public void RoleForName_UnknownName_ReturnsNull()
    {
        Assert.Null(DoclingHeronRoles.RoleForName("not_a_real_class"));
    }
}
