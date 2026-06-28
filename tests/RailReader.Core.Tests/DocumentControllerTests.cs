using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class DocumentControllerTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentController _controller;

    public DocumentControllerTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath();
        var config = new AppConfig();
        _controller = new DocumentController(config.ToCoreSettings(), config, AnnotationService.Default, new SynchronousThreadMarshaller(),
            TestFixtures.CreatePdfFactory());
        // Don't initialize worker (requires ONNX model) — test navigation without analysis
    }

    public void Dispose()
    {
        _controller.Dispose();
    }

    [Fact]
    public void CreateDocument_ReturnsValidState()
    {
        var state = _controller.CreateDocument(_pdfPath);
        Assert.Equal(3, state.PageCount);
        Assert.Equal(0, state.CurrentPage);
        Assert.NotEmpty(state.Title);
        state.Dispose();
    }

    [Fact]
    public void AddDocument_SetsActiveIndex()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);

        Assert.Single(_controller.Documents);
        Assert.Equal(0, _controller.ActiveDocumentIndex);
        Assert.Same(state, _controller.ActiveDocument);
    }

    [Fact]
    public void CloseDocument_RemovesFromList()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);

        _controller.CloseDocument(0);
        Assert.Empty(_controller.Documents);
    }

    [Fact]
    public void ListDocuments_ReturnsCorrectInfo()
    {
        var s1 = _controller.CreateDocument(_pdfPath);
        s1.LoadPageBitmap();
        _controller.AddDocument(s1);

        var list = _controller.ListDocuments();
        Assert.Equal(0, list.ActiveIndex);
        Assert.Single(list.Documents);
        Assert.Equal(3, list.Documents[0].PageCount);
    }

    [Fact]
    public void GetDocumentInfo_ReturnsCorrectState()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);

        var info = _controller.GetDocumentInfo();
        Assert.NotNull(info);
        Assert.Equal(_pdfPath, info.FilePath);
        Assert.Equal(3, info.PageCount);
        Assert.Equal(0, info.CurrentPage);
    }

    [Fact]
    public void FitPage_SetsZoom()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        _controller.FitPage();
        Assert.True(state.Camera.Zoom > 0);
    }

    [Fact]
    public void FitWidth_SetsZoom()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        _controller.FitWidth();
        Assert.True(state.Camera.Zoom > 0);
    }

    [Fact]
    public void MoveDocument_ReordersCorrectly()
    {
        var s1 = _controller.CreateDocument(_pdfPath);
        s1.LoadPageBitmap();
        _controller.AddDocument(s1);

        var s2 = _controller.CreateDocument(_pdfPath);
        s2.LoadPageBitmap();
        _controller.AddDocument(s2);

        _controller.ActiveDocumentIndex = 0;
        _controller.MoveDocument(0, 1);
        Assert.Same(s1, _controller.Documents[1]);
        Assert.Same(s2, _controller.Documents[0]);
    }

    private void SetupRailMode(DocumentModel doc)
    {
        var (ww, wh) = (doc.Primary.Width, doc.Primary.Height);
        TestFixtures.SetupRailMode(doc, _controller.Config, ww, wh);
    }

    private void SetupMultiBlockRailMode(DocumentModel doc, params (BlockRole Role, BBox BBox)[] blocks)
    {
        var (ww, wh) = (doc.Primary.Width, doc.Primary.Height);
        TestFixtures.SetupRailMode(doc, _controller.Config, ww, wh, blocks);
    }

    /// <summary>
    /// Creates, loads, adds, and sets viewport for a standard test document.
    /// Returns the DocumentModel ready for further setup (e.g., <see cref="SetupRailMode"/>).
    /// </summary>
    private DocumentModel CreateAndAddDocument()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        return state;
    }

    // --- New tests ---

    [Fact]
    public void Tick_NoDocument_ReturnsDefault()
    {
        var result = _controller.Tick(0.016);
        Assert.False(result.CameraChanged);
        Assert.False(result.PageChanged);
        Assert.False(result.OverlayChanged);
        Assert.False(result.SearchChanged);
        Assert.False(result.AnnotationsChanged);
        Assert.False(result.StillAnimating);
    }

    [Fact]
    public void GoToPage_UpdatesCurrentPage()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        _controller.GoToPage(1);
        Assert.Equal(1, _controller.ActiveDocument!.CurrentPage);
    }

    [Fact]
    public void ToggleAutoScroll_ActivatesAndDeactivates()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        _controller.ToggleAutoScroll();
        Assert.True(_controller.AutoScrollActive);

        _controller.ToggleAutoScroll();
        Assert.False(_controller.AutoScrollActive);
    }

    [Fact]
    public void IsAnimating_FalseWhenAutoScrollParked()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        _controller.ToggleAutoScroll();
        Assert.True(_controller.AutoScrollActive);
        // Actively scrolling counts as animating.
        Assert.True(_controller.FocusedViewport!.IsAnimating);

        // Park, then tick until the deferred park settles into the indefinite hold
        // (the park engages once any pending snap completes).
        state.Rail.ParkAutoScroll();
        for (int i = 0; i < 60 && !_controller.AutoScrollParked; i++)
            _controller.Tick(0.05);
        Assert.True(_controller.AutoScrollParked);

        // Regression (issue #62): a parked auto-scroll is an idle wait, not motion.
        // IsAnimating must go false so the consumer's render loop can quiesce — it must
        // not stay pinned true via AutoScrollActive while parked.
        Assert.False(_controller.FocusedViewport!.IsAnimating);

        // Resuming flow re-engages animation.
        _controller.ResumeAutoScrollFromPark();
        Assert.True(_controller.AutoScrollActive);
        Assert.False(_controller.AutoScrollParked);
        Assert.True(_controller.FocusedViewport!.IsAnimating);
    }

    [Fact]
    public void ToggleJumpModeExclusive_StopsAutoScroll()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        _controller.ToggleAutoScroll();
        Assert.True(_controller.AutoScrollActive);

        _controller.ToggleJumpModeExclusive();
        Assert.False(_controller.AutoScrollActive);
        Assert.True(_controller.JumpMode);
    }

    [Fact]
    public void CycleColourEffect_Cycles()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);

        var next = _controller.CycleColourEffect();
        Assert.NotEqual(ColourEffect.None, next);
    }

    [Fact]
    public void HandleVerticalNav_AdvancesLine()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        int lineBefore = state.Rail.CurrentLine;
        _controller.HandleArrowDown();
        Assert.Equal(lineBefore + 1, state.Rail.CurrentLine);
    }

    [Fact]
    public void Tick_WithDocument_DoesNotCrash()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        var ex = Record.Exception(() =>
        {
            for (int i = 0; i < 10; i++)
                _controller.Tick(0.016);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void FitPage_UpdatesZoom()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        double zoomBefore = state.Camera.Zoom;
        _controller.FitPage();
        Assert.True(state.Camera.Zoom > 0);
        Assert.NotEqual(zoomBefore, state.Camera.Zoom);
    }

    // --- Pause/Resume Cycle ---

    [Fact]
    public void HandlePan_CapturesRailState()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        int expectedBlock = state.Rail.CurrentBlock;
        int expectedLine = state.Rail.CurrentLine;
        double expectedBias = state.Rail.VerticalBias;
        double expectedZoom = state.Camera.Zoom;

        // Pause: Ctrl+drag
        _controller.HandlePan(10, 10, ctrlHeld: true);
        Assert.True(_controller.RailPaused);

        // Pan some more while paused (should not change rail state)
        _controller.HandlePan(20, 20);

        // Resume
        _controller.ResumeRailFromPause();
        Assert.False(_controller.RailPaused);
        Assert.Equal(expectedBlock, state.Rail.CurrentBlock);
        Assert.Equal(expectedLine, state.Rail.CurrentLine);
        Assert.Equal(expectedBias, state.Rail.VerticalBias);
        Assert.Equal(expectedZoom, state.Camera.Zoom);
    }

    [Fact]
    public void CloseDocument_ClearsRailPause()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        _controller.HandlePan(10, 10, ctrlHeld: true);
        Assert.True(_controller.RailPaused);

        _controller.CloseDocument(0);
        Assert.False(_controller.RailPaused);
    }

    [Fact]
    public void SelectDocument_ClearsRailPause()
    {
        var s1 = _controller.CreateDocument(_pdfPath);
        s1.LoadPageBitmap();
        _controller.AddDocument(s1);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(s1);

        var s2 = _controller.CreateDocument(_pdfPath);
        s2.LoadPageBitmap();
        _controller.AddDocument(s2);

        // Pause on tab 1
        _controller.ActiveDocumentIndex = 0;
        _controller.HandlePan(10, 10, ctrlHeld: true);
        Assert.True(_controller.RailPaused);

        // Switch to tab 2
        _controller.SelectDocument(1);
        Assert.False(_controller.RailPaused);
    }

    [Fact]
    public void ResumeRailFromPause_WhenNotPaused_IsNoOp()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        double zoomBefore = state.Camera.Zoom;
        _controller.ResumeRailFromPause();
        Assert.False(_controller.RailPaused);
        Assert.Equal(zoomBefore, state.Camera.Zoom);
    }

    // --- Phase 3b: Non-Rail Edge-Hold Page Advance ---

    [Fact]
    public void Focus_RailHoldsPageAtBoundary()
    {
        // A block-confined (portal) viewport must not page-advance when rail reaches the block's last
        // line — the boundary is a no-op so the reader can't escape the focus block's page.
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        // Three navigable Text blocks (multi-line) so the UNconfined navigable set is genuinely >1 —
        // otherwise a single-block fixture makes NavigableCount==1 before Focus, and the collapse
        // assertion below would pass even if ReapplyFocus's collapse logic were broken.
        TestFixtures.SetupRailMode(state, _controller.Config, 800, 600,
            (BlockRole.Text, new BBox(72, 72, 468, 150), 4),
            (BlockRole.Text, new BBox(72, 240, 468, 150), 4),
            (BlockRole.Text, new BBox(72, 420, 468, 150), 4));

        var vp = state.Primary;
        Assert.True(state.TryGetAnalysis(0, out var analysis) && analysis.Blocks.Count == 3);
        Assert.Equal(3, vp.Rail.NavigableCount);   // unconfined: all three Text blocks navigable
        // Assigning Focus on this already-seated page collapses the rail to the focus block immediately
        // (ReapplyFocus) — so this genuinely exercises the 3→1 rail-set collapse, not just the boundary guard.
        vp.Focus = new FocusBlock(0, 0, analysis.Blocks[0].BBox);
        Assert.Equal(1, vp.Rail.NavigableCount);

        // Drive the rail well past the end of the page; without the guard this would page-advance.
        for (int i = 0; i < 500; i++)
            _controller.HandleArrowDown();

        Assert.Equal(0, state.CurrentPage);     // never paged off the focus block's page
        Assert.Equal(0, vp.Rail.CurrentBlock);  // and never stepped out of the (only) confined block
    }

    // NOTE: the auto-scroll-stop fix (TickAutoScroll's `case ConfinedHold -> StopAutoScroll`) isn't
    // unit-tested end-to-end — driving semi-auto-scroll to a block boundary in the harness needs more
    // state plumbing than is worth it. The boundary→ConfinedHold return it depends on IS covered by
    // Focus_RailHoldsPageAtBoundary (AdvanceLine via HandleArrowDown), and the handler is a direct
    // mirror of the adjacent PageChangedRailLost StopAutoScroll case.

    [Fact]
    public void Focus_ForAnotherPage_DoesNotBlockPaging()
    {
        // The boundary guard is gated on CurrentFocusBlockIndex (Focus on the CURRENT page), not a bare
        // Focus != null — so a stale focus authored for a different page (where no confinement is in
        // effect) must NOT trap paging. This distinguishes the page-matched guard from the weak null-only
        // one (which Focus_RailHoldsPageAtBoundary cannot, since there Focus.Page == CurrentPage).
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        var vp = state.Primary;
        // Focus targets a different page than the one displayed (page 0) → CurrentFocusBlockIndex == null.
        vp.Focus = new FocusBlock(99, 0, new BBox(0, 0, 10, 10));
        // Directly assert the confinement predicate is inert off-page (distinguishes the page-matched
        // guard from a bare `Focus != null`, which Focus_RailHoldsPageAtBoundary cannot) and that the
        // rail-set was NOT collapsed.
        Assert.Null(vp.CurrentFocusBlockIndex);
        Assert.True(vp.Rail.NavigableCount >= 1);

        for (int i = 0; i < 500; i++)
            _controller.HandleArrowDown();

        // Not confined on this page, so the rail boundary is free to page/park exactly as without focus.
        Assert.True(state.CurrentPage > 0 || _controller.RailPaused);
    }

    [Fact]
    public void HandleArrowDown_AtPageEdge_AdvancesAfterHold()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        // Pan camera to the top edge so it can't pan further up
        state.Camera.OffsetY = 0;
        state.ClampCamera(800, 600);

        // Pan as far up as possible to reach the edge
        for (int i = 0; i < 20; i++)
            _controller.HandleArrowUp();
        _controller.ClearPageEdgeHold();

        // Now hold ArrowDown to reach bottom edge, then edge-hold advance
        // First, pan down many times to reach the bottom edge
        for (int i = 0; i < 100; i++)
            _controller.HandleArrowDown();

        // At this point the camera should be at the bottom edge.
        // Hold for edge-advance threshold (400ms)
        Thread.Sleep(450);
        _controller.HandleArrowDown();

        Assert.True(state.CurrentPage > 0 || _controller.RailPaused);
    }

    [Fact]
    public void ClearPageEdgeHold_WhenNotHolding_IsNoOp()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        // Clear should be a no-op when not edge-holding
        _controller.ClearPageEdgeHold();
        Assert.Equal(0, state.CurrentPage);
    }

    // --- Phase 3c: Navigation History ---

    [Fact]
    public void NavigateToBookmark_PushesToBackStack()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        // Add a bookmark pointing to page 2
        state.Annotations.Bookmarks.Add(new BookmarkEntry { Name = "Test", Page = 2 });
        Assert.Equal(0, state.CurrentPage);

        _controller.NavigateToBookmark(0);
        Assert.Equal(2, state.CurrentPage);
        Assert.Equal(1, state.BackStackCount);
        Assert.Equal(0, state.PeekBack());
    }

    [Fact]
    public void NavigateBack_RestoresPage()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        // Set up history via bookmark navigation
        state.Annotations.Bookmarks.Add(new BookmarkEntry { Name = "Page2", Page = 2 });
        _controller.NavigateToBookmark(0);
        Assert.Equal(2, state.CurrentPage);

        _controller.NavigateBack();
        Assert.Equal(0, state.CurrentPage);
        Assert.Equal(0, state.BackStackCount);
        Assert.Equal(1, state.ForwardStackCount);
        Assert.Equal(2, state.PeekForward());
    }

    [Fact]
    public void NavigateForward_AfterBack()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        state.Annotations.Bookmarks.Add(new BookmarkEntry { Name = "Page2", Page = 2 });
        _controller.NavigateToBookmark(0);
        _controller.NavigateBack();
        Assert.Equal(0, state.CurrentPage);

        _controller.NavigateForward();
        Assert.Equal(2, state.CurrentPage);
    }

    [Fact]
    public void NavigateBack_EmptyStack_DoesNothing()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        int pageBefore = state.CurrentPage;
        _controller.NavigateBack();
        Assert.Equal(pageBefore, state.CurrentPage);
    }

    // --- Phase 3d: Click Handling ---

    [Fact]
    public void HandleClick_LinkNavigatesAndPushesHistory()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        // Inject a link at page-point (100, 100) → page 2
        state.SetLinks(0,
        [
            new PdfLink
            {
                Rect = new RectF(50, 50, 200, 200),
                Destination = new PageDestination { PageIndex = 2 }
            }
        ]);

        // Convert page coords to canvas coords
        double canvasX = 100 * state.Camera.Zoom + state.Camera.OffsetX;
        double canvasY = 100 * state.Camera.Zoom + state.Camera.OffsetY;

        var (handled, dest) = _controller.HandleClick(canvasX, canvasY);
        Assert.True(handled);
        Assert.NotNull(dest);
        Assert.IsType<PageDestination>(dest);
        Assert.Equal(2, state.CurrentPage);
        Assert.Equal(1, state.BackStackCount);
        Assert.Equal(0, state.PeekBack());
    }

    [Fact]
    public void HandleClick_BlockSelectionInRailMode()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        // Click on a point within the rail block area
        // TestFixtures.SetupRailMode creates a block at the first navigable index
        var block = state.Rail.CurrentNavigableBlock;
        double pageX = block.BBox.X + block.BBox.W / 2;
        double pageY = block.BBox.Y + block.BBox.H / 2;
        double canvasX = pageX * state.Camera.Zoom + state.Camera.OffsetX;
        double canvasY = pageY * state.Camera.Zoom + state.Camera.OffsetY;

        var (handled, _) = _controller.HandleClick(canvasX, canvasY);
        Assert.True(handled);
    }

    [Fact]
    public void HandleClick_EmptyArea_NotHandled()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        // No rail mode, no links — click should not be handled

        var (handled, _) = _controller.HandleClick(500, 500);
        Assert.False(handled);
    }

    // --- Agent API: GetReadingPosition ---

    [Fact]
    public void GetReadingPosition_NoDocument_ReturnsNull()
    {
        Assert.Null(_controller.GetReadingPosition());
    }

    [Fact]
    public void GetReadingPosition_NoRailMode_ReturnsNull()
    {
        CreateAndAddDocument();

        // Zoom is below rail threshold — no rail mode
        Assert.Null(_controller.GetReadingPosition());
    }

    [Fact]
    public void GetReadingPosition_WithRailMode_ReturnsPosition()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        var pos = _controller.GetReadingPosition();
        Assert.NotNull(pos);
        Assert.Equal(0, pos.Page);
        Assert.Equal(0, pos.BlockIndex);
        Assert.Equal(0, pos.LineIndex);
        Assert.Equal(BlockRole.Text, pos.Role);
        Assert.NotEqual(default, pos.BlockBBox);
    }

    [Fact]
    public void GetReadingPosition_AfterArrowDown_AdvancesLine()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        Assert.Equal(0, _controller.GetReadingPosition()!.LineIndex);

        _controller.HandleArrowDown();
        Assert.Equal(1, _controller.GetReadingPosition()!.LineIndex);
    }

    // --- Agent API: GetPageDescription ---

    [Fact]
    public void GetPageDescription_NoDocument_ReturnsNull()
    {
        Assert.Null(_controller.GetPageDescription());
    }

    [Fact]
    public void GetPageDescription_NoAnalysis_ReturnsNull()
    {
        CreateAndAddDocument();

        // No analysis injected — should return null
        Assert.Null(_controller.GetPageDescription());
    }

    [Fact]
    public void GetPageDescription_WithAnalysis_ReturnsBlocks()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        var desc = _controller.GetPageDescription();
        Assert.NotNull(desc);
        Assert.Equal(0, desc.Page);
        Assert.Equal(1, desc.TotalBlocks);
        Assert.Single(desc.Blocks);
        Assert.Equal(BlockRole.Text, desc.Blocks[0].Role);
        Assert.Equal(0, desc.Blocks[0].ReadingOrder);
    }

    // --- Agent API: NavigateToRole ---

    [Fact]
    public void NavigateToRole_NoDocument_ReturnsFalse()
    {
        Assert.False(_controller.NavigateToRole(BlockRole.Text));
    }

    [Fact]
    public void NavigateToRole_NoRailMode_ReturnsFalse()
    {
        CreateAndAddDocument();

        Assert.False(_controller.NavigateToRole(BlockRole.Text));
    }

    [Fact]
    public void NavigateToRole_TargetNotFound_ReturnsFalse()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        // Only block is Text; searching for Heading should fail
        Assert.False(_controller.NavigateToRole(BlockRole.Heading));
    }

    [Fact]
    public void NavigateToRole_TargetFound_NavigatesAndSnaps()
    {
        var state = CreateAndAddDocument();
        SetupMultiBlockRailMode(state,
            (BlockRole.Text, new BBox(72, 72, 468, 200)),
            (BlockRole.Heading, new BBox(72, 300, 468, 100)),
            (BlockRole.Text, new BBox(72, 420, 468, 200)));

        // Start at first block (Text, index 0)
        Assert.Equal(BlockRole.Text, _controller.GetReadingPosition()!.Role);

        // Navigate to next Heading
        bool found = _controller.NavigateToRole(BlockRole.Heading);
        Assert.True(found);

        var pos = _controller.GetReadingPosition();
        Assert.NotNull(pos);
        Assert.Equal(BlockRole.Heading, pos.Role);
    }

    [Fact]
    public void NavigateToRole_Backward_NavigatesBackward()
    {
        var state = CreateAndAddDocument();
        SetupMultiBlockRailMode(state,
            (BlockRole.Text, new BBox(72, 72, 468, 200)),
            (BlockRole.Heading, new BBox(72, 300, 468, 100)),
            (BlockRole.Text, new BBox(72, 420, 468, 200)));

        // Navigate forward to Heading
        _controller.NavigateToRole(BlockRole.Heading);
        Assert.Equal(BlockRole.Heading, _controller.GetReadingPosition()!.Role);

        // Navigate backward to Text — must land on Text(0), not Text(2)
        bool found = _controller.NavigateToRole(BlockRole.Text, forward: false);
        Assert.True(found);
        var pos = _controller.GetReadingPosition();
        Assert.NotNull(pos);
        Assert.Equal(BlockRole.Text, pos.Role);
        // Heading is at Order=1, so backward from Heading lands on the Text block at Order=0
        Assert.Equal(0, pos.BlockIndex);
    }

    // --- Agent API: Events ---

    [Fact]
    public void PageChanged_FiredOnGoToPage()
    {
        CreateAndAddDocument();

        int? receivedPage = null;
        _controller.FocusedViewport!.PageChanged = page => receivedPage = page;

        _controller.GoToPage(1);
        Assert.Equal(1, receivedPage);
    }

    [Fact]
    public void ReadingPositionChanged_FiredOnArrowDown()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        ReadingPosition? received = null;
        _controller.FocusedViewport!.ReadingPositionChanged = pos => received = pos;

        _controller.HandleArrowDown();

        Assert.NotNull(received);
        Assert.Equal(1, received.LineIndex);
        Assert.Equal(BlockRole.Text, received.Role);
    }

    [Fact]
    public void ReadingPositionChanged_FiredOnClick()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        var block = state.Rail.CurrentNavigableBlock;
        double pageX = block.BBox.X + block.BBox.W / 2;
        double pageY = block.BBox.Y + block.BBox.H / 2;
        double canvasX = pageX * state.Camera.Zoom + state.Camera.OffsetX;
        double canvasY = pageY * state.Camera.Zoom + state.Camera.OffsetY;

        ReadingPosition? received = null;
        _controller.FocusedViewport!.ReadingPositionChanged = pos => received = pos;

        _controller.HandleClick(canvasX, canvasY);

        Assert.NotNull(received);
        Assert.Equal(BlockRole.Text, received.Role);
    }

    [Fact]
    public void AnalysisPageReady_CanBeSubscribed()
    {
        int? receivedPage = null;
        _controller.AnalysisPageReady = page => receivedPage = page;

        // Worker is null in tests, so PollAnalysisResults is a no-op
        _controller.PollAnalysisResults();
        Assert.Null(receivedPage);
    }

    // --- Agent API: text content verification ---

    [Fact]
    public void GetReadingPosition_WithTextCache_ReturnsNonEmptyText()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        // Inject text into the cache so GetReadingPosition can extract it
        var block = state.Rail.CurrentNavigableBlock;
        var bbox = block.BBox;
        // Create CharBoxes that fall within the block
        var chars = "Hello world".Select((c, i) => new CharBox(i, bbox.X + i * 10, bbox.Y + 5, bbox.X + i * 10 + 10, bbox.Y + 20)).ToList();
        state.SetText(0, new PageText("Hello world and more text here", chars));

        var pos = _controller.GetReadingPosition();
        Assert.NotNull(pos);
        Assert.NotEmpty(pos.BlockText);
    }

    // --- Agent API: NavigateToRole boundary cases ---

    [Fact]
    public void NavigateToRole_ForwardFromLastBlock_ReturnsFalse()
    {
        var state = CreateAndAddDocument();
        SetupMultiBlockRailMode(state,
            (BlockRole.Text, new BBox(72, 72, 468, 200)),
            (BlockRole.Heading, new BBox(72, 300, 468, 100)),
            (BlockRole.Text, new BBox(72, 420, 468, 200)));

        // Navigate forward to the last Text block (index 2)
        _controller.NavigateToRole(BlockRole.Heading);
        _controller.NavigateToRole(BlockRole.Text); // lands on Text(2)
        Assert.Equal(2, _controller.GetReadingPosition()!.BlockIndex);

        // Forward from last Text — no more Text blocks ahead
        Assert.False(_controller.NavigateToRole(BlockRole.Text, forward: true));
    }

    [Fact]
    public void NavigateToRole_BackwardFromFirstBlock_ReturnsFalse()
    {
        var state = CreateAndAddDocument();
        SetupMultiBlockRailMode(state,
            (BlockRole.Text, new BBox(72, 72, 468, 200)),
            (BlockRole.Heading, new BBox(72, 300, 468, 100)));

        // Start at first block (Text, index 0)
        Assert.Equal(0, _controller.GetReadingPosition()!.BlockIndex);

        // Backward from first block — no Text blocks behind
        Assert.False(_controller.NavigateToRole(BlockRole.Text, forward: false));
    }

    [Fact]
    public void NavigateToRole_NonNavigableRole_ReturnsFalse()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        // Figure is not in default NavigableRoles
        Assert.False(_controller.NavigateToRole(BlockRole.Figure));
    }

    // --- Agent API: event negative tests ---

    [Fact]
    public void ReadingPositionChanged_NotFiredWithoutRailMode()
    {
        CreateAndAddDocument();
        // No rail mode — zoom is below threshold

        ReadingPosition? received = null;
        _controller.FocusedViewport!.ReadingPositionChanged = pos => received = pos;

        _controller.HandleArrowDown();
        Assert.Null(received);
    }

    [Fact]
    public void PageChanged_FiredOnLinkClickNavigation()
    {
        var state = CreateAndAddDocument();

        // Inject a link at page-point (100, 100) → page 2
        state.SetLinks(0,
        [
            new PdfLink
            {
                Rect = new RectF(50, 50, 200, 200),
                Destination = new PageDestination { PageIndex = 2 }
            }
        ]);

        double canvasX = 100 * state.Camera.Zoom + state.Camera.OffsetX;
        double canvasY = 100 * state.Camera.Zoom + state.Camera.OffsetY;

        int? receivedPage = null;
        _controller.FocusedViewport!.PageChanged = page => receivedPage = page;

        _controller.HandleClick(canvasX, canvasY);
        Assert.Equal(2, receivedPage);
    }

    // --- Agent API: regression tests for the code-review fixes ---

    [Fact]
    public void NavigateToRole_MultiLineTarget_LandsOnFirstLine()
    {
        var state = CreateAndAddDocument();
        // Heading (1 line) followed by a 5-line Text block.
        TestFixtures.SetupRailMode(state, _controller.Config, 800, 600,
            (BlockRole.Heading, new BBox(72, 72, 468, 30), 1),
            (BlockRole.Text, new BBox(72, 150, 468, 250), 5));
        state.Rail.CurrentBlock = 0; // start on the Heading
        state.Rail.CurrentLine = 0;

        Assert.True(_controller.NavigateToRole(BlockRole.Text));

        var pos = _controller.GetReadingPosition();
        Assert.NotNull(pos);
        Assert.Equal(BlockRole.Text, pos.Role);
        // Regression: NavigateToRole used to snap via the block's geometric centre,
        // landing on the line nearest the centre (~line 2 of 5). It must start at
        // the first line so an agent reads the block top-to-bottom.
        Assert.Equal(0, pos.LineIndex);
    }

    [Fact]
    public void NavigateToRole_OverlappingBlocks_LandsOnRoleMatchedBlock()
    {
        var state = CreateAndAddDocument();
        // A Heading nested inside a larger Text block (its centre lies within Text).
        TestFixtures.SetupRailMode(state, _controller.Config, 800, 600,
            (BlockRole.Text, new BBox(72, 72, 468, 400), 1),
            (BlockRole.Heading, new BBox(150, 200, 168, 40), 1));
        state.Rail.CurrentBlock = 0; // start on the enclosing Text block
        state.Rail.CurrentLine = 0;

        Assert.True(_controller.NavigateToRole(BlockRole.Heading));

        // Regression: snapping by the Heading's centre point used to hit-test into
        // the enclosing Text block (first navigable block containing the point) and
        // land there while still returning true. It must land on the Heading.
        Assert.Equal(BlockRole.Heading, _controller.GetReadingPosition()!.Role);
    }

    [Fact]
    public void GetReadingPosition_BlockIndex_AlignsWithPageDescription_WhenNonNavigableBlocksPrecede()
    {
        var state = CreateAndAddDocument();
        // A non-navigable Figure precedes the navigable blocks, so the navigable-
        // subset index (Rail.CurrentBlock) and the page-level array index diverge.
        TestFixtures.SetupRailMode(state, _controller.Config, 800, 600,
            (BlockRole.Figure, new BBox(72, 72, 468, 100), 1),    // array 0, non-navigable
            (BlockRole.Text, new BBox(72, 200, 468, 100), 1),     // array 1, navigable
            (BlockRole.Heading, new BBox(72, 320, 468, 40), 1));  // array 2, navigable
        state.Rail.CurrentBlock = 0; // first navigable block = the Text block
        state.Rail.CurrentLine = 0;

        var pos = _controller.GetReadingPosition();
        Assert.NotNull(pos);
        Assert.Equal(BlockRole.Text, pos.Role);
        // BlockIndex is the page-level index (Text is the 2nd block), not the
        // navigable-subset index (0).
        Assert.Equal(1, pos.BlockIndex);

        // It indexes directly into GetPageDescription().Blocks.
        var desc = _controller.GetPageDescription();
        Assert.NotNull(desc);
        Assert.Equal(pos.Role, desc.Blocks[pos.BlockIndex].Role);
        Assert.Equal(pos.BlockBBox, desc.Blocks[pos.BlockIndex].BBox);
    }

    [Fact]
    public void GoToPage_SamePageOrClamped_DoesNotFirePageChanged()
    {
        CreateAndAddDocument(); // 3-page PDF, starts on page 0

        int fireCount = 0;
        int lastPage = -1;
        _controller.FocusedViewport!.PageChanged = page => { fireCount++; lastPage = page; };

        _controller.GoToPage(0); // no-op (already on page 0)
        Assert.Equal(0, fireCount);

        _controller.GoToPage(1); // real change
        Assert.Equal(1, fireCount);
        Assert.Equal(1, lastPage);

        _controller.GoToPage(1); // no-op (already on page 1)
        Assert.Equal(1, fireCount);

        _controller.GoToPage(999); // clamps to last page (2) — real change
        Assert.Equal(2, fireCount);
        Assert.Equal(2, lastPage);

        _controller.GoToPage(999); // clamps to 2 == current — no-op
        Assert.Equal(2, fireCount);
    }

    [Fact]
    public void ReadingPositionChanged_EventPayloadHasNoText_PullApiHasText()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        // Inject text so the pull API can extract it.
        var bbox = state.Rail.CurrentNavigableBlock.BBox;
        var chars = "Hello world"
            .Select((_, i) => new CharBox(i, bbox.X + i * 10, bbox.Y + 5, bbox.X + i * 10 + 10, bbox.Y + 20))
            .ToList();
        state.SetText(0, new PageText("Hello world and more", chars));

        ReadingPosition? evt = null;
        _controller.FocusedViewport!.ReadingPositionChanged = p => evt = p;
        _controller.HandleArrowDown(); // fires ReadingPositionChanged

        Assert.NotNull(evt);
        // The push payload deliberately carries no text (hot path).
        Assert.Equal("", evt.BlockText);
        Assert.Equal("", evt.LineText);
        // The pull API does extract text.
        Assert.NotEmpty(_controller.GetReadingPosition()!.BlockText);
    }

    [Fact]
    public void GetPageDescription_TruncatedPreview_KeepsEllipsisDespiteLeadingWhitespace()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state); // single Text block, BBox(72,72,468,200)

        var bbox = state.Rail.CurrentNavigableBlock.BBox;
        // 5 leading spaces + 300 letters: trimming the 200-char window would drop
        // the length below 200, which used to suppress the ellipsis.
        var text = new string(' ', 5) + new string('A', 300);
        var chars = new List<CharBox>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            float x = bbox.X + (i % 80) * 5;
            float y = bbox.Y + (i / 80) * 12;
            chars.Add(new CharBox(i, x, y, x + 5, y + 12));
        }
        state.SetText(0, new PageText(text, chars));

        var preview = _controller.GetPageDescription()!.Blocks[0].TextPreview;
        Assert.EndsWith("…", preview);          // truncation is still signalled
        Assert.DoesNotContain(" ", preview);     // leading whitespace was trimmed
    }

    [Fact]
    public void GetPageDescription_ShortBlock_NoEllipsis()
    {
        var state = CreateAndAddDocument();
        SetupRailMode(state);

        var bbox = state.Rail.CurrentNavigableBlock.BBox;
        const string text = "Short block text.";
        var chars = text
            .Select((_, i) => new CharBox(i, bbox.X + i * 5, bbox.Y + 5, bbox.X + i * 5 + 5, bbox.Y + 18))
            .ToList();
        state.SetText(0, new PageText(text, chars));

        var preview = _controller.GetPageDescription()!.Blocks[0].TextPreview;
        Assert.Equal(text, preview);
        Assert.DoesNotContain("…", preview);
    }

    [Fact]
    public void AnalysisPageReady_FiresForOpenDocument_ViaWorker()
    {
        // Drive the real analysis worker with a fake analyzer (no ONNX model).
        _controller.InitializeWorker(
            FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer());

        var received = new List<int>();
        _controller.AnalysisPageReady = page => received.Add(page);

        CreateAndAddDocument(); // submits page-0 analysis on a background thread

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!received.Contains(0) && sw.ElapsedMilliseconds < 5000)
        {
            _controller.PollAnalysisResults();
            System.Threading.Thread.Sleep(10);
        }

        // The result belongs to an open document, so it must be announced.
        Assert.Contains(0, received);
    }

    // ---- FocusBlock confinement (xhigh review) -------------------------------------------------------

    [Fact]
    public void HandleZoomKey_ZoomOut_ConfinedView_FlooredAtBlockFit()
    {
        var doc = CreateAndAddDocument();
        _controller.SetViewportSize(400, 400);

        var blocks = new List<LayoutBlock>();
        for (int i = 0; i < 4; i++)
            blocks.Add(new LayoutBlock { BBox = new BBox(0, i * 20, 100, 18), Role = BlockRole.Text, Lines = [] });
        doc.SetAnalysis(0, doc.DefaultAnalysisParams,
            new PageAnalysis { Blocks = blocks, PageWidth = 612, PageHeight = 792 });

        var bounds = new BBox(100, 100, 60, 30);
        doc.Primary.Focus = new FocusBlock(0, 0, bounds);
        double fit = doc.Primary.ComputeBlockFitZoom(bounds, 400, 400);
        doc.Camera.Zoom = fit;

        _controller.HandleZoomKey(zoomIn: false); // try to zoom out past the whole block

        // The tween target must not drop below block-fit — the clamp is inert mid-tween, so a lower target
        // would briefly reveal off-block content before snapping back.
        double target = doc.Primary.Zoom.PendingTargetZoom ?? doc.Camera.Zoom;
        Assert.True(target >= fit - 1e-6, $"zoom-out target {target} fell below the block-fit floor {fit}");
    }

    [Fact]
    public void RetargetFocus_RelocatesPortalToAnotherPageAndReconfines()
    {
        var doc = CreateAndAddDocument(); // 3-page PDF, focused on page 0
        _controller.SetViewportSize(400, 400);

        doc.Primary.Focus = new FocusBlock(0, 0, new BBox(0, 0, 100, 100));
        Assert.Equal(0, doc.Primary.CurrentPage);
        Assert.Equal(0, doc.Primary.CurrentFocusBlockIndex);

        bool ok = _controller.RetargetFocus(2, 1, new BBox(10, 10, 120, 60));

        Assert.True(ok);
        Assert.Equal(2, doc.Primary.CurrentPage);            // moved (a direct CurrentPage set would be refused)
        Assert.Equal(2, doc.Primary.Focus!.Page);
        Assert.Equal(1, doc.Primary.Focus!.BlockIndex);
        Assert.Equal(1, doc.Primary.CurrentFocusBlockIndex); // re-confined on the new page

        Assert.False(_controller.RetargetFocus(99, 0, new BBox(0, 0, 1, 1))); // out-of-range page rejected
        Assert.Equal(2, doc.Primary.Focus!.Page);            // ...and leaves the existing focus untouched
    }

    [Fact]
    public void HandleZoom_DegenerateConfinedBlock_AboveZoomMax_DoesNotThrow()
    {
        // Regression (review fix A): a zero-area FocusBlock makes ComputeBlockFitZoom return the raw uncapped
        // Camera.Zoom; if that exceeds ZoomMax, ConfinedZoomFloor must still cap at ZoomMax so the zoom
        // handlers' Math.Clamp(target, floor, ZoomMax) can't throw min>max.
        var doc = CreateAndAddDocument();
        _controller.SetViewportSize(400, 400);
        doc.Primary.Focus = new FocusBlock(0, 0, new BBox(100, 100, 0, 0)); // zero-area
        doc.Camera.Zoom = Camera.ZoomMax + 10;                             // above max

        Assert.True(doc.Primary.ConfinedZoomFloor(400, 400) <= Camera.ZoomMax + 1e-9);
        Assert.Null(Record.Exception(() => _controller.HandleZoomKey(zoomIn: false)));
        Assert.Null(Record.Exception(() => _controller.HandleZoom(-1.0, 200, 200, ctrlHeld: false)));
    }

    [Fact]
    public void RetargetFocus_FramesDestinationBlock_NotLeftOverMagnified()
    {
        // Review fix B: relocating from a tiny block (high zoom) to a larger block must fit the destination,
        // not leave it over-magnified — the confinement clamp alone only RAISES zoom, so RetargetFocus fits.
        var doc = CreateAndAddDocument();
        _controller.SetViewportSize(400, 400);

        doc.Primary.Focus = new FocusBlock(0, 0, new BBox(0, 0, 10, 10)); // tiny
        doc.Camera.Zoom = 15.0;                                           // as if fit to the tiny block

        var bigBlock = new BBox(0, 0, 300, 600);
        _controller.RetargetFocus(2, 1, bigBlock);

        Assert.Equal(2, doc.Primary.CurrentPage);
        Assert.True(doc.Camera.Zoom < 15.0, "zoom should drop to fit the larger destination block");
        double expectedFit = Math.Min(400.0 / 300.0, 400.0 / 600.0); // CenterPage's confined no-margin fit
        Assert.Equal(expectedFit, doc.Camera.Zoom, precision: 2);
    }
}
