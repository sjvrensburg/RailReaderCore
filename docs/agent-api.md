# Agent API

RailReaderCore's `DocumentController` is a headless, UI-free orchestration surface designed to be driven by AI agents, automated tests, or user interfaces. This document describes the API surface for external consumers.

## Overview

```csharp
var controller = new DocumentController(
    coreSettings, recentFilesStore, annotationStore,
    threadMarshaller, pdfServiceFactory);
```

All commands and queries run synchronously on the calling thread. In a UI application, that thread is the UI thread; in a headless agent, any single thread will do (use `SynchronousThreadMarshaller`).

## Document Lifecycle

```csharp
// 1. Create a document state from a file path
var doc = controller.CreateDocument("/path/to/paper.pdf");

// 2. Load the first page bitmap (required before navigation)
doc.LoadPageBitmap();

// 3. Register the document (restores position, submits analysis)
controller.AddDocument(doc);
```

For analysis (layout detection), initialize the worker before opening documents:

```csharp
controller.InitializeWorker(capabilities, () => new MyLayoutAnalyzer());
```

## Query Methods

### `ListDocuments() → DocumentList`

Returns all open documents with their titles, page counts, and current pages.

```csharp
var list = controller.ListDocuments();
// list.ActiveIndex  — which tab is focused
// list.Documents[i]  — DocumentSummary for each tab
```

### `GetDocumentInfo(int? index = null) → DocumentInfo?`

Full state snapshot of one document: file path, title, page count, current page, zoom, camera offsets, rail mode status, analysis status, navigable block count, auto-scroll state.

```csharp
var info = controller.GetDocumentInfo();
Console.WriteLine($"Page {info.CurrentPage + 1}/{info.PageCount}, Rail={info.RailActive}");
```

### `GetReadingPosition(int? index = null) → ReadingPosition?`

Current reading position in rail mode: page, block index, line index, block role, block text, line text, block bounding box. Returns `null` if rail mode is not active.

```csharp
var pos = controller.GetReadingPosition();
if (pos is not null)
    Console.WriteLine($"Reading: {pos.Role} block at line {pos.LineIndex}");
```

### `GetPageDescription(int? page = null) → PageDescription?`

Accessible description of a page's layout: all detected blocks with semantic roles, text previews (up to 200 characters), bounding boxes, and reading order. Useful for building an accessibility tree or giving an agent a page overview.

```csharp
var desc = controller.GetPageDescription();
foreach (var block in desc.Blocks)
    Console.WriteLine($"  [{block.ReadingOrder}] {block.Role}: {block.TextPreview}");
```

### `GetSearchState() → SearchResult`

Current search state: total matches, active match index, matches per page.

## Navigation Commands

### Page Navigation

| Method | Description |
|--------|-------------|
| `GoToPage(int page)` | Navigate to a specific page (0-based) |
| `FitPage()` | Fit the entire page in the viewport |
| `FitWidth()` | Fit the page width in the viewport |

### Rail Navigation (line-by-line)

| Method | Description |
|--------|-------------|
| `HandleArrowDown()` | Advance to next line (or next page at boundary) |
| `HandleArrowUp()` | Go to previous line (or previous page at boundary) |
| `HandleLineHome()` | Snap to start of current line |
| `HandleLineEnd()` | Snap to end of current line |
| `NavigateToRole(BlockRole, bool forward)` | Jump to the next block of a given role (Heading, Table, etc.) |

`NavigateToRole` scans the current page's analysis in reading order and snaps to the next (or previous) navigable block matching the target role. Returns `true` if found.

```csharp
// Jump to the next heading
if (controller.NavigateToRole(BlockRole.Heading))
{
    var pos = controller.GetReadingPosition();
    Console.WriteLine($"Jumped to: {pos!.LineText}");
}
```

### Click Handling

```csharp
var (handled, linkDest) = controller.HandleClick(canvasX, canvasY);
```

Clicks are resolved in priority order: PDF links first, then rail-mode block snapping.

### History

| Method | Description |
|--------|-------------|
| `NavigateToBookmark(int index)` | Jump to a user bookmark |
| `NavigateBack()` | Go back in navigation history |
| `NavigateForward()` | Go forward in navigation history |
| `AddBookmark(string name)` | Bookmark the current page |

