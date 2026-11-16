using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>#204: <see cref="AnnotationFileManager.Saved"/> fires once per successful write.</summary>
public class AnnotationSavedEventTests
{
    private sealed class MemoryStore : IAnnotationStore
    {
        public bool Fail;
        public int Saves, Deletes;
        public AnnotationFile? Load(string pdfPath, string? password = null) => null;
        public bool Save(string pdfPath, AnnotationFile annotations, string? password = null) { Saves++; return !Fail; }
        public bool Delete(string pdfPath, string? password = null) { Deletes++; return !Fail; }
    }

    private const string Pdf = "/tmp/railreader-saved-event/doc.pdf";
    private readonly MemoryStore _store = new();
    private readonly QueuedThreadMarshaller _ui = new();
    private readonly List<AnnotationsSavedEventArgs> _events = [];

    private AnnotationFileManager Manager()
    {
        var m = new AnnotationFileManager(_store, _ui);
        m.Saved += (_, e) => _events.Add(e);
        return m;
    }

    private static void Edit(AnnotationFile file) => file.Bookmarks.Add(new BookmarkEntry { Name = "b", Page = 0 });

    [Fact]
    public void DebouncedAutoSaveRaisesOnceOnTheUiThread()
    {
        using var m = Manager();
        var file = m.Checkout(Pdf);
        Edit(file);
        m.MarkDirty(Pdf);
        m.MarkDirty(Pdf);   // debounced: still one write

        Assert.True(TestWait.Until(() => _events.Count > 0, _ui));
        var e = Assert.Single(_events);
        Assert.Equal(Pdf, e.PdfPath);
        Assert.Same(file, e.Annotations);
        Assert.False(e.Deleted);
        Assert.Equal(1, _store.Saves);
    }

    [Fact]
    public void LastReleaseRaises_AndAFlushWithNothingUnsavedDoesNot()
    {
        using var m = Manager();
        Edit(m.Checkout(Pdf));
        m.MarkDirty(Pdf);
        m.Release(Pdf);
        Assert.Single(_events);

        m.Checkout(Pdf);
        m.FlushAll();        // clean: no write, no event
        m.Release(Pdf);
        Assert.Single(_events);
    }

    [Fact]
    public void EmptiedDocumentRaisesWithDeleted()
    {
        using var m = Manager();
        m.Checkout(Pdf);
        m.MarkDirty(Pdf);    // dirty but empty: the store deletes
        m.FlushAll();
        Assert.True(Assert.Single(_events).Deleted);
        Assert.Equal(1, _store.Deletes);
    }

    [Fact]
    public void FailedSaveRaisesNothing()
    {
        _store.Fail = true;
        string? failure = null;
        using var m = Manager();
        m.OnSaveFailure = msg => failure = msg;
        Edit(m.Checkout(Pdf));
        m.MarkDirty(Pdf);
        m.FlushAll();
        Assert.Empty(_events);
        Assert.NotNull(failure);
    }

    [Fact]
    public void AThrowingHandlerDoesNotStopOtherDocumentsFlushing()
    {
        const string other = "/tmp/railreader-saved-event/other.pdf";
        var m = new AnnotationFileManager(_store, _ui);
        m.Saved += (_, e) => { _events.Add(e); throw new InvalidOperationException("handler bug"); };
        Edit(m.Checkout(Pdf));
        Edit(m.Checkout(other));
        m.MarkDirty(Pdf);
        m.MarkDirty(other);
        m.Dispose();
        Assert.Equal(2, _events.Count);
        Assert.Equal(2, _store.Saves);
    }
}
