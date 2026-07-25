using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Detects text lines inside layout blocks.
///
/// Three strategies are applied in order of preference:
///   1. <b>Atomic classes</b> — figure and chart blocks collapse to a single
///      line spanning the full block. Such purely visual blocks should advance
///      in rail mode as one unit, not be fragmented row-by-row. Tables are
///      <i>not</i> atomic when <c>tableRowReading</c> is set (the default): a
///      table's rows are detected like any other text so the reader can step
///      through them line-by-line (e.g. financial statements).
///   2. <b>Char-box clustering</b> — when PDFium per-character bounding boxes are
///      available, cluster them by vertical position. Robust to subscripts,
///      superscripts, and inline math; gives true baselines rather than the
///      smoothed peaks of pixel projection.
///   3. <b>Pixel projection</b> — fall back to row-density analysis of the
///      rasterized page. Used for scanned PDFs and any block where char
///      clustering produced nothing.
/// </summary>
public static class LineDetector
{
    /// <summary>
    /// Block roles unconditionally treated as a single atomic line in rail mode.
    /// Only purely visual blocks belong here — they have no meaningful per-line
    /// structure and should advance as one unit. <see cref="BlockRole.Table"/> is
    /// deliberately <i>not</i> in this set: it is atomic only when table-row
    /// reading is disabled (see the <c>tableRowReading</c> parameter of
    /// <see cref="DetectLines"/>). Math roles (<see cref="BlockRole.DisplayMath"/>,
    /// <see cref="BlockRole.InlineMath"/>, <see cref="BlockRole.Algorithm"/>)
    /// deliberately stay line-detectable because stepwise derivations and
    /// algorithm pseudocode read line-by-line; char-box clustering handles
    /// those without fragmenting sub/superscripts.
    /// </summary>
    internal static readonly HashSet<BlockRole> AtomicLineRoles =
    [
        BlockRole.Figure,
        BlockRole.Chart,
    ];

    /// <summary>
    /// Returns line runs (in block-relative pixel coordinates) for a block,
    /// using char clustering when available and pixel projection as fallback.
    /// </summary>
    /// <summary>
    /// Cluster split threshold as a multiple of median char height. Math blocks
    /// use a more generous multiple so a single display equation's stacked parts
    /// (fraction bars, sub/superscripts, matrix rows) cluster into one line
    /// rather than fragmenting — while genuinely separated derivation rows, whose
    /// inter-row gaps are larger, still split. Tuned against the Surya line
    /// oracle (see tools/line-eval): char-clustering was over-segmenting
    /// DisplayMath ~3-4×.
    /// </summary>
    internal const float DefaultSplitMultiplier = 1.0f;
    internal const float MathSplitMultiplier = 1.3f;

    /// <summary>
    /// A char taller than this multiple of the block's median char height is
    /// treated as an oversize glyph — typically a drop cap — and lifted out of
    /// line clustering so it can't dominate a line's vertical band. Normal
    /// glyph-height variation (caps and ascenders/descenders vs x-height) stays
    /// well under this; a drop cap spanning 2–3 text lines is several times the
    /// median, far above it. See <see cref="DetectLinesFromChars"/>.
    /// </summary>
    internal const float OversizeGlyphFactor = 1.8f;

    /// <summary>
    /// A horizontal gap between successive glyphs wider than this multiple of the median
    /// glyph height opens a new table cell. ~1× the font size cleanly separates the
    /// whitespace-aligned columns of a financial statement without splitting the ordinary
    /// inter-word spaces inside a single cell (mirrors liteparse's borderless-table rule).
    /// </summary>
    internal const float CellGapMultiplier = 1.0f;

    /// <summary>
    /// A pixel column whose longest continuous dark run exceeds this fraction of the table crop's
    /// height is treated as a vertical ruling line (column separator) in <see cref="DetectColumnGrid"/>.
    /// Rules are continuous top-to-bottom; text/number columns are dark only where glyphs sit, so
    /// their longest run stays well under this. 0.5 is comfortably below a rule's near-full coverage
    /// yet above any text column's.
    /// </summary>
    internal const float RuleRunFraction = 0.5f;

    /// <summary>
    /// A table row consisting of one run at least this fraction of the block's width is a
    /// spanning heading rather than a data row, and is excluded from unruled column-band
    /// construction in <see cref="DetectColumnBands"/> — it straddles every column, so
    /// pooling it would merge them all into one band.
    /// </summary>
    internal const float BandSpannerWidthFraction = 0.8f;

    /// <summary>
    /// Fraction of an OCR-detected line's width that must fall inside a block before the line
    /// is treated as that block's. Text lines are detected page-wide with no knowledge of the
    /// layout blocks, so in a two-column page a line from the left column shares its Y band
    /// with the right column's block; requiring most of the line's width to be inside keeps
    /// each line with its own block. Loose enough to tolerate a block box that clips a
    /// hanging indent or a trailing glyph.
    /// </summary>
    internal const float OcrLineBlockOverlapFraction = 0.6f;

    /// <summary>
    /// Distance in points within which two vector rulings are the same rule. Producers draw
    /// hairlines as thin filled rectangles as often as stroked lines, and a filled rectangle
    /// contributes both of its long edges — a fraction of a point apart. Also folds a rule
    /// sitting a hair inside the block's edge into that edge.
    /// </summary>
    internal const float RulingMergeTolerance = 1.5f;

    /// <summary>
    /// Fraction of a table block's width a horizontal rule must cross before it counts as a
    /// row separator in <see cref="MergeRowsByRulings"/>. A rule underlining a single header
    /// cell, or a neighbouring block's rule clipped into this one, spans far less and must not
    /// be allowed to cut a row.
    /// </summary>
    internal const float RowRuleSpanFraction = 0.5f;

    /// <summary>
    /// Most text lines a band between two horizontal rules may hold before
    /// <see cref="MergeRowsByRulings"/> declines to fuse them into one row. A wrapped table cell
    /// runs to two lines, occasionally three; beyond that the rules are separating sections of
    /// the table, and fusing would collapse every data row in the section into a single rail row.
    /// </summary>
    internal const int MaxLinesPerRuledRow = 3;

    private static readonly HashSet<BlockRole> MathRoles =
        [BlockRole.DisplayMath, BlockRole.InlineMath, BlockRole.Algorithm];

