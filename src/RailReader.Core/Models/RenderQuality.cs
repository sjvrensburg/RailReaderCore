namespace RailReader.Core.Models;

/// <summary>
/// Render-fidelity presets. Each preset maps (via
/// <see cref="RenderDpiSettings.ForPreset"/>) to a maximum rasterisation DPI
/// and a tier step — the DPI quantum the renderer snaps to as the user zooms.
/// Higher fidelity is sharper at high zoom but produces larger bitmaps and
/// re-rasters more often; lower fidelity favours fluidity on weaker hardware.
/// <see cref="Custom"/> takes user-supplied max-DPI / tier-step values.
/// </summary>
/// <remarks>
/// Persisted as an integer in the consumer's config (mirroring
/// <see cref="ColourEffect"/>), so the order of these members is part of the
/// on-disk contract — append new presets, never reorder existing ones.
/// </remarks>
public enum RenderQuality
{
    /// <summary>800 DPI cap, 50 tier step. Maximum fidelity for high-res output / deep zoom.</summary>
    Ultra,

    /// <summary>600 DPI cap, 75 tier step. Default high-fidelity baseline.</summary>
    Quality,

    /// <summary>525 DPI cap, 85 tier step. Sharp, with slightly reduced re-rastering frequency.</summary>
    High,

    /// <summary>450 DPI cap, 100 tier step. Balanced compromise for general use.</summary>
    Balanced,

    /// <summary>400 DPI cap, 125 tier step. Reduced load for mid-range hardware.</summary>
    Medium,

    /// <summary>350 DPI cap, 150 tier step. Fewer re-rasters; prioritises fluidity.</summary>
    Performance,

    /// <summary>User-defined max-DPI and tier-step values.</summary>
    Custom,
}