## Events

Subscribe to events for reactive state observation. All events fire on the calling thread.

| Event | Type | When |
|-------|------|------|
| `PageChanged` | `Action<int>` | Active document's page changes (parameter = new page index) |
| `ReadingPositionChanged` | `Action<ReadingPosition>` | Rail block or line changes (parameter = new position) |
| `AnalysisPageReady` | `Action<int>` | Layout analysis completes for a page (parameter = page index) |
| `StateChanged` | `Action<string>` | Property changed (legacy; parameter = property name) |
| `StatusMessage` | `Action<string>` | Transient status message for display |

```csharp
controller.PageChanged += page => Console.WriteLine($"Now on page {page + 1}");
controller.ReadingPositionChanged += pos =>
    Console.WriteLine($"Reading {pos.Role} at line {pos.LineIndex}: {pos.LineText}");
controller.AnalysisPageReady += page =>
    Console.WriteLine($"Analysis ready for page {page + 1}");
```

Note: `ReadingPositionChanged` fires from explicit navigation commands (arrow keys, click, `NavigateToRole`, auto-scroll) and edge-hold advances. It does not fire from direct `RailNav` property mutations.

## Block Roles

`NavigateToRole` and `BlockSummary.Role` use the `BlockRole` enum:

| Role | Description |
|------|-------------|
| `Text` | Body text paragraph |
| `Heading` | Section heading |
| `Title` | Document title |
| `Caption` | Figure/table caption |
| `Aside` | Sidebar or margin note |
| `DisplayMath` | Displayed equation |
| `InlineMath` | Inline equation |
| `Algorithm` | Pseudocode/algorithm block |
| `Table` | Table |
| `Figure` | Figure or image |
| `Chart` | Chart or graph |
| `Header` | Page header |
| `Footer` | Page footer |
| `PageNumber` | Page number |
| `Footnote` | Footnote |
| `Reference` | Bibliography entry |

Not all roles are navigable — `NavigableRoles` (from `CoreSettings`) controls which roles the rail system locks onto. By default, text-like roles are navigable; visual roles (Figure, Chart, Table) are not.

## Tick Loop

The controller drives animations (snap, zoom, auto-scroll) and polls analysis results via `Tick`:

```csharp
// Call at ~60fps from your main loop
var result = controller.Tick(deltaTimeSeconds);
if (result.CameraChanged) Repaint();
if (result.PageChanged) UpdatePageDisplay();
if (result.OverlayChanged) RepaintOverlay();
if (result.StillAnimating) RequestNextFrame();
```

For non-animating scenarios (headless agent), call `PollAnalysisResults()` and `TrySubmitBackgroundReadAhead()` periodically instead.

## Example: Headless Agent Session

```csharp
using RailReader.Core;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;

// Wire up
var config = new CoreSettings();
using var controller = new DocumentController(
    config, myRecentFiles, myAnnotationStore,
    new SynchronousThreadMarshaller(), myPdfFactory);

// Open a document
var doc = controller.CreateDocument("/path/to/paper.pdf");
doc.LoadPageBitmap();
controller.AddDocument(doc);
controller.SetViewportSize(1200, 900);

// Wait for analysis (poll-based in headless mode)
while (!controller.GetDocumentInfo()!.HasAnalysis)
    controller.PollAnalysisResults();

// Get an overview
var desc = controller.GetPageDescription();
Console.WriteLine($"Page has {desc!.TotalBlocks} blocks:");
foreach (var block in desc.Blocks)
    Console.WriteLine($"  [{block.ReadingOrder}] {block.Role}: {block.TextPreview}");

// Navigate to the first heading
if (controller.NavigateToRole(BlockRole.Heading))
{
    var pos = controller.GetReadingPosition();
    Console.WriteLine($"Heading: {pos!.BlockText}");
}

// Read line by line
for (int i = 0; i < 5; i++)
{
    var pos = controller.GetReadingPosition();
    Console.WriteLine($"Line {pos!.LineIndex}: {pos.LineText}");
    controller.HandleArrowDown();
}
```