    /// <param name="tableRowReading">
    /// When true (the default), a <see cref="BlockRole.Table"/> block is split into
    /// per-row lines via char clustering instead of collapsing to one atomic line,
    /// so rail mode can step through table rows. When false the table stays atomic.
    /// </param>
    /// <param name="cellNavigation">
    /// When true (and the block is a table read row-by-row), each detected row's
    /// <see cref="LineInfo.Cells"/> is populated by splitting the row's glyphs into cells
    /// at horizontal whitespace gaps, so rail mode can step the row cell-by-cell. Requires
    /// a text layer (char boxes); the pixel-projection fallback produces no cells. Has no
    /// effect on non-table blocks or when <paramref name="tableRowReading"/> is false.
    /// </param>
    /// <param name="ocrLines">
    /// Text-line boxes recovered by OCR for a page with no text layer, in page-point space.
    /// Used only when char clustering produced nothing — they are real detected lines, so
    /// they take precedence over the pixel-projection fallback. Null when the page has a
    /// text layer or OCR is off.
    /// </param>
    /// <param name="rulings">
    /// The page's vector ruling lines, when the backend can read them. Used for exact table
    /// column grids in preference to the raster scan; null falls back to the raster path.
    /// </param>
    public static List<LineInfo> DetectLines(
        LayoutBlock block,
        IReadOnlyList<CharBox>? charBoxes,
        byte[] rgbBytes, int imgW, int imgH, float scaleX, float scaleY,
        bool tableRowReading = true,
        bool cellNavigation = false,
        IReadOnlyList<BBox>? ocrLines = null,
        PageRulings? rulings = null)
    {
        bool isTable = block.Role == BlockRole.Table;
        // Rotated-text blocks collapse to one atomic line: mid-Y char clustering
        // (and the row-density fallback) assume horizontal lines and would shatter
        // a sideways block into per-glyph fragments. Rail then frames the whole
        // block; per-line reading of rotated text goes through rotate-to-read.
        if (AtomicLineRoles.Contains(block.Role) || block.IsRotatedText || (isTable && !tableRowReading))
            return [new LineInfo(block.BBox.Y + block.BBox.H / 2f, block.BBox.H, block.BBox.X, block.BBox.W)];

        // Table rows come from the same char clustering as prose, but skip the
        // vertical-overlap merge in NormalizeLines: a table's rows are already
        // cleanly separated by the greedy split, and the merge can fuse
        // tightly-spaced rows (common in dense financial statements).
        bool mergeOverlaps = !isTable;
        bool detectCells = isTable && cellNavigation;

        if (charBoxes is { Count: > 0 })
        {
            bool isMath = MathRoles.Contains(block.Role);
            float mult = isMath ? MathSplitMultiplier : DefaultSplitMultiplier;
            // Lift oversize spanning glyphs (drop caps) out of clustering for prose,
            // but not for math — there a tall bracket legitimately spans, and should
            // merge, stacked rows (matrices, large fractions).
            var charLines = DetectLinesFromChars(block.BBox, charBoxes, mult, excludeOversizeSpanners: !isMath);
            if (charLines.Count > 0)
            {
                var rows = NormalizeLines(charLines, block.BBox, mergeOverlaps);
                // For a ruled table the drawn row separators are the authority on where one
                // row ends: a cell whose text wraps is one row, not two.
                if (isTable) rows = MergeRowsByRulings(rows, rulings, block.BBox);
                // Cells are a pure overlay on the validated row geometry — row
                // Y/Height/X/Width are untouched, so row reading is unregressed.
                return detectCells
                    ? AssignCells(rows, charBoxes, block.BBox, rgbBytes, imgW, imgH, scaleX, scaleY, rulings)
                    : rows;
            }
        }

        if (ocrLines is { Count: > 0 })
        {
            var detected = LinesFromOcr(block.BBox, ocrLines);
            if (detected.Count > 0) return NormalizeLines(detected, block.BBox, mergeOverlaps);
        }

        return NormalizeLines(DetectLinesFromPixels(block, rgbBytes, imgW, imgH, scaleX, scaleY), block.BBox, mergeOverlaps);
    }

    /// <summary>
    /// Selects the OCR-detected lines belonging to <paramref name="block"/> and clips them to it.
    ///
    /// <para>
    /// A line belongs to the block when its vertical centre lies inside the block and most of
    /// its width does too. Centre-in-band assigns each line to exactly one of two vertically
    /// adjacent blocks; the width test then keeps a neighbouring column's line — which shares
    /// the same Y band in multi-column layouts — out of this block.
    /// </para>
    /// </summary>
    internal static List<LineInfo> LinesFromOcr(BBox block, IReadOnlyList<BBox> ocrLines)
    {
        float top = block.Y, bottom = block.Y + block.H;
        float left = block.X, right = block.X + block.W;

        var lines = new List<LineInfo>();
        foreach (var l in ocrLines)
        {
            if (l.H <= 0 || l.W <= 0) continue;

            float centreY = l.Y + l.H / 2f;
            if (centreY < top || centreY > bottom) continue;

            float lx = Math.Max(left, l.X);
            float lr = Math.Min(right, l.X + l.W);
            float overlap = lr - lx;
            if (overlap <= 0 || overlap < l.W * OcrLineBlockOverlapFraction) continue;

            lines.Add(new LineInfo(centreY, l.H, lx, overlap));
        }
        return lines;
    }

    /// <summary>
    /// Enforces the invariants every line consumer (rail stepping, snap, line
    /// focus/highlight, and chunk concatenation) silently assumes: positive
    /// height, geometry clamped inside the block, sorted top-to-bottom, and
    /// (when <paramref name="mergeOverlaps"/> is set) no two lines overlapping by
    /// more than half the smaller. Idempotent.
    /// </summary>
    /// <param name="mergeOverlaps">
    /// Merge lines whose vertical bands overlap by &gt; 50% of the smaller. On by
    /// default. Disabled for table rows, which the greedy split already separates
    /// cleanly and whose tight spacing the merge would otherwise fuse.
    /// </param>
    internal static List<LineInfo> NormalizeLines(List<LineInfo> lines, BBox block, bool mergeOverlaps = true)
    {
        float top = block.Y, bottom = block.Y + block.H;
        float left = block.X, right = block.X + block.W;

        var clamped = new List<LineInfo>(lines.Count);
        foreach (var l in lines)
        {
            float h = l.Height;
            if (h <= 0) continue;
            // Clamp the [top,bottom] band into the block, recompute centre/height.
            float lt = Math.Max(top, l.Y - h / 2f);
            float lb = Math.Min(bottom, l.Y + h / 2f);
            if (lb - lt <= 0) continue;
            float lx = Math.Max(left, l.X);
            float lr = Math.Min(right, l.X + l.Width);
            float lw;
            if (lr - lx > 0) { lw = lr - lx; }
            else { lx = left; lw = block.W; }   // extent doesn't overlap → span the block, anchored at its left
            if (lw <= 0) continue;              // degenerate block width
            clamped.Add(new LineInfo((lt + lb) / 2f, lb - lt, lx, lw));
        }

        clamped.Sort((a, b) => a.Y.CompareTo(b.Y));

        if (!mergeOverlaps) return clamped;

        // Merge lines whose vertical bands overlap by > 50% of the smaller.
        var merged = new List<LineInfo>(clamped.Count);
        foreach (var l in clamped)
        {
            if (merged.Count > 0)
            {
                var p = merged[^1];
                float pt = p.Y - p.Height / 2f, pb = p.Y + p.Height / 2f;
                float lt = l.Y - l.Height / 2f, lb = l.Y + l.Height / 2f;
                float ov = Math.Min(pb, lb) - Math.Max(pt, lt);
                if (ov > 0.5f * Math.Min(p.Height, l.Height))
                {
                    float nt = Math.Min(pt, lt), nb = Math.Max(pb, lb);
                    float nx = Math.Min(p.X, l.X), nr = Math.Max(p.X + p.Width, l.X + l.Width);
                    merged[^1] = new LineInfo((nt + nb) / 2f, nb - nt, nx, nr - nx);
                    continue;
                }
            }
            merged.Add(l);
        }
        return merged;
    }

