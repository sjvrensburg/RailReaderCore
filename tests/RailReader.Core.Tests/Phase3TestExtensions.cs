using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader.Core.Tests;

/// <summary>
/// Phase 3 removed two pure-convenience facades from <see cref="DocumentController"/>.
/// These test-only extension methods reproduce them so the many mechanical call sites
/// stay unchanged. New behaviour (per-viewport sizing/ticking) is exercised directly elsewhere.
/// </summary>
internal static class Phase3TestExtensions
{
    // Phase 3 removed the ambient viewport size; size every open document's primary view (matches
    // the old single-window SetViewportSize, which tests call after AddDocument).
    public static void SetViewportSize(this DocumentController c, double w, double h)
    {
        foreach (var doc in c.Documents) doc.Primary.SetSize(w, h);
    }

    // Phase 3 removed the controller-level Tick facade; tick the focused viewport (pumps analysis).
    public static TickResult Tick(this DocumentController c, double dt)
        => c.FocusedViewport is { } vp ? c.TickViewport(vp, dt) : default;
}
