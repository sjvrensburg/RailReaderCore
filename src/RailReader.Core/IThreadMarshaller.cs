namespace RailReader.Core;

/// <summary>
/// Abstracts UI thread marshalling so Core types can post work
/// to the main thread without depending on Avalonia.
/// </summary>
public interface IThreadMarshaller
{
    void Post(Action action);

    /// <summary>
    /// Debug assertion that the current thread is the UI thread.
    /// Default implementation is a no-op (for tests/CLI).
    /// </summary>
    void AssertUIThread() { }
}

/// <summary>
/// Executes actions synchronously on the calling thread.
/// Used for headless contexts (tests, CLI agent) where no UI thread exists.
/// Posts are serialized across threads — never overlapping each other — because that is
/// part of the guarantee a real UI thread gives and the one Core's marshalled callbacks
/// are written against (they mutate caches and per-view state). Without it, callbacks
/// posted from concurrent background tasks (e.g. two analysis-prep tasks) run truly in
/// parallel and corrupt that state (issue #167). Note the limit: this serializes posts
/// against OTHER POSTS only, not against direct calls the host makes on its own thread —
/// state touched from both sides (e.g. DocumentModel's text caches, the worker's
/// in-flight set) must still be thread-safe in its own right. The lock is reentrant,
/// so a post that posts again on the same thread proceeds.
/// </summary>
public sealed class SynchronousThreadMarshaller : IThreadMarshaller
{
    private readonly object _gate = new();

    public void Post(Action action)
    {
        lock (_gate) action();
    }
}
