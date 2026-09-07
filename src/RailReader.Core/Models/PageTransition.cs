namespace RailReader.Core.Models;

/// <summary>
/// How a page change assigns the destination page's camera offset. See
/// docs/continuous-scroll-plan.md §1.2/§4.
/// </summary>
public enum PageTransition
{
    /// <summary>
    /// Single-page mode: today's behaviour (keep the old offset, clamp to the new page).
    /// Continuous mode: the page top lands at the viewport top (an explicit jump).
    /// </summary>
    Default,

    /// <summary>
    /// Re-anchors the camera onto the new page without moving the screen (the §1.2 renaming).
    /// Used by scroll re-anchoring and by rail's page-advance transitions. Single-page mode
    /// treats this the same as <see cref="Default"/> (there is only one page's offset).
    /// </summary>
    PreserveScreen,
}
