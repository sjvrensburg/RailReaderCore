using System.Text.Json.Serialization;
using RailReader.Core.Services;

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
public abstract partial class Annotation
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

/// <summary>
/// Removes an annotation <b>and every reply beneath it</b> (<see cref="AnnotationThreads.Descendants"/>) as
/// one undo step — the Acrobat behaviour: deleting a comment deletes its thread, never leaving replies
/// stranded as unrelated notes.
/// </summary>
public sealed class RemoveAnnotationAction(int pageIndex, Annotation annotation) : IUndoAction
{
    /// <summary>Everything the last <see cref="Redo"/> removed, in removal order, with the position each
    /// came from. Empty when it found nothing to remove (annotation absent from the page, or page key
    /// missing) — Undo of a no-op removal must itself be a no-op: inserting anyway would either throw
    /// or silently duplicate the annotation onto a page it was never on.</summary>
    private readonly List<(int Page, int Index, Annotation Annotation)> _removed = [];

    public void Undo(AnnotationFile file)
    {
        // Reverse of removal order, so each index is valid again by the time it is re-inserted.
        for (int i = _removed.Count - 1; i >= 0; i--)
        {
            var (page, index, ann) = _removed[i];
            if (!file.Pages.TryGetValue(page, out var list))
            {
                list = [];
                file.Pages[page] = list;
            }
            list.Insert(Math.Min(index, list.Count), ann);
        }
        _removed.Clear();
    }

    public void Redo(AnnotationFile file)
    {
        _removed.Clear();
        if (!file.Pages.TryGetValue(pageIndex, out var list) || list.IndexOf(annotation) is var at && at < 0) return;

        var doomed = AnnotationThreads.Descendants(file, annotation);
        list.RemoveAt(at);
        _removed.Add((pageIndex, at, annotation));
        foreach (var page in file.Pages.Keys.ToList())
        {
            var l = file.Pages[page];
            for (int i = l.Count - 1; i >= 0; i--)
            {
                if (!doomed.Contains(l[i])) continue;
                _removed.Add((page, i, l[i]));
                l.RemoveAt(i);
            }
        }
    }
}

/// <summary>
/// Adds a reply (<see cref="Annotation.InReplyTo"/> set) as ONE undo step, including the
/// <see cref="Annotation.NativeId"/> minted on the thread root when it had none — undo removes
/// the reply and un-mints the id, so a reply can never be left pointing at an id that no longer
/// exists.
/// </summary>
public sealed class AddReplyAction(int pageIndex, Annotation root, Annotation reply, string? mintedRootId) : IUndoAction
{
    private readonly AddAnnotationAction _add = new(pageIndex, reply);

    public void Redo(AnnotationFile file)
    {
        if (mintedRootId is not null) root.NativeId = mintedRootId;
        _add.Redo(file);
    }

    public void Undo(AnnotationFile file)
    {
        _add.Undo(file);
        if (mintedRootId is not null) root.NativeId = null;
    }
}

/// <summary>
/// Edit of an annotation's comment body (<see cref="Annotation.Contents"/>, plus the legacy
/// <see cref="TextNoteAnnotation.Text"/> for notes, which <see cref="Annotation.EffectiveContents"/>
/// prefers over a null <c>Contents</c>). Records old and new body so a note edit or a highlight
/// comment is a single undo step.
/// </summary>
public sealed class EditContentsAction(Annotation annotation, string? oldContents, string? newContents) : IUndoAction
{
    public void Undo(AnnotationFile file) => Apply(annotation, oldContents);
    public void Redo(AnnotationFile file) => Apply(annotation, newContents);

    /// <summary>Writes <paramref name="contents"/> as the annotation's body. For a note the legacy
    /// <c>Text</c> is kept in sync (null → empty) so both read paths agree.</summary>
    public static void Apply(Annotation annotation, string? contents)
    {
        annotation.Contents = contents;
        if (annotation is TextNoteAnnotation note) note.Text = contents ?? "";
    }
}

/// <summary>
/// Replaces several annotations on one page with a single merged one as ONE undo step
/// (see <c>DocumentModel.MergeOverlappingTextMarkup</c>). The merged annotation is inserted at
/// the list position of the first removed source so z-order within the markup tier is kept;
/// undo restores every source at its original index.
/// </summary>
public sealed class MergeAnnotationsAction(int pageIndex, IReadOnlyList<Annotation> sources, Annotation merged) : IUndoAction
{
    private readonly List<(int Index, Annotation Annotation)> _removed = [];
    private readonly List<(Annotation Reply, string? Parent)> _repointed = [];
    private bool _mintedId;
    private int _insertIndex = -1;

    public void Redo(AnnotationFile file)
    {
        _removed.Clear();
        _repointed.Clear();
        _mintedId = false;
        _insertIndex = -1;
        if (!file.Pages.TryGetValue(pageIndex, out var list))
        {
            list = [];
            file.Pages[pageIndex] = list;
        }
        for (int i = 0; i < list.Count; i++)
            if (sources.Contains(list[i])) _removed.Add((i, list[i]));
        for (int k = _removed.Count - 1; k >= 0; k--)
            list.RemoveAt(_removed[k].Index);
        _insertIndex = _removed.Count > 0 ? Math.Min(_removed[0].Index, list.Count) : list.Count;
        list.Insert(_insertIndex, merged);
        RepointReplies(file);
    }

    /// <summary>Replies to a merged-away source are re-pointed at the merged annotation (minting
    /// its id if it has none) so the thread follows the markup instead of dangling.</summary>
    private void RepointReplies(AnnotationFile file)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, src) in _removed)
            if (!string.IsNullOrEmpty(src.NativeId)) ids.Add(src.NativeId);
        if (ids.Count == 0) return;

        foreach (var ann in file.Pages.Values.SelectMany(l => l))
        {
            if (ReferenceEquals(ann, merged) || string.IsNullOrEmpty(ann.InReplyTo) || !ids.Contains(ann.InReplyTo)) continue;
            if (string.IsNullOrEmpty(merged.NativeId))
            {
                merged.NativeId = Guid.NewGuid().ToString();
                _mintedId = true;
            }
            _repointed.Add((ann, ann.InReplyTo));
            ann.InReplyTo = merged.NativeId;
        }
    }

    public void Undo(AnnotationFile file)
    {
        foreach (var (reply, parent) in _repointed) reply.InReplyTo = parent;
        _repointed.Clear();
        if (_mintedId) merged.NativeId = null;
        _mintedId = false;
        if (!file.Pages.TryGetValue(pageIndex, out var list)) return;
        list.Remove(merged);
        // Re-insert in ascending index order so each original position is valid again.
        foreach (var (index, ann) in _removed)
            list.Insert(Math.Min(index, list.Count), ann);
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
