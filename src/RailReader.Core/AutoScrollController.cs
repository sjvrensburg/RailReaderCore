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
    // The view this controller drives. One AutoScrollController is built per Viewport (in the Viewport
    // ctor), so it acts on its OWN rail rather than taking a per-call target — passing a foreign view's
    // rail was a footgun, and driving DocumentModel.Rail (the Primary facade) was the multi-viewport bug.
    private readonly Viewport _owner;

    public AutoScrollController(CoreSettings config, Viewport owner)
    {
        _config = config;
        _owner = owner;
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

    // All operate on the owning view's own rail (_owner.Rail).

    /// <summary>
    /// Toggles auto-scroll on/off for the owning view. Requires it to be in rail mode to activate.
    /// </summary>
    public void ToggleAutoScroll()
    {
        if (AutoScrollActive)
        {
            StopAutoScroll();
            return;
        }
        if (!_owner.Rail.Active) return;

        _owner.Rail.StartAutoScroll(_config.DefaultAutoScrollSpeed);
        AutoScrollActive = true;
    }

    /// <summary>
    /// Stops auto-scroll on the owning view's rail and notifies the UI.
    /// </summary>
    public void StopAutoScroll()
    {
        _owner.Rail.StopAutoScroll();
        AutoScrollActive = false;
    }

    /// <summary>
    /// Toggles auto-scroll for the owning view, disabling jump mode first if active.
    /// </summary>
    public void ToggleAutoScrollExclusive()
    {
        if (JumpMode) JumpMode = false;
        ToggleAutoScroll();
    }

    /// <summary>
    /// Toggles jump mode, stopping the owning view's auto-scroll first if active.
    /// </summary>
    public void ToggleJumpModeExclusive()
    {
        if (AutoScrollActive) StopAutoScroll();
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
