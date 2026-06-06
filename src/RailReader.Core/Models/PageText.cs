namespace RailReader.Core.Models;

public record PageText(string Text, List<CharBox> CharBoxes)
{
    /// <summary>
    /// Extracts text whose character midpoints fall within the given rectangle.
    /// Returns null if no characters match.
    /// </summary>
    public string? ExtractTextInRect(float left, float top, float right, float bottom)
    {
        var chars = new List<(int Index, char Ch)>();
        foreach (var cb in CharBoxes)
        {
            float midX = (cb.Left + cb.Right) / 2f;
            float midY = (cb.Top + cb.Bottom) / 2f;
            if (midX >= left && midX <= right && midY >= top && midY <= bottom
                && cb.Index >= 0 && cb.Index < Text.Length)
            {
                chars.Add((cb.Index, Text[cb.Index]));
            }
        }
        if (chars.Count == 0) return null;
        chars.Sort((a, b) => a.Index.CompareTo(b.Index));
        return new string(chars.Select(c => c.Ch).ToArray()).Trim();
    }

    /// <summary>
    /// Extracts text within a layout block's bounding box.
    /// </summary>
    public string ExtractBlockText(LayoutBlock block)
    {
        var bbox = block.BBox;
        return ExtractTextInRect(bbox.X, bbox.Y, bbox.X + bbox.W, bbox.Y + bbox.H) ?? "";
    }

    /// <summary>
    /// Extracts up to <paramref name="maxChars"/> characters of a block's text in
    /// reading order (trimmed for display). Sets <paramref name="truncated"/> to
    /// true when the block contains more matched characters than were returned, so
    /// callers can decide whether to append an ellipsis based on the real content
    /// length rather than the trimmed preview length (trailing whitespace removed
    /// by the trim must not hide that the text was cut). Avoids allocating a
    /// full-length string when only a preview is needed.
    /// </summary>
    public string ExtractBlockText(LayoutBlock block, int maxChars, out bool truncated)
    {
        truncated = false;
        if (maxChars <= 0) return "";
        var bbox = block.BBox;
        float left = bbox.X, top = bbox.Y, right = bbox.X + bbox.W, bottom = bbox.Y + bbox.H;
        // Collect one extra index so we can distinguish "exactly maxChars" from
        // "more than maxChars" independently of any later trimming.
        var indices = new List<int>(Math.Min(maxChars + 1, CharBoxes.Count));
        foreach (var cb in CharBoxes)
        {
            float midX = (cb.Left + cb.Right) / 2f;
            float midY = (cb.Top + cb.Bottom) / 2f;
            if (midX >= left && midX <= right && midY >= top && midY <= bottom
                && cb.Index >= 0 && cb.Index < Text.Length)
            {
                indices.Add(cb.Index);
                if (indices.Count > maxChars) { truncated = true; break; }
            }
        }
        if (indices.Count == 0) return "";
        indices.Sort();
        int take = Math.Min(indices.Count, maxChars);
        // Don't slice through a surrogate pair at the cut.
        if (truncated && take > 0 && char.IsHighSurrogate(Text[indices[take - 1]])) take--;
        var buf = new char[take];
        for (int i = 0; i < take; i++) buf[i] = Text[indices[i]];
        return new string(buf).Trim();
    }
}

public record struct CharBox(int Index, float Left, float Top, float Right, float Bottom);