    // One glyph's horizontal extent, kept while splitting a table row into cells.
    // Vertical extent is irrelevant once a glyph is bucketed to a row.
    internal readonly record struct GlyphRef(float Left, float Right);

    /// <summary>
    /// Overlays per-row cell geometry onto already-detected table rows. Each in-block,
    /// non-degenerate glyph is bucketed to the nearest row by centre-Y; the row's glyphs are
    /// split into cells wherever the horizontal gap between successive glyphs exceeds
    /// <see cref="CellGapMultiplier"/>× the median glyph height. Rows are updated in place
    /// (the row list is freshly produced by <see cref="NormalizeLines"/> and owned by the
    /// caller) with <see cref="LineInfo.Cells"/> populated — a row that gathered no glyphs
    /// keeps <c>Cells == null</c>. Pure overlay: the row Y/Height/X/Width are never modified,
    /// so table-row reading is unaffected by whether cells are computed.
    /// </summary>
    internal static List<LineInfo> AssignCells(
        List<LineInfo> rows, IReadOnlyList<CharBox> charBoxes, BBox block,
        byte[] rgbBytes, int imgW, int imgH, float scaleX, float scaleY,
        PageRulings? rulings = null)
    {
        if (rows.Count == 0) return rows;

        float left = block.X, right = block.X + block.W;
        float top = block.Y, bottom = block.Y + block.H;

        // Bucket in-block glyphs to the nearest row (by line centre) and collect heights
        // for the gap threshold. Nearest-row is consistent with the mid-Y clustering that
        // produced the rows, and assigns every glyph exactly once.
        var rowGlyphs = new List<GlyphRef>?[rows.Count];
        var heights = new List<float>();
        foreach (var c in charBoxes)
        {
            float h = c.Bottom - c.Top;
            if (h <= 0) continue; // whitespace / degenerate box
            float midX = (c.Left + c.Right) * 0.5f;
            float midY = (c.Top + c.Bottom) * 0.5f;
            if (midX < left || midX > right || midY < top || midY > bottom) continue;

            int best = 0;
            float bestDist = float.PositiveInfinity;
            for (int r = 0; r < rows.Count; r++)
            {
                float d = Math.Abs(rows[r].Y - midY);
                if (d < bestDist) { bestDist = d; best = r; }
            }
            (rowGlyphs[best] ??= []).Add(new GlyphRef(c.Left, c.Right));
            heights.Add(h);
        }

        if (heights.Count == 0) return rows;
        heights.Sort();

        // Phase 2 (issue #67): prefer the table's own ruled column grid when present. Dense
        // statistical tables (space-grouped thousands, dash placeholders, right-aligned numerics,
        // missing cells, hierarchical headers) defeat any glyph-gap heuristic, but they are drawn
        // with vertical rules — recovering that grid gives a fixed, aligned column count with empty
        // cells for blanks (stable column index across rows). Unruled tables fall back to the gap
        // split below, so this is purely additive.
        float gapThreshold = RobustGapThreshold(heights);

        // Prefer the page's own vector rules when the backend can read them: they are the
        // exact lines the producer drew, where the raster scan can only infer them from dark
        // pixel runs at whatever resolution the analysis pixmap happens to be.
        var grid = DetectColumnGridFromRulings(rulings, block);
        grid ??= DetectColumnGrid(rgbBytes, imgW, imgH, block, scaleX, scaleY);

        // A "grid" is only believable if the table's own content sits in it. A figure's border,
        // a code block's background or a tall bracket can read as a rule — producing columns no
        // row actually populates. Tabula guards its lattice extraction the same way, by checking
        // the ruled structure against the structure the text alone implies.
        if (grid is not null && !GridMatchesGlyphs(grid, rowGlyphs, gapThreshold))
            grid = null;

        if (grid is not null)
        {
            var gridCells = BuildGridCells(grid); // identical bands for every row → aligned columns
            for (int r = 0; r < rows.Count; r++)
                if (rowGlyphs[r] is { Count: > 0 })
                    rows[r] = rows[r] with { Cells = gridCells };
            return rows;
        }

        // Phase 3: unruled tables get their column grid recovered from glyph geometry
        // across *all* rows rather than each row splitting itself. Same payoff as the
        // ruled path above — a stable column index and empty cells for blanks.
        var bands = DetectColumnBands(rowGlyphs, block, gapThreshold);
        if (bands is not null)
        {
            var bandCells = BuildGridCells(bands);
            for (int r = 0; r < rows.Count; r++)
                if (rowGlyphs[r] is { Count: > 0 })
                    rows[r] = rows[r] with { Cells = bandCells };
            return rows;
        }

        for (int r = 0; r < rows.Count; r++)
            if (rowGlyphs[r] is { Count: > 0 } glyphs)
                rows[r] = rows[r] with { Cells = SplitRowCells(glyphs, gapThreshold) };
        return rows;
    }

