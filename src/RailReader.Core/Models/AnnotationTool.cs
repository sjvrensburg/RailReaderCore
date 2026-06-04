namespace RailReader.Core.Models;

public enum AnnotationTool
{
    None,
    TextSelect,
    Highlight,
    Pen,
    TextNote,
    Rectangle,
    Eraser,

    // Appended (not interleaved) so the existing members keep their underlying values —
    // the addition stays binary-additive for any consumer that persists the tool by ordinal.
    // Drag-over-text markup tools share the Highlight selection→rects pipeline, differing
    // only in the concrete TextMarkupAnnotation subtype committed.
    Underline,
    StrikeOut,
    Squiggly,
    // FreeText ("typewriter") — drag a box, then the UI supplies text.
    FreeText,
}
