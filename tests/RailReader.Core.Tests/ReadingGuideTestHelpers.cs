using System.Text;
using RailReader.Core.Models;

namespace RailReader.Core.Tests;

/// <summary>
/// Builds synthetic <see cref="PageText"/> / <see cref="PageAnalysis"/> pairs for the reading-guide
/// tests: monospace glyphs <see cref="CharW"/> wide and <see cref="CharH"/> tall, laid out line by
/// line at explicit page positions, so word rects and line bands are exact.
/// </summary>
internal sealed class GuidePageBuilder
{
    public const float CharW = 5f;
    public const float CharH = 10f;

    private readonly StringBuilder _text = new();
    private readonly List<CharBox> _boxes = [];

    /// <summary>
    /// Appends <paramref name="line"/> with its glyphs starting at (<paramref name="x"/>,
    /// <paramref name="top"/>), one box per non-space character (spaces get a box too when
    /// <paramref name="spaceBoxes"/> is set — some producers emit them), then a newline with no box.
    /// </summary>
    public GuidePageBuilder Line(string line, float x, float top, bool spaceBoxes = false)
    {
        for (int i = 0; i < line.Length; i++)
        {
            int index = _text.Length;
            _text.Append(line[i]);
            if (line[i] == ' ' && !spaceBoxes) continue;
            float left = x + i * CharW;
            _boxes.Add(new CharBox(index, left, top, left + CharW, top + CharH));
        }
        _text.Append('\n');
        return this;
    }

    /// <summary>Appends raw text with explicit boxes (ligatures, odd spacing).</summary>
    public GuidePageBuilder Raw(string text, params CharBox[] boxesRelativeToText)
    {
        int offset = _text.Length;
        _text.Append(text);
        foreach (var b in boxesRelativeToText)
            _boxes.Add(b with { Index = b.Index + offset });
        return this;
    }

    public PageText Build() => new(_text.ToString(), _boxes);

    /// <summary>A block at (<paramref name="x"/>, <paramref name="y"/>) sized <paramref name="w"/>×<paramref name="h"/>
    /// with one <see cref="LineInfo"/> per entry of <paramref name="lineTops"/> (each <see cref="CharH"/> tall,
    /// spanning the block's width).</summary>
    public static LayoutBlock Block(float x, float y, float w, float h, BlockRole role = BlockRole.Text, params float[] lineTops)
    {
        var block = new LayoutBlock { BBox = new BBox(x, y, w, h), Role = role };
        foreach (var top in lineTops)
            block.Lines.Add(new LineInfo(top + CharH / 2f, CharH, x, w));
        return block;
    }

    public static PageAnalysis MakeAnalysis(params LayoutBlock[] blocks)
        => new() { Blocks = [.. blocks], PageWidth = 500, PageHeight = 700 };
}
