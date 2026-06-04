using System.Text.Json.Serialization;

namespace RailReader.Core.Models;

public class BookmarkEntry
{
    public string Name { get; set; } = "";
    public int Page { get; set; }

    [JsonIgnore]
    public string PageDisplay => $"Page {Page + 1}";
}

public class AnnotationFile
{
    public int Version { get; set; } = 1;
    public string SourcePdf { get; set; } = "";
    /// <summary>Full path to the source PDF. Used for orphan detection in internal storage.</summary>
    public string SourcePdfPath { get; set; } = "";
    public Dictionary<int, List<Annotation>> Pages { get; set; } = [];
    public List<BookmarkEntry> Bookmarks { get; set; } = [];
}

/// <summary>Where an annotation came from — used to decide write-back behaviour.</summary>
public enum AnnotationSource
{
    /// <summary>Authored in RailReader; not (yet) present in the PDF.</summary>
    RailReader,
    /// <summary>Read from the PDF's own /Annots dictionary.</summary>
    InPdf,
}

/// <summary>
/// Acrobat review state (/State under the "Review" /StateModel). <see cref="None"/>
/// means no explicit review state has been set.
/// </summary>
public enum ReviewState
{
    None,
    Accepted,
    Rejected,
    Cancelled,
    Completed,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HighlightAnnotation), "highlight")]
[JsonDerivedType(typeof(UnderlineAnnotation), "underline")]
[JsonDerivedType(typeof(StrikeOutAnnotation), "strikeout")]
[JsonDerivedType(typeof(SquigglyAnnotation), "squiggly")]
[JsonDerivedType(typeof(FreehandAnnotation), "freehand")]
[JsonDerivedType(typeof(TextNoteAnnotation), "text_note")]
[JsonDerivedType(typeof(RectAnnotation), "rect")]
[JsonDerivedType(typeof(CaretAnnotation), "caret")]
[JsonDerivedType(typeof(FreeTextAnnotation), "free_text")]
public abstract class Annotation
{
    public string Color { get; set; } = "#FFFF00";
    public float Opacity { get; set; } = 1.0f;

    // --- Native PDF round-trip metadata ---
    // Null/empty for RailReader-authored annotations until they are written into
    // the PDF; populated when read from an existing /Annots dictionary.

    /// <summary>Author — PDF /T.</summary>
    public string? Author { get; set; }

    /// <summary>Comment / note text — PDF /Contents. For FreeText this is the body.</summary>
    public string? Contents { get; set; }

    /// <summary>Intent / subject label — PDF /Subj (e.g. "Comment on Text").</summary>
    public string? Subject { get; set; }

    /// <summary>Stable unique id — PDF /NM. Identity and dedupe key across stores.</summary>
    public string? NativeId { get; set; }

    /// <summary>Creation timestamp — PDF /CreationDate.</summary>
    public DateTimeOffset? CreatedUtc { get; set; }

    /// <summary>Last-modified timestamp — PDF /M.</summary>
    public DateTimeOffset? ModifiedUtc { get; set; }

    /// <summary>Review state — PDF /State + /StateModel "Review".</summary>
    public ReviewState State { get; set; } = ReviewState.None;

    /// <summary>Parent annotation's <see cref="NativeId"/> for replies — PDF /IRT.</summary>
    public string? InReplyTo { get; set; }

    /// <summary>Provenance of this annotation (in-PDF vs RailReader-authored).</summary>
    public AnnotationSource Source { get; set; } = AnnotationSource.RailReader;

    /// <summary>
    /// Faithful DeviceRGB colour components (each 0..1) from the PDF /C array, when
    /// known. Authoritative over <see cref="Color"/> for round-trip; null when unknown
    /// (e.g. PDFium cannot report /C because the annotation carries an /AP stream).
    /// Length 3 when set.
    /// </summary>
    public float[]? ColorComponents { get; set; }

    /// <summary>
    /// PDF annotation flags (/F) — Print, Hidden, NoView, ReadOnly, Locked, etc. Preserved
    /// so an edit/recreate doesn't strip them. 0 means "unset"; the writer then defaults to
    /// Print so RailReader-authored annotations remain printable.
    /// </summary>
    public int Flags { get; set; }

    /// <summary>
    /// The note/comment body to display and round-trip — <see cref="Contents"/>, or empty
    /// string when unset. <see cref="TextNoteAnnotation"/> overrides this to fall back to its
    /// legacy <see cref="TextNoteAnnotation.Text"/> field, so the read path (which fills
    /// <see cref="Contents"/>) and the author path (which fills <c>Text</c>) present a single
    /// body string to renderers, the writer, and equivalence checks. The single source of
    /// truth for "effective contents" across all assemblies.
    /// </summary>
    [JsonIgnore]
    public virtual string EffectiveContents => Contents ?? "";
}

/// <summary>Text-markup annotation anchored to one or more text quads (PDF QuadPoints).</summary>
public abstract class TextMarkupAnnotation : Annotation
{
    public List<HighlightRect> Rects { get; set; } = [];
}

public class HighlightAnnotation : TextMarkupAnnotation;
public class UnderlineAnnotation : TextMarkupAnnotation;
public class StrikeOutAnnotation : TextMarkupAnnotation;
public class SquigglyAnnotation : TextMarkupAnnotation;

public record struct HighlightRect(float X, float Y, float W, float H);

