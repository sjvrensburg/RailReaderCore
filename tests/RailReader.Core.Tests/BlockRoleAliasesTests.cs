using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class BlockRoleAliasesTests
{
    [Fact]
    public void Figure_IncludesChart()
        => Assert.Equal(new[] { BlockRole.Figure, BlockRole.Chart }, BlockRoleAliases.Resolve("figure"));

    [Fact]
    public void Equation_IncludesInlineMathAndAlgorithm()
        => Assert.Equal(
            new[] { BlockRole.DisplayMath, BlockRole.InlineMath, BlockRole.Algorithm },
            BlockRoleAliases.Resolve("equation"));

    [Theory]
    [InlineData("FIGURE")]
    [InlineData(" figure ")]
    public void IsCaseAndWhitespaceInsensitive(string token)
        => Assert.Equal(new[] { BlockRole.Figure, BlockRole.Chart }, BlockRoleAliases.Resolve(token));

    [Fact]
    public void RawEnumName_ParsesAsSingleRole()
        => Assert.Equal(new[] { BlockRole.Caption }, BlockRoleAliases.Resolve("Caption"));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void Unknown_YieldsEmpty(string? token)
        => Assert.Empty(BlockRoleAliases.Resolve(token));
}
