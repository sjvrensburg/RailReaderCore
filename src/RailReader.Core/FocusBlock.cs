using RailReader.Core.Models;

namespace RailReader.Core;

/// <summary>
/// Confines a <see cref="Viewport"/> to a single layout block — a "portal" view that keeps one
/// figure/table/equation in view. While set on a viewport:
/// <list type="bullet">
///   <item>the camera can pan and zoom only within <see cref="Bounds"/> (it cannot zoom out past the
///   whole block, nor pan to reveal neighbouring page content) — enforced by
///   <see cref="Viewport.ClampCamera"/>;</item>
///   <item>rail navigation is restricted to this one block: no block-to-block advance, no semantic
///   role jumps off it (line stepping within the block still works) — enforced by
///   <c>RailNav.SetAnalysis</c> collapsing the navigable set to this block.</item>
/// </list>
/// Set by a host that wants a viewport pinned to a block; assign <c>null</c> to release the viewport
/// back to normal whole-page navigation. Both clamps gate on <see cref="Page"/> matching the view's
/// current page, so a confinement authored for page N is inert while the viewport sits elsewhere.
/// </summary>
/// <param name="Page">The page (0-based) the block lives on.</param>
/// <param name="BlockIndex">Index into that page's <c>PageAnalysis.Blocks</c> — the block rail is
/// confined to. Ignored by rail confinement if out of range for the seated analysis (stale focus).</param>
/// <param name="Bounds">The block's page-space rectangle (PDF points) the camera is clamped to.</param>
public sealed record FocusBlock(int Page, int BlockIndex, BBox Bounds);
