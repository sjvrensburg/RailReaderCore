namespace RailReader.Core.Models;

public enum NavResult
{
    Ok,
    PageBoundaryNext,
    PageBoundaryPrev,

    /// <summary>
    /// The requested move does not apply in the current context — e.g. a cell step
    /// (<see cref="RailReader.Core.Services.RailNav.NextCell"/>/<c>PrevCell</c>) on a line
    /// that has no cells. The consumer should fall back to its default handling (pan/jump).
    /// </summary>
    NotApplicable
}

public enum ScrollDirection
{
    Forward,
    Backward
}
