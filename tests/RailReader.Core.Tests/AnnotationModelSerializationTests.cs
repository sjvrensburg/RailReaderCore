using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Covers the native-PDF annotation model extensions (PR 1 step 2): the new
/// markup subtypes and the round-trip metadata must survive polymorphic
/// JSON serialization, and legacy sidecar JSON must still deserialize.
/// </summary>
public class AnnotationModelSerializationTests
{
    private static AnnotationFile RoundTrip(AnnotationFile file)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rr-annot-{Guid.NewGuid():N}.json");
        try
        {
            AnnotationService.ExportJson(file, path);
            var loaded = AnnotationService.ImportJson(path);
            Assert.NotNull(loaded);
            return loaded!;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NewMarkupSubtypes_SurvivePolymorphicRoundTrip()
    {
        var file = new AnnotationFile();
        file.Pages[0] =
        [
            new HighlightAnnotation { Rects = [new HighlightRect(1, 2, 3, 4)] },
            new UnderlineAnnotation { Rects = [new HighlightRect(5, 6, 7, 8)] },
            new StrikeOutAnnotation { Rects = [new HighlightRect(9, 10, 11, 12)] },
            new SquigglyAnnotation { Rects = [new HighlightRect(13, 14, 15, 16)] },
            new CaretAnnotation { X = 17, Y = 18, W = 5, H = 6 },
            new FreeTextAnnotation { X = 19, Y = 20, W = 100, H = 40, Contents = "typed note" },
        ];

        var page = RoundTrip(file).Pages[0];

        Assert.Collection(page,
            a => Assert.IsType<HighlightAnnotation>(a),
            a => Assert.IsType<UnderlineAnnotation>(a),
            a => Assert.IsType<StrikeOutAnnotation>(a),
            a => Assert.IsType<SquigglyAnnotation>(a),
            a => Assert.IsType<CaretAnnotation>(a),
            a => Assert.IsType<FreeTextAnnotation>(a));

        Assert.Equal(new HighlightRect(5, 6, 7, 8), ((UnderlineAnnotation)page[1]).Rects[0]);
        Assert.Equal("typed note", page[5].Contents);
    }

    [Fact]
    public void RoundTripMetadata_IsPreserved()
    {
        var created = new DateTimeOffset(2026, 5, 28, 10, 4, 11, TimeSpan.FromHours(2));
        var modified = new DateTimeOffset(2026, 5, 28, 10, 4, 31, TimeSpan.FromHours(2));

        var file = new AnnotationFile();
        file.Pages[0] =
        [
            new HighlightAnnotation
            {
                Rects = [new HighlightRect(1, 2, 3, 4)],
                Opacity = 0.4f,
                Author = "cclohessy",
                Contents = "accuracy",
                Subject = "Comment on Text",
                NativeId = "f4d62817-6d31-4f0a-9a3a-5724fb1e80d9",
                CreatedUtc = created,
                ModifiedUtc = modified,
                State = ReviewState.Accepted,
                Source = AnnotationSource.InPdf,
                ColorComponents = [0.0235291f, 0.541183f, 0.109802f],
            },
        ];

        var a = RoundTrip(file).Pages[0][0];

        Assert.Equal("cclohessy", a.Author);
        Assert.Equal("accuracy", a.Contents);
        Assert.Equal("Comment on Text", a.Subject);
        Assert.Equal("f4d62817-6d31-4f0a-9a3a-5724fb1e80d9", a.NativeId);
        Assert.Equal(created, a.CreatedUtc);
        Assert.Equal(modified, a.ModifiedUtc);
        Assert.Equal(ReviewState.Accepted, a.State);
        Assert.Equal(AnnotationSource.InPdf, a.Source);
        Assert.NotNull(a.ColorComponents);
        Assert.Equal(3, a.ColorComponents!.Length);
        Assert.Equal(0.541183f, a.ColorComponents[1], 5);
    }

    [Fact]
    public void Reply_LinksToParentByNativeId()
    {
        var file = new AnnotationFile();
        file.Pages[0] =
        [
            new HighlightAnnotation { NativeId = "parent", Contents = "original" },
            new TextNoteAnnotation { InReplyTo = "parent", Contents = "a reply", X = 1, Y = 2 },
        ];

        var page = RoundTrip(file).Pages[0];
        Assert.Equal("parent", page[1].InReplyTo);
    }

    [Fact]
    public void LegacySidecarJson_StillDeserializes()
    {
        // A sidecar written before the model extension: no metadata fields,
        // discriminators "highlight" / "text_note" only.
        var legacy = """
        {
          "version": 1,
          "source_pdf": "old.pdf",
          "pages": {
            "0": [
              { "type": "highlight", "color": "#FFFF00", "opacity": 0.4,
                "rects": [ { "x": 10, "y": 10, "w": 100, "h": 20 } ] },
              { "type": "text_note", "color": "#FFFF00", "opacity": 1,
                "x": 5, "y": 6, "text": "hi" }
            ]
          },
          "bookmarks": []
        }
        """;
        var path = Path.Combine(Path.GetTempPath(), $"rr-legacy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, legacy);
        try
        {
            var loaded = AnnotationService.ImportJson(path);
            Assert.NotNull(loaded);
            var page = loaded!.Pages[0];
            var hl = Assert.IsType<HighlightAnnotation>(page[0]);
            Assert.Equal(new HighlightRect(10, 10, 100, 20), hl.Rects[0]);
            // Fields absent in legacy JSON take their defaults.
            Assert.Null(hl.Author);
            Assert.Equal(ReviewState.None, hl.State);
            Assert.Equal(AnnotationSource.RailReader, hl.Source);
            var note = Assert.IsType<TextNoteAnnotation>(page[1]);
            Assert.Equal("hi", note.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