public class FreehandAnnotation : Annotation
{
    public float StrokeWidth { get; set; } = 2f;
    public List<PointF> Points { get; set; } = [];
}

public record struct PointF(float X, float Y);

public class TextNoteAnnotation : Annotation
{
    public float X { get; set; }
    public float Y { get; set; }
    public string Text { get; set; } = "";

    /// <inheritdoc/>
    /// <remarks>Falls back to the legacy <see cref="Text"/> field when <see cref="Annotation.Contents"/>
    /// is null — notes read from a PDF carry the body in <c>/Contents</c>, while RailReader-authored
    /// notes carry it in <c>Text</c>.</remarks>
    [JsonIgnore]
    public override string EffectiveContents => Contents ?? Text;

    [JsonIgnore]
    public bool IsExpanded { get; set; }
}

public class RectAnnotation : Annotation
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public float StrokeWidth { get; set; } = 2f;
    public bool Filled { get; set; }
}

/// <summary>
/// Caret ("inserted text") markup — a small insertion marker. PDF /Caret.
/// The note text (if any) lives in <see cref="Annotation.Contents"/>.
/// <para>Read-only: PDFium can surface and preserve existing carets but cannot
/// <i>create</i> them (Caret is not in its supported-subtype whitelist), and
/// RailReader has no caret-authoring tool.</para>
/// </summary>
public class CaretAnnotation : Annotation
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
}

/// <summary>
/// Free-text ("typewriter") annotation drawn directly on the page. PDF /FreeText.
/// The displayed text lives in <see cref="Annotation.Contents"/>; <see cref="Color"/> is
/// the text colour and <see cref="FontSize"/> its point size — both synthesised into the
/// PDF /DA (default appearance) on write so strict viewers can render the text.
/// </summary>
public class FreeTextAnnotation : Annotation
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }

    /// <summary>Text point size, written into /DA. Defaults to 12.</summary>
    public float FontSize { get; set; } = 12f;
}

public interface IUndoAction
{
    void Undo(AnnotationFile file);
    void Redo(AnnotationFile file);
}

public sealed class AddAnnotationAction(int pageIndex, Annotation annotation) : IUndoAction
{
    public void Undo(AnnotationFile file)
    {
        if (file.Pages.TryGetValue(pageIndex, out var list))
            list.Remove(annotation);
    }

    public void Redo(AnnotationFile file)
    {
        if (!file.Pages.TryGetValue(pageIndex, out var list))
        {
            list = [];
            file.Pages[pageIndex] = list;
        }
        list.Add(annotation);
    }
}

public sealed class RemoveAnnotationAction(int pageIndex, Annotation annotation) : IUndoAction
{
    private int _index;

    public void Undo(AnnotationFile file)
    {
        if (!file.Pages.TryGetValue(pageIndex, out var list))
        {
            list = [];
            file.Pages[pageIndex] = list;
        }
        list.Insert(Math.Min(_index, list.Count), annotation);
    }

    public void Redo(AnnotationFile file)
    {
        if (file.Pages.TryGetValue(pageIndex, out var list))
        {
            _index = list.IndexOf(annotation);
            list.Remove(annotation);
        }
    }
}

public sealed class MoveAnnotationAction(
    Annotation annotation, PositionSnapshot oldPosition, PositionSnapshot newPosition) : IUndoAction
{
    public void Undo(AnnotationFile file) => oldPosition.ApplyTo(annotation);
    public void Redo(AnnotationFile file) => newPosition.ApplyTo(annotation);
}

public class PositionSnapshot
{
    public float X { get; init; }
    public float Y { get; init; }
    public List<PointF>? Points { get; init; }
    public List<HighlightRect>? Rects { get; init; }

    public static PositionSnapshot Capture(Annotation annotation) => annotation switch
    {
        TextNoteAnnotation tn => new() { X = tn.X, Y = tn.Y },
        FreehandAnnotation f => new() { Points = [.. f.Points] },
        TextMarkupAnnotation m => new() { Rects = [.. m.Rects] },
        RectAnnotation r => new() { X = r.X, Y = r.Y },
        CaretAnnotation c => new() { X = c.X, Y = c.Y },
        FreeTextAnnotation ft => new() { X = ft.X, Y = ft.Y },
        _ => new(),
    };

    public void ApplyTo(Annotation annotation)
    {
        switch (annotation)
        {
            case TextNoteAnnotation tn:
                tn.X = X; tn.Y = Y;
                break;
            case FreehandAnnotation f when Points is not null:
                f.Points = [.. Points];
                break;
            case TextMarkupAnnotation m when Rects is not null:
                m.Rects = [.. Rects];
                break;
            case RectAnnotation r:
                r.X = X; r.Y = Y;
                break;
            case CaretAnnotation c:
                c.X = X; c.Y = Y;
                break;
            case FreeTextAnnotation ft:
                ft.X = X; ft.Y = Y;
                break;
        }
    }
}

public enum ResizeHandle
{
    None,
    TopLeft, Top, TopRight,
    Right,
    BottomRight, Bottom, BottomLeft,
    Left,
}

public sealed class ResizeFreehandAction(
    FreehandAnnotation annotation, List<PointF> oldPoints, List<PointF> newPoints) : IUndoAction
{
    public void Undo(AnnotationFile file) => annotation.Points = [.. oldPoints];
    public void Redo(AnnotationFile file) => annotation.Points = [.. newPoints];
}
