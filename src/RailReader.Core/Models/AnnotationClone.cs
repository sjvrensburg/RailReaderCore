using System.Text.Json;
using System.Text.Json.Serialization;

namespace RailReader.Core.Models;

public abstract partial class Annotation
{
    /// <summary>
    /// True for the types that can be copied and pasted: position-anchored annotations
    /// (TextNote, FreeText, Rect, Freehand). Text markup only means something on the text it
    /// was made over, and PDFium cannot create carets.
    /// </summary>
    public static bool IsCopyable(Annotation? a)
        => a is TextNoteAnnotation or FreeTextAnnotation or RectAnnotation or FreehandAnnotation;

    /// <summary>
    /// Deep copy of <paramref name="source"/> as a brand-new annotation. Goes through the
    /// polymorphic JSON contract so new subtypes are covered without touching this method.
    /// <see cref="NativeId"/> is cleared: <c>PdfAnnotationWriter</c> and
    /// <c>CompositeAnnotationStore</c> both key on <c>/NM</c>, so a copy that kept its source's id
    /// would look fine in-session and vanish after save + reopen. Also resets
    /// <see cref="Source"/>, <see cref="InReplyTo"/> and the timestamps; author, colour,
    /// opacity, flags, contents and review state are kept.
    /// </summary>
    public static Annotation CloneForPaste(Annotation source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var json = JsonSerializer.Serialize(source, AnnotationCloneJsonContext.Default.Annotation);
        var clone = JsonSerializer.Deserialize(json, AnnotationCloneJsonContext.Default.Annotation)
            ?? throw new InvalidOperationException("Annotation clone deserialised to null");
        clone.NativeId = null;
        clone.Source = AnnotationSource.RailReader;
        clone.InReplyTo = null;
        clone.CreatedUtc = DateTimeOffset.UtcNow;
        clone.ModifiedUtc = null;
        return clone;
    }
}

[JsonSerializable(typeof(Annotation))]
internal partial class AnnotationCloneJsonContext : JsonSerializerContext;
