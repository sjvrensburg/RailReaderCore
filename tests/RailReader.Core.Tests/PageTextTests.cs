using RailReader.Core.Models;
using Xunit;

namespace RailReader.Core.Tests;

public class PageTextTests
{
    // A block covering the whole synthetic coordinate space used below.
    private static LayoutBlock FullBlock() => new()
    {
        Role = BlockRole.Text,
        BBox = new BBox(0, 0, 10_000, 10_000),
    };

    // Lays characters left-to-right on one row, one CharBox per char (index order).
    private static PageText MakeText(string text)
    {
        var chars = new List<CharBox>(text.Length);
        for (int i = 0; i < text.Length; i++)
            chars.Add(new CharBox(i, i * 10, 100, i * 10 + 8, 116));
        return new PageText(text, chars);
    }

    [Fact]
    public void ExtractBlockText_FewerThanMax_NotTruncated()
    {
        var pt = MakeText("Hello");
        var result = pt.ExtractBlockText(FullBlock(), maxChars: 100, out bool truncated);

        Assert.Equal("Hello", result);
        Assert.False(truncated);
    }

    [Fact]
    public void ExtractBlockText_ExactlyMax_NotTruncated()
    {
        var pt = MakeText(new string('A', 50));
        var result = pt.ExtractBlockText(FullBlock(), maxChars: 50, out bool truncated);

        Assert.Equal(50, result.Length);
        Assert.False(truncated);
    }

    [Fact]
    public void ExtractBlockText_MoreThanMax_TruncatedFlagSet()
    {
        var pt = MakeText(new string('A', 250));
        var result = pt.ExtractBlockText(FullBlock(), maxChars: 200, out bool truncated);

        Assert.Equal(200, result.Length);
        Assert.True(truncated);
    }

    [Fact]
    public void ExtractBlockText_TruncationReportedIndependentlyOfTrim()
    {
        // Leading whitespace would, after trimming, shrink the returned string below
        // maxChars — but the block genuinely contains more text, so truncated must
        // remain true (the previous length-based heuristic got this wrong).
        var pt = MakeText(new string(' ', 5) + new string('A', 300));
        var result = pt.ExtractBlockText(FullBlock(), maxChars: 200, out bool truncated);

        Assert.True(truncated);
        Assert.True(result.Length <= 200);
        Assert.DoesNotContain(" ", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExtractBlockText_NonPositiveMaxChars_ReturnsEmpty(int maxChars)
    {
        var pt = MakeText("Hello");
        var result = pt.ExtractBlockText(FullBlock(), maxChars, out bool truncated);

        Assert.Equal("", result);
        Assert.False(truncated);
    }

    [Fact]
    public void ExtractBlockText_NoMatchingChars_ReturnsEmpty()
    {
        var pt = MakeText("Hello");
        var offBlock = new LayoutBlock { Role = BlockRole.Text, BBox = new BBox(100_000, 100_000, 10, 10) };
        var result = pt.ExtractBlockText(offBlock, maxChars: 50, out bool truncated);

        Assert.Equal("", result);
        Assert.False(truncated);
    }
}
