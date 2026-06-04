using RailReader.Core;
using RailReader.Core.Models;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Regression tests for <see cref="AnnotationInteractionHandler.BuildHighlightRects"/> and its
/// line merge (issue #37): characters on one visual line must collapse to a SINGLE rect
/// spanning the full line band even though ascenders/capitals sit several px above x-height
/// letters — otherwise authored Underline/StrikeOut/Squiggly draw a ragged, per-ascender
/// baseline. The merge now groups by vertical overlap, not by top-equality.
/// </summary>
public class HighlightRectMergeTests
{
    private static CharBox Box(int i, float left, float top, float right, float bottom)
        => new(i, left, top, right, bottom);

    [Fact]
    public void OneLine_WithAscenders_MergesToSingleRect()
    {
        // One visual line, bottom = 123. x-height letters top = 110; ascenders/capitals
        // top = 104 (6px higher — exceeds the old 4px top-equality threshold that fragmented
        // the line into one rect per ascender run).
        var chars = new List<CharBox>
        {
            Box(0, 72, 110, 80, 123),  // x-height (a)
            Box(1, 80, 104, 84, 123),  // ascender (l)
            Box(2, 84, 110, 92, 123),  // x-height (o)
            Box(3, 92, 104, 100, 123), // capital (T)
        };
        var pt = new PageText("aloT", chars);

        var rects = AnnotationInteractionHandler.BuildHighlightRects(pt, 0, chars.Count);

        var r = Assert.Single(rects);                // one rect for the whole line
        Assert.Equal(104f, r.Y, 0.5f);               // band top = min char top
        Assert.Equal(123f, r.Y + r.H, 0.5f);         // band bottom = max char bottom
        Assert.True(r.X <= 72f && r.X + r.W >= 100f, // spans the full line width
            $"rect should span the whole line (X={r.X}, W={r.W})");
    }

    [Fact]
    public void TwoLines_ProduceTwoRects()
    {
        var chars = new List<CharBox>
        {
            Box(0, 72, 104, 100, 123),  // line 1 (cap + x-height share bottom 123)
            Box(1, 100, 110, 130, 123), // line 1
            Box(2, 72, 128, 100, 147),  // line 2 — top 128 is below the line-1 band (≤123)
            Box(3, 100, 128, 130, 147), // line 2
        };
        var pt = new PageText("abcd", chars);

        var rects = AnnotationInteractionHandler.BuildHighlightRects(pt, 0, chars.Count);

        Assert.Equal(2, rects.Count);
        Assert.Equal(104f, rects[0].Y, 0.5f);
        Assert.Equal(128f, rects[1].Y, 0.5f);
    }

    [Fact]
    public void SkipsZeroBoxes_AndHonoursSubSelectionRange()
    {
        var chars = new List<CharBox>
        {
            Box(0, 0, 0, 0, 0),        // zero box (e.g. a space) — must not split the line
            Box(1, 72, 110, 80, 123),
            Box(2, 80, 104, 88, 123),
        };
        var pt = new PageText(" al", chars);

        var rects = AnnotationInteractionHandler.BuildHighlightRects(pt, 1, 2); // indices 1..2

        var r = Assert.Single(rects);
        Assert.Equal(104f, r.Y, 0.5f);
        Assert.Equal(123f, r.Y + r.H, 0.5f);
    }
}
