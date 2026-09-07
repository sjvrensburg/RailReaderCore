namespace RailReader.Core.Models;

public class RecentFileEntry
{
    public string FilePath { get; set; } = "";
    public int Page { get; set; }
    public double Zoom { get; set; } = 1.0;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public ColourEffect? ColourEffect { get; set; }

    /// <summary>
    /// True once <see cref="Services.IRecentFilesStore.SaveReadingPosition"/> has actually written
    /// this entry's <see cref="Zoom"/>/<see cref="OffsetX"/>/<see cref="OffsetY"/>. An entry can also
    /// exist from <see cref="Services.IRecentFilesStore.AddRecentFile"/> alone (opened, but the app
    /// closed/crashed before <c>CloseDocument</c> ever ran) — its Zoom/OffsetX/OffsetY are just the
    /// type's field defaults (1.0/0/0), not a real reading position, and restoring them on reopen
    /// would silently override <c>DocumentModel.CenterPage</c>'s fit with an arbitrary top-left/1.0×
    /// camera. Old JSON predating this field deserialises it to <c>false</c> (its default), so an
    /// upgrade loses camera restore for previously-saved positions exactly once, not silently wrongly.
    /// </summary>
    public bool HasSavedCamera { get; set; }
}