    /// <summary>
    /// Checks a candidate column grid against the table's glyphs, so a grid recovered from
    /// something that is not a column separator is rejected before it reaches the reader.
    ///
    /// <para>
    /// Three conditions, all about correspondence between rules and content: most rows that have
    /// content must spread it across more than one column (otherwise the "columns" cut nothing),
    /// the rows that support the boundaries must outnumber the rows that contradict them, and
    /// some row must populate a fair share of the columns (otherwise the grid claims far more
    /// structure than the text supports — the signature of stray vertical strokes). A grid that
    /// fails any is discarded and the caller falls through to glyph-derived bands and then to
    /// the per-row split.
    /// </para>
    /// <para>
    /// Contradiction is counted, not fatal. A single row that sits entirely in one column while
    /// carrying a gap the per-row split would divide — a total line, a footnote, a spanning
    /// note — says nothing about the twenty rows above it that straddle the rules correctly, and
    /// rejecting the whole grid for it costs exactly the aligned blank-cell column index the
    /// ruled path exists to provide.
    /// </para>
    /// </summary>
    internal static bool GridMatchesGlyphs(
        List<float> bounds, IReadOnlyList<List<GlyphRef>?> rowGlyphs, float gapThreshold)
    {
        int columns = bounds.Count - 1;
        if (columns < 2) return false;

        int rowsWithGlyphs = 0, rowsSpanningColumns = 0, rowsContradicting = 0, maxOccupied = 0;

        foreach (var glyphs in rowGlyphs)
        {
            if (glyphs is not { Count: > 0 }) continue;
            rowsWithGlyphs++;

            // Which columns this row actually puts content in (by glyph centre).
            var occupied = new bool[columns];
            foreach (var g in glyphs)
            {
                float centre = (g.Left + g.Right) * 0.5f;
                for (int c = 0; c < columns; c++)
                {
                    if (centre >= bounds[c] && centre <= bounds[c + 1]) { occupied[c] = true; break; }
                }
            }

            int count = 0;
            foreach (bool o in occupied) if (o) count++;
            if (count > maxOccupied) maxOccupied = count;

            // A row whose content is one run inside a single column tells us nothing about
            // whether the boundaries are real; a row that straddles boundaries does.
            if (count > 1) rowsSpanningColumns++;
            else if (count == 1 && SplitRowCells(glyphs, gapThreshold).Count > 1)
            {
                // Content the glyph-gap split would have divided, all landing in one column,
                // is evidence *against* the grid: for this row the boundaries are in the wrong
                // places. Weighed against the rows that straddle them, not decisive on its own.
                rowsContradicting++;
            }
        }

        if (rowsWithGlyphs == 0) return false;
        return rowsSpanningColumns * 2 >= rowsWithGlyphs
            && rowsSpanningColumns > rowsContradicting
            && maxOccupied * 2 >= columns;
    }

    /// <summary>
    /// Recovers a shared column grid for an <b>unruled</b> table by pooling every row's
    /// glyph runs and merging the ones that occupy the same horizontal band.
    ///
    /// <para>
    /// Splitting each row independently (<see cref="SplitRowCells"/>) is correct for that
    /// row but not across rows: a row with a blank cell, a merged cell, or a short entry
    /// yields a different cell count and different spans than its neighbours, so column
    /// <c>k</c> means something different on every row and cell navigation drifts sideways
    /// as it steps down. Pooling the runs recovers the column structure the table is
    /// actually laid out on — the same thing <see cref="DetectColumnGrid"/> reads off the
    /// ruling lines when they exist — and every row then gets the identical band list, so
    /// blanks become empty navigable cells at the right index.
    /// </para>
    /// <para>
    /// Rows whose single run spans most of the block (section headings and titles set
    /// inside the table body) are excluded from band construction: they overlap every
    /// column at once and would collapse the whole table into one band. They still receive
    /// the resulting bands like any other row.
    /// </para>
    /// <para>
    /// Returns sorted column boundaries in the same shape as <see cref="DetectColumnGrid"/>,
    /// or <c>null</c> when the evidence for a columnar layout is too weak (fewer than three
    /// bands, or too few rows showing more than one run) — the caller then falls back to
    /// the per-row split. Adapted from tabula-java's stream-mode column detection
    /// (<c>BasicExtractionAlgorithm.ColumnPositions</c>).
    /// </para>
    /// </summary>
    internal static List<float>? DetectColumnBands(
        IReadOnlyList<List<GlyphRef>?> rowGlyphs, BBox block, float gapThreshold)
    {
        if (gapThreshold <= 0) return null;

        float blockLeft = block.X, blockRight = block.X + block.W;
        if (blockRight - blockLeft <= 0) return null;

        float spannerWidth = block.W * BandSpannerWidthFraction;

        // Pool every contributing row's runs. Note SplitRowCells sorts each row's glyph
        // list by left edge in place; the caller's fallback re-splits the same (now
        // sorted) lists, which is why this can run ahead of it without disturbing it.
        var runs = new List<CellInfo>();
        int contributingRows = 0, multiCellRows = 0;
        foreach (var glyphs in rowGlyphs)
        {
            if (glyphs is not { Count: > 0 }) continue;

            var rowRuns = SplitRowCells(glyphs, gapThreshold);
            // A lone run covering most of the block is a spanning heading, not a column.
            if (rowRuns.Count == 1 && rowRuns[0].Width >= spannerWidth) continue;

            contributingRows++;
            if (rowRuns.Count > 1) multiCellRows++;
            runs.AddRange(rowRuns);
        }

        // If most rows are single-run this is a list or a paragraph block, not a grid.
        if (contributingRows == 0 || multiCellRows * 2 < contributingRows) return null;

        runs.Sort((a, b) => a.X.CompareTo(b.X));

        // Merge runs into bands. Runs closer than one cell gap belong to the same column:
        // overlap alone is too strict, since a narrow entry on one row and a wide one on
        // another can sit side by side without ever overlapping.
        var bandLeft = new List<float>();
        var bandRight = new List<float>();
        float curLeft = runs[0].X, curRight = runs[0].X + runs[0].Width;
        for (int i = 1; i < runs.Count; i++)
        {
            float l = runs[i].X, r = l + runs[i].Width;
            if (l - curRight < gapThreshold)
            {
                if (r > curRight) curRight = r;
            }
            else
            {
                bandLeft.Add(curLeft); bandRight.Add(curRight);
                curLeft = l; curRight = r;
            }
        }
        bandLeft.Add(curLeft); bandRight.Add(curRight);

        // Same confidence bar as the ruled path: ≥3 columns before we claim a grid.
        if (bandLeft.Count < 3) return null;

        // Boundaries: block edges plus the midpoint of each inter-band gutter. Clamped
        // and kept strictly increasing so BuildGridCells can't emit a zero/negative span.
        var bounds = new List<float>(bandLeft.Count + 1) { blockLeft };
        for (int i = 1; i < bandLeft.Count; i++)
        {
            float mid = (bandRight[i - 1] + bandLeft[i]) * 0.5f;
            mid = Math.Clamp(mid, blockLeft, blockRight);
            if (mid > bounds[^1]) bounds.Add(mid);
        }
        if (blockRight > bounds[^1]) bounds.Add(blockRight);

        return bounds.Count >= 4 ? bounds : null;
    }

