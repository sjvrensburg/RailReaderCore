using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class ReadingGuideSteerTests
{
    private static ReadingGuideSteer Stick() => new()
    {
        DeadZonePx = 16, FullDeflectionPx = 160,
        MinCharsPerSecond = 5, MaxCharsPerSecond = 17,
        MinLinesPerSecond = 0.8, MaxLinesPerSecond = 3,
        SentencePauseChars = 6, MinWordChars = 2,
    };

    // Ticks at 60 Hz until a step comes out; returns the seconds it took (or -1 after `maxSecs`).
    private static double SecondsToStep(ReadingGuideSteer s, int chars, bool sentenceEnd, out SteerOutput step, double maxSecs = 10)
    {
        const double dt = 1.0 / 60;
        double t = 0;
        while (t < maxSecs)
        {
            step = s.Tick(dt, chars, sentenceEnd);
            t += dt;
            if (step.Any) return t;
        }
        step = SteerOutput.None;
        return -1;
    }

    [Fact]
    public void NotEngaged_OrInsideDeadZone_NeverMoves()
    {
        var s = Stick();
        Assert.False(s.Tick(1, 5, false).Any);
        s.Engage(100, 100);
        s.SetPointer(110, 106);          // 10 px right, inside the 16 px dead zone
        Assert.Equal(-1, SecondsToStep(s, 5, false, out _, maxSecs: 3));
        Assert.Equal(SteerAxis.None, s.Axis);
        Assert.False(s.IsMoving);
    }

    [Fact]
    public void FullDeflectionRight_AdvancesAtMaxPace_InCharacters()
    {
        var s = Stick();
        s.Engage(100, 100);
        s.SetPointer(100 + 200, 100);     // past full deflection
        double t = SecondsToStep(s, 5, false, out var step);
        Assert.Equal(1, step.WordSteps);
        Assert.Equal(0, step.LineSteps);
        Assert.Equal(SteerAxis.Horizontal, step.Axis);
        Assert.Equal(1.0, step.Throttle, 3);
        Assert.InRange(t, 5.0 / 17 - 0.02, 5.0 / 17 + 0.02);     // 5 chars at 17 chars/s ≈ 0.29 s
        Assert.True(s.IsMoving);

        // A longer word dwells proportionally longer.
        double t10 = SecondsToStep(s, 10, false, out _);
        Assert.InRange(t10, 10.0 / 17 - 0.02, 10.0 / 17 + 0.02);
    }

    [Fact]
    public void DeadZoneEdge_IsMinPace_AndRampIsQuadratic()
    {
        var s = Stick();
        s.Engage(0, 0);
        s.SetPointer(17, 0);               // just past the dead zone → ≈ min pace
        double tMin = SecondsToStep(s, 10, false, out _);
        Assert.InRange(tMin, 10.0 / 5 - 0.05, 10.0 / 5 + 0.05);

        s.Engage(0, 0);
        s.SetPointer(16 + 72, 0);          // half deflection: ramp 0.25 → 5 + 12 × 0.25 = 8 chars/s
        double tHalf = SecondsToStep(s, 10, false, out var step);
        Assert.InRange(tHalf, 10.0 / 8 - 0.05, 10.0 / 8 + 0.05);
        Assert.Equal(0.5, step.Throttle, 2);
    }

    [Fact]
    public void Left_ReversesSign_AndDirectionChangeResetsProgress()
    {
        var s = Stick();
        s.Engage(0, 0);
        s.SetPointer(200, 0);
        for (int i = 0; i < 10; i++) s.Tick(1.0 / 60, 10, false);   // partway through a 10-char dwell
        s.SetPointer(-200, 0);                                       // flip
        double t = SecondsToStep(s, 10, false, out var step);
        Assert.Equal(-1, step.WordSteps);
        Assert.InRange(t, 10.0 / 17 - 0.02, 10.0 / 17 + 0.02);       // a full dwell again, nothing carried over
        Assert.Equal(-1.0, step.Throttle, 3);
    }

    [Fact]
    public void VerticalDeflection_StepsOneLineAtOnce_ThenRepeatsAfterTheHoldDelay()
    {
        var s = Stick();
        s.LineRepeatDelaySecs = 0.5;
        s.Engage(0, 0);
        s.SetPointer(30, 200);             // |dy| > |dx|
        var step = s.Tick(1.0 / 60, 5, false);
        Assert.Equal((0, 1, SteerAxis.Vertical), (step.WordSteps, step.LineSteps, step.Axis));   // immediate

        // Nothing during the repeat delay, then steps at 3 lines/s.
        double dt = 1.0 / 60, t = dt;
        while (t < 0.5 - 1e-9) { Assert.False(s.Tick(dt, 5, false).Any); t += dt; }
        double tNext = SecondsToStep(s, 5, false, out step);
        Assert.Equal(1, step.LineSteps);
        Assert.InRange(tNext, 1.0 / 3 - 0.05, 1.0 / 3 + 0.05);   // ± a frame or two of hold/accumulation rounding

        // Reversing fires at once again.
        s.SetPointer(30, -200);
        Assert.Equal(-1, s.Tick(dt, 5, false).LineSteps);
    }

    [Fact]
    public void ADriftIntoTheVerticalAxis_MovesOneLineOnly()
    {
        var s = Stick();
        s.Engage(0, 0);
        s.SetPointer(0, 40);
        int lines = 0;
        for (int i = 0; i < 24; i++) lines += s.Tick(1.0 / 60, 5, false).LineSteps;   // 0.4 s of drift
        Assert.Equal(1, lines);
        s.SetPointer(0, 0);                // back to the dead zone resets the gesture
        s.Tick(1.0 / 60, 5, false);
        s.SetPointer(0, 40);
        Assert.Equal(1, s.Tick(1.0 / 60, 5, false).LineSteps);
    }

    [Fact]
    public void SentenceEnd_AddsAPause_AndShortWordsAreFloored()
    {
        var s = Stick();
        s.Engage(0, 0);
        s.SetPointer(200, 0);
        double plain = SecondsToStep(s, 5, false, out _);
        double paused = SecondsToStep(s, 5, true, out _);
        Assert.InRange(paused - plain, 6.0 / 17 - 0.03, 6.0 / 17 + 0.03);

        double one = SecondsToStep(s, 1, false, out _);
        double two = SecondsToStep(s, 2, false, out _);
        Assert.InRange(one, two - 0.02, two + 0.02);                 // "a" dwells like a 2-char word
    }

    [Fact]
    public void LineOnly_UsesTheLineStepCost()
    {
        var s = Stick();
        s.Engage(0, 0);
        s.SetPointer(200, 0);
        double t = SecondsToStep(s, 0, false, out var step);
        Assert.Equal(1, step.WordSteps);
        Assert.InRange(t, 12.0 / 17 - 0.02, 12.0 / 17 + 0.02);
    }

    [Fact]
    public void AtMostOneStepPerTick_EvenWithAHugeDt()
    {
        var s = Stick();
        s.Engage(0, 0);
        s.SetPointer(200, 0);
        var step = s.Tick(5.0, 3, false);
        Assert.Equal(1, step.WordSteps);
    }

    [Fact]
    public void Release_StopsEverything()
    {
        var s = Stick();
        s.Engage(0, 0);
        s.SetPointer(200, 0);
        s.Tick(0.1, 5, false);
        s.Release();
        Assert.False(s.Engaged);
        Assert.False(s.IsMoving);
        Assert.False(s.Tick(1, 5, false).Any);
    }
}
