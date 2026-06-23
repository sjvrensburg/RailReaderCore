using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Manages auto-scroll and jump mode state.
/// Extracted from DocumentController for testability and separation of concerns.
/// </summary>
internal sealed class AutoScrollController
{
    private CoreSettings _config;

    public AutoScrollController(CoreSettings config)
    {
        _config = config;
    }

    public void UpdateConfig(CoreSettings config) => _config = config;

    /// <summary>Whether auto-scroll is currently active.</summary>
    public bool AutoScrollActive
    {
        get => _autoScrollActive;
        private set
        {
            if (_autoScrollActive == value) return;
            _autoScrollActive = value;
            StateChanged?.Invoke(nameof(AutoScrollActive));
        }
    }
    private bool _autoScrollActive;

    /// <summary>Whether jump mode is currently active.</summary>
    public bool JumpMode
    {
        get => _jumpMode;
        set
        {
            if (_jumpMode == value) return;
            _jumpMode = value;
            StateChanged?.Invoke(nameof(JumpMode));
        }
    }
    private bool _jumpMode;

    /// <summary>
    /// Fired when a property changes. UI can subscribe to update bindings.
    /// </summary>
    public Action<string>? StateChanged;

    // These operate on a specific Viewport's own rail. Each AutoScrollController instance belongs to
    // one Viewport, so they take the owning view rather than the document — driving DocumentModel.Rail
    // (the Primary facade) would start/stop/gate the WRONG rail when the focused view is a
    // secondary/detached pane.

    /// <summary>
    /// Toggles auto-scroll on/off for <paramref name="vp"/>. Requires that view to be in rail mode
    /// to activate.
    /// </summary>
    public void ToggleAutoScroll(Viewport vp)
    {
        if (AutoScrollActive)
        {
            StopAutoScroll(vp);
            return;
        }
        if (!vp.Rail.Active) return;

        vp.Rail.StartAutoScroll(_config.DefaultAutoScrollSpeed);
        AutoScrollActive = true;
    }

    /// <summary>
    /// Stops auto-scroll on <paramref name="vp"/>'s own rail and notifies the UI.
    /// </summary>
    public void StopAutoScroll(Viewport vp)
    {
        vp.Rail.StopAutoScroll();
        AutoScrollActive = false;
    }

    /// <summary>
    /// Toggles auto-scroll for <paramref name="vp"/>, disabling jump mode first if active.
    /// </summary>
    public void ToggleAutoScrollExclusive(Viewport vp)
    {
        if (JumpMode) JumpMode = false;
        ToggleAutoScroll(vp);
    }

    /// <summary>
    /// Toggles jump mode, stopping <paramref name="vp"/>'s auto-scroll first if active.
    /// </summary>
    public void ToggleJumpModeExclusive(Viewport vp)
    {
        if (AutoScrollActive) StopAutoScroll(vp);
        JumpMode = !JumpMode;
    }

    /// <summary>
    /// Activates auto-scroll directly (used by TickRailSnap when auto-scroll trigger fires).
    /// </summary>
    public void ActivateAutoScroll() => AutoScrollActive = true;

    /// <summary>
    /// The configured auto-scroll speed.
    /// </summary>
    public double AutoScrollSpeed => _config.DefaultAutoScrollSpeed;
}