    /// <summary>
    /// Merges the text lines of a ruled table into its actual rows, using the horizontal rules
    /// drawn between them.
    ///
    /// <para>
    /// Char clustering finds <i>text lines</i>, which is the right answer for prose and the
    /// wrong one for a table whose cells wrap: a two-line cell becomes two rail rows, the
    /// second of which looks like a row with content in only one column, and cell navigation
    /// steps through a phantom. Where the table draws a rule between each row, that ambiguity
    /// is already resolved on the page — lines sharing a band between two rules are one row.
    /// </para>
    /// <para>
    /// Applied band by band, and only where the rules are delimiting rows rather than sections:
    /// a booktabs-style table with a rule above, below and under the header would otherwise
    /// collapse its whole body into one row. The test is that a band holds no more than
    /// <see cref="MaxLinesPerRuledRow"/> lines — a wrapped cell runs to two or three, a section
    /// runs to as many rows as the section has. A band that fails keeps its own text lines;
    /// the rest of the table still merges, so one over-full band no longer forfeits the whole
    /// table (nor does an average over bands let one hide behind the empty ones).
    /// </para>
    /// </summary>
    internal static List<LineInfo> MergeRowsByRulings(List<LineInfo> rows, PageRulings? rulings, BBox block)
    {
        if (rulings is null || rulings.Horizontal.Count == 0 || rows.Count < 2) return rows;
        if (block.W <= 0 || block.H <= 0) return rows;

        float left = block.X, right = block.X + block.W;
        float top = block.Y, bottom = block.Y + block.H;
        float minSpan = block.W * RowRuleSpanFraction;

        // Interior rules that actually cross the table, so a rule underlining one header cell
        // (or a neighbouring block's rule clipped into this one) cannot cut a row.
        var cuts = new List<float>();
        foreach (var rule in rulings.Horizontal)
        {
            float overlap = Math.Min(right, rule.End) - Math.Max(left, rule.Start);
            if (overlap < minSpan) continue;
            if (rule.Position <= top + RulingMergeTolerance || rule.Position >= bottom - RulingMergeTolerance) continue;
            if (cuts.Count == 0 || rule.Position > cuts[^1] + RulingMergeTolerance) cuts.Add(rule.Position);
        }
        if (cuts.Count < 2) return rows;   // fewer than two interior rules is not a row grid
        cuts.Sort();

        int bands = cuts.Count + 1;

        // Bucket each row into the band its centre falls in, then fuse each band's rows.
        var banded = new List<LineInfo>?[bands];
        foreach (var row in rows)
        {
            int band = 0;
            while (band < cuts.Count && row.Y > cuts[band]) band++;
            (banded[band] ??= []).Add(row);
        }

        var merged = new List<LineInfo>(bands);
        foreach (var group in banded)
        {
            if (group is not { Count: > 0 }) continue;
            if (group.Count == 1) { merged.Add(group[0]); continue; }
            if (group.Count > MaxLinesPerRuledRow)
            {
                // Section rules, not row rules — for this band at least. Fusing here would
                // hand rail one "row" covering a whole block of the table.
                merged.AddRange(group);
                continue;
            }

            float rowTop = float.MaxValue, rowBottom = float.MinValue;
            float rowLeft = float.MaxValue, rowRight = float.MinValue;
            IReadOnlyList<CellInfo>? cells = null;
            foreach (var r in group)
            {
                rowTop = Math.Min(rowTop, r.Y - r.Height / 2f);
                rowBottom = Math.Max(rowBottom, r.Y + r.Height / 2f);
                rowLeft = Math.Min(rowLeft, r.X);
                rowRight = Math.Max(rowRight, r.X + r.Width);
                cells ??= r.Cells;
            }
            merged.Add(new LineInfo((rowTop + rowBottom) / 2f, rowBottom - rowTop,
                rowLeft, rowRight - rowLeft, cells));
        }

        return merged.Count > 0 ? merged : rows;
    }

    /// <summary>
    /// Recovers a table's column grid from the page's **vector** ruling lines.
    ///
    /// <para>
    /// This is the exact form of what <see cref="DetectColumnGrid"/> approximates from pixels:
    /// the separators are read from the PDF's own drawing operators, so there is no resolution
    /// floor, no luminance threshold, and no way for a tall glyph or a shaded background to
    /// masquerade as a rule. Available only where the backend can read page paths — see
    /// <see cref="IPdfRulingService"/> — and the caller falls back to the raster scan otherwise.
    /// </para>
    /// <para>
    /// A ruling counts as one of this block's column separators when it lies inside the block
    /// horizontally and spans most of it vertically, which excludes the short rules that
    /// underline a single header cell. Returns boundaries in the same shape as
    /// <see cref="DetectColumnGrid"/>, or null when fewer than two interior separators are
    /// found.
    /// </para>
    /// </summary>
    internal static List<float>? DetectColumnGridFromRulings(PageRulings? rulings, BBox block)
    {
        if (rulings is null || rulings.Vertical.Count == 0 || block.W <= 0 || block.H <= 0)
            return null;

        float top = block.Y, bottom = block.Y + block.H;
        float left = block.X, right = block.X + block.W;
        float minSpan = block.H * RuleRunFraction;
        float edgeEps = RulingMergeTolerance;

        var interior = new List<float>();
        foreach (var rule in rulings.Vertical)
        {
            // Overlap with the block's vertical extent, not the raw rule length: a long rule
            // running past the table still separates this block's columns.
            float overlap = Math.Min(bottom, rule.End) - Math.Max(top, rule.Start);
            if (overlap < minSpan) continue;
            if (rule.Position <= left + edgeEps || rule.Position >= right - edgeEps) continue;
            interior.Add(rule.Position);
        }

        if (interior.Count < 2) return null;
        interior.Sort();

        var bounds = new List<float>(interior.Count + 2) { left };
        foreach (float x in interior)
            if (x > bounds[^1] + edgeEps) bounds.Add(x);
        if (right > bounds[^1] + edgeEps) bounds.Add(right); else bounds[^1] = right;

        return bounds.Count >= 4 ? bounds : null;
    }

