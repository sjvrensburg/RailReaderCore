using System.Collections.Concurrent;

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
/// so a post that posts again on the same thread proceeds — but a callback must not block
/// waiting on another thread that itself posts (e.g. a synchronous hand-off to a real UI
/// thread from <see cref="Services.AnalysisWorker"/>'s result-available callback, which runs
/// inside a post): that thread waits on this lock and the two deadlock.
/// For a host that can run its own loop, <see cref="QueuedThreadMarshaller"/> gives the real
/// guarantee instead: callbacks run only on the host's thread, between its own calls (#174).
/// </summary>
public sealed class SynchronousThreadMarshaller : IThreadMarshaller
{
    private readonly object _gate = new();

    public void Post(Action action)
    {
        lock (_gate) action();
    }
}

/// <summary>
/// A stand-in UI thread for headless hosts (tests, CLI) that can pump: <see cref="Post"/> only
/// queues, and <see cref="Pump"/> runs the queued callbacks on the thread that constructed the
/// marshaller. That is the guarantee a desktop dispatcher gives and the one Core's marshalled
/// callbacks are written against: a callback never runs concurrently with the host's own calls,
/// so nothing it touches has to be thread-safe in its own right. <see cref="SynchronousThreadMarshaller"/>
/// cannot give it, because it runs each callback inline on the posting thread (issue #174).
///
/// <para>
/// Semantics: callbacks run in FIFO order; <see cref="Pump"/> drains until the queue is empty,
/// so a callback that posts during a pump runs in the same pump, and once <see cref="Pump"/>
/// returns nothing posted before or during it is still pending. There is no lock, so no deadlock
/// hazard. A <see cref="Pump"/> called from inside a running callback (say a
/// <c>ResultAvailable</c> handler that ticks a frame) returns 0 at once: the outer pump runs the
/// rest, after that callback finishes, as a dispatcher would.
/// <see cref="DocumentController.PollAnalysisResults"/> (and so <see cref="DocumentController.PumpAnalysis"/>
/// and <see cref="DocumentController.TickViewport(Viewport, double)"/>) pumps it automatically, so a
/// controller host needs no extra wiring; a host that drives the worker directly calls
/// <see cref="Pump"/> itself.
/// </para>
/// <para>
/// <see cref="AssertUIThread"/> throws off the owning thread, where the desktop marshaller only
/// debug-asserts: the point of this type is to surface contract violations that the synchronous
/// marshaller hides. An async test that resumes on another thread should keep using
/// <see cref="SynchronousThreadMarshaller"/>.
/// </para>
/// </summary>
public sealed class QueuedThreadMarshaller : IThreadMarshaller
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private bool _pumping;   // owner-thread only

    /// <summary>Queues <paramref name="action"/> for the next <see cref="Pump"/>. Safe from any thread.</summary>
    public void Post(Action action) => _queue.Enqueue(action);

    /// <summary>Number of callbacks waiting for <see cref="Pump"/>.</summary>
    public int Pending => _queue.Count;

    /// <summary>
    /// Runs every queued callback, including any posted while it runs, on the owning thread.
    /// Returns how many ran. Throws when called from any other thread.
    /// </summary>
    public int Pump()
    {
        AssertUIThread();
        if (_pumping) return 0;   // re-entered from a callback: the outer pump drains the rest
        _pumping = true;
        try
        {
            int ran = 0;
            while (_queue.TryDequeue(out var action))
            {
                action();
                ran++;
            }
            return ran;
        }
        finally
        {
            _pumping = false;
        }
    }

    public void AssertUIThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException(
                $"Called from thread {Environment.CurrentManagedThreadId}, not the marshaller's " +
                $"thread {_ownerThreadId}: UI-thread-only Core state was touched off the UI thread.");
    }
}
