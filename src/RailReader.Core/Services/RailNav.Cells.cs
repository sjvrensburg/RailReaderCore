using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed partial class RailNav
{
    /// <summary>
    /// Index of the active cell within the current line's <see cref="LineInfo.Cells"/>.
    /// Reset to 0 whenever <see cref="CurrentLine"/> is assigned (a new row always starts at
    /// its leftmost cell). Only meaningful when the current line carries cells — i.e. a table
    /// row produced with cell navigation enabled. Reads are clamped defensively, so a stale
    /// value left over from a wider row is harmless.
    /// </summary>
    public int CurrentCell { get; set; }

    /// <summary>
    /// Cells of the current line, or <c>null</c> when the line has no cell structure
    /// (any non-table line, or a table read without cell navigation). Returns <c>null</c>
    /// when rail mode has no navigable analysis.
    /// </summary>
    public IReadOnlyList<CellInfo>? CurrentCells => CanNavigate ? CurrentLineInfo.Cells : null;

    /// <summary>True when the current line exposes navigable cells.</summary>
    public bool HasCells => CurrentCells is { Count: > 0 };

    /// <summary>
    /// The active cell, clamped into range, or <c>null</c> when the current line has no
    /// cells. Used by the snap path to centre the cell and by consumers to frame it.
    /// </summary>
    public CellInfo? CurrentCellInfo
    {
        get
        {
            if (CurrentCells is not { Count: > 0 } cells) return null;
            return cells[Math.Clamp(CurrentCell, 0, cells.Count - 1)];
        }
    }

    /// <summary>
    /// Steps to the next cell in the current row. At the row's last cell it advances to the
    /// next line — which seats cell 0 — and propagates that move's result, so reaching the
    /// end of the page surfaces as <see cref="NavResult.PageBoundaryNext"/>. Returns
    /// <see cref="NavResult.NotApplicable"/> when the current line has no cells, letting the
    /// consumer fall back to its horizontal pan/jump path. Mirrors <see cref="NextLine"/>'s
    /// convention of returning <see cref="NavResult.Ok"/> when rail can't navigate.
    /// </summary>
    public NavResult NextCell()
    {
        if (!CanNavigate) return NavResult.Ok;
        if (CurrentCells is not { Count: > 0 } cells) return NavResult.NotApplicable;

        if (CurrentCell + 1 < cells.Count)
        {
            CurrentCell++;
            return NavResult.Ok;
        }
        return NextLine();
    }

    /// <summary>
    /// Steps to the previous cell in the current row. At cell 0 it moves to the previous line
    /// and seats on that row's LAST cell (when the new row has cells), propagating the line
    /// move's result — reaching the start of the page surfaces as
    /// <see cref="NavResult.PageBoundaryPrev"/>. Returns <see cref="NavResult.NotApplicable"/>
    /// when the current line has no cells.
    /// </summary>
    public NavResult PrevCell()
    {
        if (!CanNavigate) return NavResult.Ok;
        if (CurrentCells is not { Count: > 0 }) return NavResult.NotApplicable;

        if (CurrentCell > 0)
        {
            CurrentCell--;
            return NavResult.Ok;
        }

        var result = PrevLine();
        // PrevLine zeroed CurrentCell; seat on the last cell of the row we landed on
        // (only if it has cells — moving up into prose leaves the cursor at cell 0).
        if (result == NavResult.Ok && CurrentCells is { Count: > 0 } prevRowCells)
            CurrentCell = prevRowCells.Count - 1;
        return result;
    }
}