    /// <summary>
    /// Recovers a table's column grid from the **vertical ruling lines** drawn on the page. For
    /// each pixel column of the block crop it measures the longest continuous run of dark pixels;
    /// a ruled column separator is dark down (nearly) the whole crop, while text/number columns are
    /// dark only intermittently (gaps between rows), so their longest run is short. Rule columns are
    /// collapsed to boundary centres (point space) and combined with the block's left/right edges.
    /// Returns the sorted column boundaries, or <c>null</c> when fewer than two interior rules are
    /// found (no usable grid → caller falls back to the glyph-gap split). DPI-robust: hairline
    /// (0.3pt) rules survive rasterisation as continuous dark columns even at 800px-longest-edge
    /// analysis resolution (verified on SARB Quarterly Bulletin tables).
    /// </summary>
    internal static List<float>? DetectColumnGrid(
        byte[] rgbBytes, int imgW, int imgH, BBox block, float scaleX, float scaleY)
    {
        if (rgbBytes is null || rgbBytes.Length == 0 || scaleX <= 0 || scaleY <= 0) return null;

        int pxX = Math.Max(0, Math.Min((int)Math.Round(block.X / scaleX), imgW - 1));
        int pxY = Math.Max(0, Math.Min((int)Math.Round(block.Y / scaleY), imgH - 1));
        int pxW = Math.Min((int)Math.Round(block.W / scaleX), imgW - pxX);
        int pxH = Math.Min((int)Math.Round(block.H / scaleY), imgH - pxY);
        if (pxW < 4 || pxH < 8) return null;

        int ruleMinRun = (int)(pxH * RuleRunFraction);
        var ruleColsPx = new List<int>();
        for (int col = 0; col < pxW; col++)
        {
            int x = pxX + col;
            int run = 0, maxRun = 0;
            for (int row = 0; row < pxH; row++)
            {
                int idx = ((pxY + row) * imgW + x) * 3;
                bool dark = idx + 2 < rgbBytes.Length
                    && rgbBytes[idx] * 0.299f + rgbBytes[idx + 1] * 0.587f + rgbBytes[idx + 2] * 0.114f
                       < LayoutConstants.DarkLuminanceThreshold;
                run = dark ? run + 1 : 0;
                if (run > maxRun) maxRun = run;
            }
            if (maxRun >= ruleMinRun) ruleColsPx.Add(col);
        }

        // Collapse adjacent rule columns (a rule rasterises 1–2px wide) into boundary centres.
        var rules = new List<float>();
        for (int i = 0; i < ruleColsPx.Count;)
        {
            int j = i;
            while (j + 1 < ruleColsPx.Count && ruleColsPx[j + 1] - ruleColsPx[j] <= 2) j++;
            float centrePx = pxX + (ruleColsPx[i] + ruleColsPx[j]) * 0.5f;
            rules.Add(centrePx * scaleX);
            i = j + 1;
        }

        // Boundaries = block.left ∪ interior rules ∪ block.right (rules within a hair of an edge
        // are the table's outer border, folded into the edge).
        float lp = block.X, rp = block.X + block.W, edgeEps = 2f * scaleX;
        var bounds = new List<float> { lp };
        foreach (var x in rules)
            if (x > lp + edgeEps && x < rp - edgeEps) bounds.Add(x);
        bounds.Add(rp);

        // Need ≥2 interior rules (≥3 columns) to be confident it is a real ruled grid rather than a
        // stray vertical streak; simpler tables are served fine by the gap split.
        return bounds.Count >= 4 ? bounds : null;
    }

    /// <summary>Cells from grid boundaries: one band per consecutive boundary pair, left to right.
    /// The same list is shared by every row of the table so column <c>k</c> is the same span on
    /// every row (empty bands become empty navigable cells).</summary>
    private static List<CellInfo> BuildGridCells(List<float> bounds)
    {
        var cells = new List<CellInfo>(bounds.Count - 1);
        for (int i = 0; i + 1 < bounds.Count; i++)
            cells.Add(new CellInfo(bounds[i], Math.Max(0f, bounds[i + 1] - bounds[i])));
        return cells;
    }

    /// <summary>
    /// Gap threshold = median glyph height × <see cref="CellGapMultiplier"/>, but robust to
    /// short-glyph-dominated rows. Dash/dot/dot-leader-heavy tables (e.g. "- - - - - 6 665" or
    /// row labels with "............." leaders) otherwise collapse the plain median toward ~1pt —
    /// at which point a number's intra-thousands space exceeds the threshold and "1 288 272"
    /// shatters into 1/288/272 (issue #67). We anchor on a tall reference (90th-percentile glyph
    /// height ≈ a digit/letter, never a dash) and take the median over only glyphs ≥ 40% of it, so
    /// the punctuation cluster can't drag the threshold down. <paramref name="heights"/> must be
    /// sorted ascending. For fully-populated tables (no short cluster) this is the plain median.
    /// </summary>
    internal static float RobustGapThreshold(List<float> heights)
    {
        // Clamp the percentile index: float rounding of Count*0.9f can land on Count for large
        // inputs, and the caller only guarantees Count > 0.
        float tallRef = heights[Math.Min(heights.Count - 1, (int)(heights.Count * 0.9f))];
        float minRealHeight = 0.4f * tallRef;
        int lo = 0;
        while (lo < heights.Count && heights[lo] < minRealHeight) lo++;
        // Median over the "real" (non-punctuation) glyphs; fall back to the plain median if every
        // glyph fell below the floor (a row genuinely made only of short marks).
        int midIdx = lo < heights.Count ? (lo + heights.Count) / 2 : heights.Count / 2;
        return heights[midIdx] * CellGapMultiplier;
    }

    /// <summary>
    /// Splits one row's glyphs (any order) into cells: sort by left edge, then open a new
    /// cell whenever the gap from the current cell's running right edge to the next glyph's
    /// left edge exceeds <paramref name="gapThreshold"/>. Each cell spans min-left…max-right.
    /// </summary>
    private static List<CellInfo> SplitRowCells(List<GlyphRef> glyphs, float gapThreshold)
    {
        glyphs.Sort((a, b) => a.Left.CompareTo(b.Left));

        var cells = new List<CellInfo>();
        float cellLeft = glyphs[0].Left, cellRight = glyphs[0].Right;

        for (int i = 1; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            if (g.Left - cellRight > gapThreshold)
            {
                cells.Add(new CellInfo(cellLeft, Math.Max(0f, cellRight - cellLeft)));
                cellLeft = g.Left; cellRight = g.Right;
            }
            else if (g.Right > cellRight)
            {
                cellRight = g.Right;
            }
        }
        cells.Add(new CellInfo(cellLeft, Math.Max(0f, cellRight - cellLeft)));
        return cells;
    }

    private readonly record struct GlyphBox(float MidY, float Top, float Bottom, float Height, float Left, float Right);

    // Vertical+horizontal extent of one detected line, accumulated before being
    // converted to a LineInfo. A record struct held in a List and mutated only by
    // full replacement (bands[i] = ...), never by in-place field assignment.
    private record struct Band(float Top, float Bottom, float Left, float Right)
    {
        public readonly float CenterY => (Top + Bottom) * 0.5f;
        public readonly bool OverlapsY(in GlyphBox g) => g.Bottom > Top && g.Top < Bottom;
        public readonly LineInfo ToLineInfo() => new(CenterY, Bottom - Top, Left, Right - Left);
    }

