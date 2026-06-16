using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Unit tests for the semi-auto park decision (<see cref="DocumentController.ShouldParkOnLineAdvance"/>).
/// The decision is pure (no timers/snaps), so it is tested directly rather than by driving the
/// wall-clock-timed Tick loop. Page changes always park and are handled separately by the orchestrator.
/// </summary>
public class AutoScrollParkDecisionTests
{
    private static readonly IReadOnlySet<BlockRole> StopClasses = DefaultRoleSets.AutoScrollStop;

    [Fact]
    public void ProseAcrossBlockBoundary_WithinChunk_DoesNotPark()
    {
        // Entering a new prose (Text) block within the same chunk: prose flows across the break.
        Assert.False(DocumentController.ShouldParkOnLineAdvance(
            enteredNewChunk: false, enteredNewBlock: true, newRole: BlockRole.Text, StopClasses));
    }

    [Fact]
    public void MidProseBlockLineAdvance_DoesNotPark()
    {
        // A line advance within the same prose block.
        Assert.False(DocumentController.ShouldParkOnLineAdvance(
            enteredNewChunk: false, enteredNewBlock: false, newRole: BlockRole.Text, StopClasses));
    }

    [Theory]
    [InlineData(BlockRole.DisplayMath)]
    [InlineData(BlockRole.Algorithm)]
    [InlineData(BlockRole.Table)]
    [InlineData(BlockRole.Figure)]
    [InlineData(BlockRole.Chart)]
    [InlineData(BlockRole.Heading)]
    [InlineData(BlockRole.Title)]
    public void EnteringStopRoleBlock_Parks(BlockRole role)
    {
        Assert.True(DocumentController.ShouldParkOnLineAdvance(
            enteredNewChunk: false, enteredNewBlock: true, newRole: role, StopClasses));
    }

    [Fact]
    public void NewChunk_AlwaysParks_EvenForProse()
    {
        // A column/section break parks regardless of the new block's role.
        Assert.True(DocumentController.ShouldParkOnLineAdvance(
            enteredNewChunk: true, enteredNewBlock: true, newRole: BlockRole.Text, StopClasses));
    }

    [Fact]
    public void MultiLineStopBlock_ParksOnceOnEntry_ThenFlows()
    {
        // Entry into the stop block parks…
        Assert.True(DocumentController.ShouldParkOnLineAdvance(
            enteredNewChunk: false, enteredNewBlock: true, newRole: BlockRole.DisplayMath, StopClasses));
        // …but its remaining lines (same block) flow without re-parking after the reader resumes.
        Assert.False(DocumentController.ShouldParkOnLineAdvance(
            enteredNewChunk: false, enteredNewBlock: false, newRole: BlockRole.DisplayMath, StopClasses));
    }

    [Fact]
    public void LeavingStopBlockIntoProse_DoesNotPark()
    {
        Assert.False(DocumentController.ShouldParkOnLineAdvance(
            enteredNewChunk: false, enteredNewBlock: true, newRole: BlockRole.Text, StopClasses));
    }

    [Fact]
    public void StopClassesHonoured_DroppingHeading_NoParkOnHeadings()
    {
        // Config-derived stop set: with Heading removed, entering a heading no longer parks…
        var noHeadings = new HashSet<BlockRole>(StopClasses);
        noHeadings.Remove(BlockRole.Heading);

        Assert.False(DocumentController.ShouldParkOnLineAdvance(
            enteredNewChunk: false, enteredNewBlock: true, newRole: BlockRole.Heading, noHeadings));
        // …while a still-listed role keeps parking.
        Assert.True(DocumentController.ShouldParkOnLineAdvance(
            enteredNewChunk: false, enteredNewBlock: true, newRole: BlockRole.Table, noHeadings));
    }
}
