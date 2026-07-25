using RailReader.Core.Models;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for <see cref="PageText.DedupedCharBoxes"/>, which drops the repeated glyphs
/// producers emit to fake bold or a drop shadow. Those repeats are real text-layer entries
/// and would otherwise skew every statistic line detection derives from the glyph
/// population (median char height, the table cell-gap percentile anchor).
/// </summary>
public class PageTextDedupTests
{
    private static CharBox Box(int index, float left, float top, float w = 7f, float h = 10f)
        => new(index, left, top, left + w, top + h);

    [Fact]
    public void FakeBoldRepeats_AreDropped()
    {
        // "AB" stroked twice, the second pass offset by a third of a pixel — the classic
        // fake-bold trick. Both passes are in the text layer; only one glyph is on the page.
        var text = new PageText("ABAB", [
            Box(0, 100f, 50f), Box(1, 108f, 50f),
            Box(2, 100.3f, 50.2f), Box(3, 108.3f, 50.2f),
        ]);

        var deduped = text.DedupedCharBoxes;

        Assert.Equal(2, deduped.Count);
        // The first occurrence survives, so indices stay as low as possible.
        Assert.Equal([0, 1], deduped.Select(c => c.Index));
    }

    [Fact]
    public void RepeatedCharacterAtItsOwnPosition_IsKept()
    {
        // "AA" as genuinely adjacent glyphs: same character, a full glyph apart.
        var text = new PageText("AA", [Box(0, 100f, 50f), Box(1, 108f, 50f)]);

        Assert.Equal(2, text.DedupedCharBoxes.Count);
    }

    [Fact]
    public void AdjacentNarrowGlyphs_AreKept()
    {
        // The two 'l's of "all" in 11 pt Helvetica: the tight char box is ~1.1 pt wide and
        // ~7.9 pt tall, and the pair sits one 2.44 pt advance apart. A tolerance taken from the
        // taller extent is 2.63 pt and swallows the second — quietly deleting real characters
        // from the glyph set every line-split and cell-gap statistic is computed over.
        var text = new PageText("ll", [
            new CharBox(0, 100f, 50f, 101.1f, 57.9f),
            new CharBox(1, 102.44f, 50f, 103.54f, 57.9f),
        ]);

        Assert.Equal(2, text.DedupedCharBoxes.Count);
    }

    [Fact]
    public void FakeBoldRepeatOfANarrowGlyph_IsStillDropped()
    {
        // The other half of the same case: a narrow glyph stroked twice a fifth of a point
        // apart is still a duplicate. Tightening the tolerance must not blind the check.
        var text = new PageText("ll", [
            new CharBox(0, 100f, 50f, 101.1f, 57.9f),
            new CharBox(1, 100.2f, 50.1f, 101.3f, 58f),
        ]);

        Assert.Single(text.DedupedCharBoxes);
    }

    [Fact]
    public void DuplicatesStraddlingABandBoundary_AreStillFound()
    {
        // The search is bucketed by Y so a glyph is never compared against every same-valued
        // glyph on the page. A pair either side of a bucket boundary must still be compared.
        var text = new PageText("AA", [
            new CharBox(0, 100f, 51.9f, 107f, 61.9f),
            new CharBox(1, 100.1f, 52.1f, 107.1f, 62.1f),
        ]);

        Assert.Single(text.DedupedCharBoxes);
    }

    [Fact]
    public void DifferentCharactersAtTheSamePlace_AreKept()
    {
        // Overlapping but distinct glyphs (a composed accent, or an overprinted symbol) are
        // not duplicates — dedup keys on the character, not on geometry alone.
        var text = new PageText("Ao", [Box(0, 100f, 50f), Box(1, 100f, 50f)]);

        Assert.Equal(2, text.DedupedCharBoxes.Count);
    }

    [Fact]
    public void SameCharacterAtDifferentAngles_IsKept()
    {
        var text = new PageText("AA", [
            Box(0, 100f, 50f),
            Box(1, 100f, 50f) with { Angle = 90f },
        ]);

        Assert.Equal(2, text.DedupedCharBoxes.Count);
    }

    [Fact]
    public void ZeroAreaBoxes_AreAlwaysKept()
    {
        // Spaces and other non-marking glyphs have degenerate boxes: no geometry to
        // duplicate, and consumers already skip them.
        var text = new PageText("   ", [
            new CharBox(0, 100f, 50f, 100f, 50f),
            new CharBox(1, 100f, 50f, 100f, 50f),
            new CharBox(2, 100f, 50f, 100f, 50f),
        ]);

        Assert.Equal(3, text.DedupedCharBoxes.Count);
    }

    [Fact]
    public void NothingToDrop_ReturnsTheOriginalList()
    {
        // The overwhelmingly common case must not pay an allocation.
        var boxes = new List<CharBox> { Box(0, 100f, 50f), Box(1, 108f, 50f) };
        var text = new PageText("AB", boxes);

        Assert.Same(boxes, text.DedupedCharBoxes);
    }

    [Fact]
    public void SurvivingIndicesStillAddressTheirOwnCharacters()
    {
        var text = new PageText("ABAB", [
            Box(0, 100f, 50f), Box(1, 108f, 50f),
            Box(2, 100.1f, 50f), Box(3, 108.1f, 50f),
        ]);

        // Text is untouched by dedup, so every surviving index must still resolve — and to
        // the character the box was drawn for.
        foreach (var cb in text.DedupedCharBoxes)
        {
            Assert.InRange(cb.Index, 0, text.Text.Length - 1);
            char expected = cb.Left < 104f ? 'A' : 'B';
            Assert.Equal(expected, text.Text[cb.Index]);
        }
    }

    [Fact]
    public void OutOfRangeIndex_IsKeptRatherThanCrashing()
    {
        // A provider that reports a box with no matching text offset must not take the page
        // down; such boxes are passed through untouched.
        var text = new PageText("A", [Box(0, 100f, 50f), Box(99, 100f, 50f)]);

        Assert.Equal(2, text.DedupedCharBoxes.Count);
    }

    [Fact]
    public void ResultIsCached()
    {
        var text = new PageText("ABAB", [
            Box(0, 100f, 50f), Box(1, 108f, 50f),
            Box(2, 100.1f, 50f), Box(3, 108.1f, 50f),
        ]);

        Assert.Same(text.DedupedCharBoxes, text.DedupedCharBoxes);
    }
}