    /// <summary>
    /// Clusters character bounding boxes by vertical position into text lines.
    /// Returns lines in page-point space (Y measured from page top).
    /// </summary>
    /// <remarks>
    /// Algorithm: filter chars whose midpoint falls inside the block, sort by
    /// mid-Y, then greedily split clusters when the gap between a char's mid-Y
    /// and the current cluster's running-mean mid-Y exceeds a fraction of the
    /// median char height. Each cluster becomes one LineInfo whose height spans from
    /// the cluster's min-top to max-bottom — this includes ascenders, descenders,
    /// and sub/superscripts within the line.
    ///
    /// <para>
    /// When <paramref name="excludeOversizeSpanners"/> is set, glyphs taller than
    /// <see cref="OversizeGlyphFactor"/>× the median are lifted out before clustering
    /// and re-attached afterwards, so a drop cap (one glyph 2–3 lines tall) cannot
    /// be pulled into a line by its mid-Y and then inflate that line's band to its
    /// full height — which would make the downstream <see cref="NormalizeLines"/>
    /// overlap merge swallow every line the glyph spans. A lifted glyph that spans
    /// ≥2 detected lines is decoration: it widens only the topmost line's horizontal
    /// extent, never any line's vertical band. One that covers a single line (a tall
    /// operator, a raised cap) is folded back in fully so its line still bounds it.
    /// Left off for math, where a tall bracket spanning stacked rows should merge them.
    /// </para>
    /// </remarks>
    internal static List<LineInfo> DetectLinesFromChars(
        BBox bbox, IReadOnlyList<CharBox> charBoxes, float splitMultiplier = DefaultSplitMultiplier,
        bool excludeOversizeSpanners = false)
    {
        float left = bbox.X;
        float right = bbox.X + bbox.W;
        float top = bbox.Y;
        float bottom = bbox.Y + bbox.H;

        var chars = new List<GlyphBox>(charBoxes.Count);
        foreach (var c in charBoxes)
        {
            float h = c.Bottom - c.Top;
            if (h <= 0) continue; // skip whitespace / degenerate boxes

            float midX = (c.Left + c.Right) * 0.5f;
            float midY = (c.Top + c.Bottom) * 0.5f;
            if (midX < left || midX > right) continue;
            if (midY < top || midY > bottom) continue;

            chars.Add(new GlyphBox(midY, c.Top, c.Bottom, h, c.Left, c.Right));
        }

        if (chars.Count == 0) return [];

        var heightsSorted = chars.Select(c => c.Height).OrderBy(h => h).ToArray();
        float refHeight = heightsSorted[heightsSorted.Length / 2];
        if (refHeight <= 0) return [];

        // Lift out oversize spanning glyphs (drop caps) before clustering. Only when
        // some normal-height chars remain to define the real lines — an all-large run
        // (e.g. a heading) has no spanner to lift and clusters as-is.
        List<GlyphBox>? oversize = null;
        if (excludeOversizeSpanners)
        {
            float oversizeThreshold = refHeight * OversizeGlyphFactor;
            // Build the `normal` list lazily — only once the first oversize glyph is
            // seen (back-filling the normal prefix). The common case (no drop cap)
            // then allocates nothing and leaves `chars` untouched.
            List<GlyphBox>? normal = null;
            for (int i = 0; i < chars.Count; i++)
            {
                if (chars[i].Height > oversizeThreshold)
                {
                    (oversize ??= []).Add(chars[i]);
                    normal ??= chars.GetRange(0, i);
                }
                else normal?.Add(chars[i]);
            }
            if (oversize is not null && normal is { Count: > 0 }) chars = normal;
            else oversize = null;
        }

        chars.Sort((a, b) => a.MidY.CompareTo(b.MidY));

        // Greedy clustering. Threshold generous (>= 1.0 × refHeight) so sub/
        // superscripts and inline math don't fragment a single visual line; math
        // blocks pass a larger multiplier to avoid splitting a stacked equation.
        float splitThreshold = refHeight * splitMultiplier;
        var bands = new List<Band>();
        int clusterStart = 0;
        float clusterSumMidY = chars[0].MidY;

        for (int i = 1; i < chars.Count; i++)
        {
            int clusterCount = i - clusterStart;
            float clusterAvgMidY = clusterSumMidY / clusterCount;
            if (chars[i].MidY - clusterAvgMidY > splitThreshold)
            {
                bands.Add(BandOf(chars, clusterStart, i));
                clusterStart = i;
                clusterSumMidY = chars[i].MidY;
            }
            else
            {
                clusterSumMidY += chars[i].MidY;
            }
        }
        bands.Add(BandOf(chars, clusterStart, chars.Count));

        // Re-attach the lifted oversize glyphs. Each glyph's span and target line are
        // decided against a SNAPSHOT of the clustered line bands, so the outcome is
        // independent of glyph order and of mutations made while attaching earlier
        // glyphs (record-struct copy = value semantics). Stand-alone bands appended
        // below are deliberately excluded from the snapshot so they can't absorb a
        // later glyph.
        if (oversize is not null)
        {
            var lineBands = bands.ToArray();
            foreach (var g in oversize)
            {
                // Topmost (min-Top) snapshot line the glyph overlaps, and how many it spans.
                int topBand = -1, span = 0;
                for (int k = 0; k < lineBands.Length; k++)
                    if (lineBands[k].OverlapsY(g))
                    {
                        if (topBand < 0 || lineBands[k].Top < lineBands[topBand].Top) topBand = k;
                        span++;
                    }

                if (span == 0)
                {
                    // Overlaps no detected line → a stand-alone glyph: its own line.
                    bands.Add(new Band(g.Top, g.Bottom, g.Left, g.Right));
                    continue;
                }

                // Always grow the topmost overlapped line's horizontal extent so its
                // highlight reaches the glyph. Grow the vertical band only when the
                // glyph belongs to a SINGLE line (a tall operator / raised cap); a
                // multi-line spanner (drop cap) must not inflate any line's height.
                var b = bands[topBand];
                float nt = span == 1 ? Math.Min(b.Top, g.Top) : b.Top;
                float nb = span == 1 ? Math.Max(b.Bottom, g.Bottom) : b.Bottom;
                bands[topBand] = new Band(nt, nb, Math.Min(b.Left, g.Left), Math.Max(b.Right, g.Right));
            }
            bands.Sort((a, b) => a.CenterY.CompareTo(b.CenterY));
        }

        var lines = new List<LineInfo>(bands.Count);
        foreach (var b in bands)
            lines.Add(b.ToLineInfo());
        return lines;

        static Band BandOf(List<GlyphBox> sorted, int start, int endExclusive)
        {
            float minTop = float.PositiveInfinity, maxBottom = float.NegativeInfinity;
            float minLeft = float.PositiveInfinity, maxRight = float.NegativeInfinity;
            for (int i = start; i < endExclusive; i++)
            {
                if (sorted[i].Top < minTop) minTop = sorted[i].Top;
                if (sorted[i].Bottom > maxBottom) maxBottom = sorted[i].Bottom;
                if (sorted[i].Left < minLeft) minLeft = sorted[i].Left;
                if (sorted[i].Right > maxRight) maxRight = sorted[i].Right;
            }
            return new Band(minTop, maxBottom, minLeft, maxRight);
        }
    }

