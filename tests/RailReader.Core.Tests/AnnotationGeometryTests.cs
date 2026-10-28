using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AnnotationGeometryTests
{
    // ── GetAnnotationBounds ──────────────────────────────────────────

    [Fact]
    public void GetAnnotationBounds_Highlight_ReturnsBoundingRect()
    {
        var highlight = new HighlightAnnotation
        {
            Rects =
            [
                new HighlightRect(10, 20, 100, 30),  // (10,20) -> (110,50)
                new HighlightRect(50, 60, 80, 25),   // (50,60) -> (130,85)
            ],
        };

        var bounds = AnnotationGeometry.GetAnnotationBounds(highlight);

        Assert.NotNull(bounds);
        var r = bounds.Value;
        Assert.Equal(10f, r.Left, 3);
        Assert.Equal(20f, r.Top, 3);
        Assert.Equal(130f, r.Right, 3);
        Assert.Equal(85f, r.Bottom, 3);
    }

    [Fact]
    public void GetAnnotationBounds_Freehand_ReturnsBoundingRect()
    {
        var freehand = new FreehandAnnotation
        {
            Points =
            [
                new PointF(5, 15),
                new PointF(50, 80),
                new PointF(30, 10),
            ],
        };

        var bounds = AnnotationGeometry.GetAnnotationBounds(freehand);

        Assert.NotNull(bounds);
        var r = bounds.Value;
        Assert.Equal(5f, r.Left, 3);
        Assert.Equal(10f, r.Top, 3);
        Assert.Equal(50f, r.Right, 3);
        Assert.Equal(80f, r.Bottom, 3);
    }

    [Fact]
    public void GetAnnotationBounds_TextNote_ReturnsCenteredRect()
    {
        var note = new TextNoteAnnotation { X = 100, Y = 200, Text = "hello" };

        var bounds = AnnotationGeometry.GetAnnotationBounds(note);

        Assert.NotNull(bounds);
        var r = bounds.Value;
        float half = AnnotationGeometry.NoteIconSize / 2f; // 8
        Assert.Equal(100f - half, r.Left, 3);
        Assert.Equal(200f - half, r.Top, 3);
        Assert.Equal(100f + half, r.Right, 3);
        Assert.Equal(200f + half, r.Bottom, 3);
        Assert.Equal(16f, r.Width, 3);
        Assert.Equal(16f, r.Height, 3);
    }

    [Fact]
    public void GetAnnotationBounds_Rect_ReturnsExactBounds()
    {
        var rect = new RectAnnotation { X = 10, Y = 20, W = 50, H = 30 };

        var bounds = AnnotationGeometry.GetAnnotationBounds(rect);

        Assert.NotNull(bounds);
        var r = bounds.Value;
        Assert.Equal(10f, r.Left, 3);
        Assert.Equal(20f, r.Top, 3);
        Assert.Equal(60f, r.Right, 3);   // X + W = 10 + 50
        Assert.Equal(50f, r.Bottom, 3);  // Y + H = 20 + 30
    }

    [Fact]
    public void GetAnnotationBounds_EmptyHighlight_ReturnsNull()
    {
        var highlight = new HighlightAnnotation { Rects = [] };

        var bounds = AnnotationGeometry.GetAnnotationBounds(highlight);

        Assert.Null(bounds);
    }

    // ── ComputeNewBounds ─────────────────────────────────────────────

    [Fact]
    public void ComputeNewBounds_TopLeft_ShrinksRect()
    {
        // Original rect: (10, 20, 60, 50)
        var old = new RectF(10, 20, 60, 50);
        // Drag TopLeft handle from (10,20) by (+10,+10) => start at (10,20), current at (20,30)
        var result = AnnotationGeometry.ComputeNewBounds(old, ResizeHandle.TopLeft,
            px: 20, py: 30, startX: 10, startY: 20);

        Assert.Equal(20f, result.Left, 3);
        Assert.Equal(30f, result.Top, 3);
        Assert.Equal(60f, result.Right, 3);
        Assert.Equal(50f, result.Bottom, 3);
    }

    [Fact]
    public void ComputeNewBounds_BottomRight_ExpandsRect()
    {
        var old = new RectF(10, 20, 60, 50);
        // Drag BottomRight handle from (60,50) by (+10,+10) => start at (60,50), current at (70,60)
        var result = AnnotationGeometry.ComputeNewBounds(old, ResizeHandle.BottomRight,
            px: 70, py: 60, startX: 60, startY: 50);

        Assert.Equal(10f, result.Left, 3);
        Assert.Equal(20f, result.Top, 3);
        Assert.Equal(70f, result.Right, 3);
        Assert.Equal(60f, result.Bottom, 3);
    }

    [Fact]
    public void ComputeNewBounds_InvertedDrag_Normalizes()
    {
        // Original rect: (10, 20, 60, 50)
        var old = new RectF(10, 20, 60, 50);
        // Drag TopLeft handle past BottomRight: start at (10,20), current at (80,70)
        // dx = 70, dy = 50 => new Left = 10+70 = 80, new Top = 20+50 = 70
        // Before normalization: (80, 70, 60, 50) — inverted both axes
        var result = AnnotationGeometry.ComputeNewBounds(old, ResizeHandle.TopLeft,
            px: 80, py: 70, startX: 10, startY: 20);

        // Normalization ensures Left < Right, Top < Bottom
        Assert.True(result.Left < result.Right, "Left should be less than Right after normalization");
        Assert.True(result.Top < result.Bottom, "Top should be less than Bottom after normalization");
        Assert.Equal(60f, result.Left, 3);
        Assert.Equal(50f, result.Top, 3);
        Assert.Equal(80f, result.Right, 3);
        Assert.Equal(70f, result.Bottom, 3);
    }

    // ── HitTestResizeHandle ──────────────────────────────────────────

    [Fact]
    public void HitTestResizeHandle_NearCorner_ReturnsHandle()
    {
        var freehand = new FreehandAnnotation
        {
            Points =
            [
                new PointF(10, 20),
                new PointF(100, 80),
            ],
        };

        // Bounds = (10, 20, 100, 80). Selection rect inflated by 3 => (7, 17, 103, 83).
        // TopLeft handle is at (7, 17). Test a point very close to it.
        var handle = AnnotationGeometry.HitTestResizeHandle(freehand, 7, 17, tolerance: 8f);

        Assert.Equal(ResizeHandle.TopLeft, handle);
    }

    [Fact]
    public void HitTestResizeHandle_FarAway_ReturnsNone()
    {
        var freehand = new FreehandAnnotation
        {
            Points =
            [
                new PointF(10, 20),
                new PointF(100, 80),
            ],
        };

        var handle = AnnotationGeometry.HitTestResizeHandle(freehand, 500, 500, tolerance: 8f);

        Assert.Equal(ResizeHandle.None, handle);
    }

    // ── HitTest ──────────────────────────────────────────────────────

    [Fact]
    public void HitTest_InsideFilledRect_ReturnsTrue()
    {
        var rect = new RectAnnotation { X = 10, Y = 20, W = 50, H = 30, Filled = true };

        // Centre of bounds: (35, 35) — well inside
        bool hit = AnnotationGeometry.HitTest(rect, 35, 35, tolerance: 4f);

        Assert.True(hit);
    }

    [Fact]
    public void HitTest_UnfilledRect_HitsOutlineNotInterior()
    {
        var rect = new RectAnnotation { X = 10, Y = 20, W = 50, H = 30, StrokeWidth = 2f };

        Assert.False(AnnotationGeometry.HitTest(rect, 35, 35, tolerance: 4f)); // hollow centre
        Assert.True(AnnotationGeometry.HitTest(rect, 10, 35, tolerance: 4f));  // on the left edge
        Assert.True(AnnotationGeometry.HitTest(rect, 35, 54, tolerance: 4f));  // just below the bottom edge
        Assert.False(AnnotationGeometry.HitTest(rect, 35, 57, tolerance: 4f)); // beyond stroke + tolerance
    }

    [Fact]
    public void HitTest_SmallUnfilledRect_IsAllOutline()
    {
        // Narrower than twice the reach: no hollow left, so the whole box hits.
        var rect = new RectAnnotation { X = 10, Y = 10, W = 6, H = 6, StrokeWidth = 2f };

        Assert.True(AnnotationGeometry.HitTest(rect, 13, 13, tolerance: 4f));
    }

    [Fact]
    public void HitTest_WrappedHighlight_MissesTheGapBetweenItsLines()
    {
        // A highlight from the end of one line to the start of the next: its union bounds cover
        // both whole rows, but only the two line rects are drawn.
        var highlight = new HighlightAnnotation
        {
            Rects =
            [
                new HighlightRect(400, 100, 100, 12), // last words of line 1
                new HighlightRect(50, 114, 80, 12),   // first words of line 2
            ],
        };

        Assert.True(AnnotationGeometry.HitTest(highlight, 450, 106, tolerance: 4f));
        Assert.True(AnnotationGeometry.HitTest(highlight, 90, 120, tolerance: 4f));
        Assert.False(AnnotationGeometry.HitTest(highlight, 200, 106, tolerance: 4f)); // line 1, before the highlight
        Assert.False(AnnotationGeometry.HitTest(highlight, 300, 120, tolerance: 4f)); // line 2, after it
    }

    [Fact]
    public void HitTest_Freehand_HitsNearTheStrokeOnly()
    {
        // An L-shaped stroke: its bounds include the empty corner opposite the bend.
        var freehand = new FreehandAnnotation
        {
            StrokeWidth = 2f,
            Points = [new PointF(0, 0), new PointF(0, 100), new PointF(100, 100)],
        };

        Assert.True(AnnotationGeometry.HitTest(freehand, 3, 50, tolerance: 4f));   // beside the vertical leg
        Assert.True(AnnotationGeometry.HitTest(freehand, 50, 104, tolerance: 4f)); // below the horizontal leg
        Assert.False(AnnotationGeometry.HitTest(freehand, 80, 20, tolerance: 4f)); // inside bounds, off the stroke
        Assert.False(AnnotationGeometry.HitTest(freehand, 50, 94, tolerance: 4f)); // 6 > 1 + 4 from the leg
    }

    [Fact]
    public void DistanceToPolyline_SinglePointAndSegment()
    {
        Assert.Equal(5f, AnnotationGeometry.DistanceToPolyline([new PointF(0, 0)], 3, 4), 3);
        Assert.Equal(2f, AnnotationGeometry.DistanceToPolyline([new PointF(0, 0), new PointF(10, 0)], 5, 2), 3);
        // Beyond the segment's end: distance to the endpoint, not to the infinite line.
        Assert.Equal(5f, AnnotationGeometry.DistanceToPolyline([new PointF(0, 0), new PointF(10, 0)], 13, 4), 3);
    }

    [Fact]
    public void HitTest_OutsideBounds_ReturnsFalse()
    {
        var rect = new RectAnnotation { X = 10, Y = 20, W = 50, H = 30 };

        // Bounds are (10, 20, 60, 50). Inflated by 4 => (6, 16, 64, 54).
        // Point (200, 200) is far outside.
        bool hit = AnnotationGeometry.HitTest(rect, 200, 200, tolerance: 4f);

        Assert.False(hit);
    }

    // ── Bounds Helpers ───────────────────────────────────────────────

    [Fact]
    public void ComputeRectsBounds_MultipleRects()
    {
        var rects = new List<HighlightRect>
        {
            new(0, 0, 50, 20),      // (0,0) -> (50,20)
            new(60, 30, 40, 10),     // (60,30) -> (100,40)
        };

        var bounds = AnnotationGeometry.ComputeRectsBounds(rects);

        Assert.Equal(0f, bounds.Left, 3);
        Assert.Equal(0f, bounds.Top, 3);
        Assert.Equal(100f, bounds.Right, 3);
        Assert.Equal(40f, bounds.Bottom, 3);
    }

    [Fact]
    public void ComputePointsBounds_MultiplePoints()
    {
        var points = new List<PointF>
        {
            new(5, 100),
            new(75, 10),
            new(40, 55),
        };

        var bounds = AnnotationGeometry.ComputePointsBounds(points);

        Assert.Equal(5f, bounds.Left, 3);
        Assert.Equal(10f, bounds.Top, 3);
        Assert.Equal(75f, bounds.Right, 3);
        Assert.Equal(100f, bounds.Bottom, 3);
    }
}