    /// <summary>
    /// Pixel-projection line detection: crops the block region of the page bitmap,
    /// computes per-row dark-pixel density, and finds runs above an adaptive
    /// threshold. Returns lines in page-point space.
    /// </summary>
    internal static List<LineInfo> DetectLinesFromPixels(
        LayoutBlock block, byte[] rgbBytes, int imgW, int imgH, float scaleX, float scaleY)
    {
        // Clamp both bounds (like DetectColumnGrid): a block with a slightly negative origin
        // must degrade gracefully, not index rgbBytes with a negative pixel offset.
        int pxX = Math.Max(0, Math.Min((int)Math.Round(block.BBox.X / scaleX), imgW - 1));
        int pxY = Math.Max(0, Math.Min((int)Math.Round(block.BBox.Y / scaleY), imgH - 1));
        int pxW = Math.Min((int)Math.Round(block.BBox.W / scaleX), imgW - pxX);
        int pxH = Math.Min((int)Math.Round(block.BBox.H / scaleY), imgH - pxY);

        if (pxW <= 0 || pxH <= 0)
            return [];

        var densities = ComputeRowDensities(rgbBytes, imgW, pxX, pxY, pxW, pxH);
        var runs = FindLineRuns(densities);

        var lines = new List<LineInfo>(runs.Count);
        foreach (var run in runs)
        {
            float centerYPx = run.Start + run.Height / 2.0f;
            lines.Add(new LineInfo(block.BBox.Y + centerYPx * scaleY, run.Height * scaleY,
                block.BBox.X, block.BBox.W));
        }
        return lines;
    }

    internal static float[] ComputeRowDensities(byte[] rgbBytes, int imgW, int cropX, int cropY, int cropW, int cropH)
    {
        var profile = new float[cropH];
        for (int row = 0; row < cropH; row++)
        {
            int darkCount = 0;
            for (int col = 0; col < cropW; col++)
            {
                int pixelIdx = ((cropY + row) * imgW + (cropX + col)) * 3;
                if (pixelIdx + 2 < rgbBytes.Length)
                {
                    float r = rgbBytes[pixelIdx];
                    float g = rgbBytes[pixelIdx + 1];
                    float b = rgbBytes[pixelIdx + 2];
                    float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                    if (lum < LayoutConstants.DarkLuminanceThreshold)
                        darkCount++;
                }
            }
            profile[row] = (float)darkCount / cropW;
        }

        // Smooth with radius-1 moving average
        var smoothed = new float[cropH];
        for (int r = 0; r < cropH; r++)
        {
            int start = Math.Max(r - 1, 0);
            int end = Math.Min(r + 2, cropH);
            float sum = 0;
            for (int k = start; k < end; k++) sum += profile[k];
            smoothed[r] = sum / (end - start);
        }
        return smoothed;
    }

    /// <summary>
    /// Detects line runs using adaptive density thresholding with recovery for short lines.
    /// The primary pass uses a density-fraction threshold (15% of average) which reliably
    /// segments dense text. A second recovery pass scans any uncovered regions at the top
    /// and bottom of the block with a low absolute threshold to catch short lines (e.g. the
    /// last few words of a paragraph) that fall below the density-fraction threshold.
    /// </summary>
    internal static List<(int Start, int Height)> FindLineRuns(float[] densities)
    {
        // Primary pass: density-fraction threshold — works well for dense text
        float nonZeroSum = 0;
        int nonZeroCount = 0;
        foreach (var v in densities)
        {
            if (v > 0.005f) { nonZeroSum += v; nonZeroCount++; }
        }
        float threshold = nonZeroCount == 0
            ? 0.005f
            : Math.Max(nonZeroSum / nonZeroCount * LayoutConstants.DensityThresholdFraction, 0.005f);

        var runs = FindRunsAboveThreshold(densities, threshold);

        if (runs.Count == 0) return runs;

        // Recovery pass: check for uncovered regions at top and bottom of the block.
        // If the first/last detected line is far from the block edge, re-scan that
        // region with a low absolute threshold to catch short lines.
        int medianHeight = runs.Select(r => r.Height).OrderBy(h => h).ElementAt(runs.Count / 2);
        float recoveryThreshold = 0.005f;

        // Top recovery: region before the first detected line
        int firstLineStart = runs[0].Start;
        if (firstLineStart > medianHeight / 2)
        {
            var topRuns = FindRunsAboveThreshold(densities[..firstLineStart], recoveryThreshold);
            runs.InsertRange(0, topRuns);
        }

        // Bottom recovery: region after the last detected line
        var lastRun = runs[^1];
        int lastLineEnd = lastRun.Start + lastRun.Height;
        int remaining = densities.Length - lastLineEnd;
        if (remaining > medianHeight / 2)
        {
            var bottomRuns = FindRunsAboveThreshold(densities[lastLineEnd..], recoveryThreshold);
            runs.AddRange(bottomRuns.Select(r => (r.Start + lastLineEnd, r.Height)));
        }

        return runs;
    }

    private static List<(int Start, int Height)> FindRunsAboveThreshold(float[] densities, float threshold)
    {
        var runs = new List<(int Start, int Height)>();
        int? runStart = null;

        for (int r = 0; r < densities.Length; r++)
        {
            if (densities[r] > threshold)
            {
                runStart ??= r;
            }
            else if (runStart is not null)
            {
                int runH = r - runStart.Value;
                if (runH >= LayoutConstants.MinLineHeightPx)
                    runs.Add((runStart.Value, runH));
                runStart = null;
            }
        }
        if (runStart is not null)
        {
            int runH = densities.Length - runStart.Value;
            if (runH >= LayoutConstants.MinLineHeightPx)
                runs.Add((runStart.Value, runH));
        }
        return runs;
    }
}
