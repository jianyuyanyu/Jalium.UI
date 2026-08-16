using Jalium.UI.Controls.TextEffects;

namespace Jalium.UI.Tests.TextEffects;

/// <summary>
/// Regression cover for the "text appears with no effect at all" bug.
///
/// <para>
/// <see cref="TextEffectPresenter"/> stamps its clock when it SUBSCRIBES to the frame loop,
/// but the first callback only lands once the window is actually on screen. That gap is
/// routinely longer than a whole enter animation; fed in verbatim it drove every cell to
/// Settled on the very first frame, so every built-in effect rendered as a plain, instantly
/// finished string. The delta resolver is what keeps a stalled clock from eating the animation.
/// </para>
/// </summary>
public class FrameDeltaClampTests
{
    [Fact]
    public void FirstTickAfterSubscribe_DoesNotAdvanceTheClock()
    {
        // The subscribe→first-frame gap says nothing about how long the animation has run;
        // it only establishes the time base.
        Assert.Equal(0, TextEffectPresenter.ResolveFrameDelta(1_500, isFirstTick: true));
        Assert.Equal(0, TextEffectPresenter.ResolveFrameDelta(16, isFirstTick: true));
    }

    [Fact]
    public void HealthyFrameDeltas_PassThroughUntouched()
    {
        // 60Hz and 30Hz frames must not be altered — clamping is for stalls, not for pacing.
        Assert.Equal(16, TextEffectPresenter.ResolveFrameDelta(16, isFirstTick: false));
        Assert.Equal(33, TextEffectPresenter.ResolveFrameDelta(33, isFirstTick: false));
        Assert.Equal(
            TextEffectPresenter.MaxFrameDeltaMs,
            TextEffectPresenter.ResolveFrameDelta(TextEffectPresenter.MaxFrameDeltaMs, isFirstTick: false));
    }

    [Fact]
    public void StalledClock_IsCappedInsteadOfSkippingAhead()
    {
        // A 2-second stall must not advance a 600ms animation past its end in one step.
        Assert.Equal(
            TextEffectPresenter.MaxFrameDeltaMs,
            TextEffectPresenter.ResolveFrameDelta(2_000, isFirstTick: false));
    }

    [Fact]
    public void NonPositiveDelta_IsTreatedAsNoTime()
    {
        // Same-millisecond ticks and any clock going backwards must never rewind the animation.
        Assert.Equal(0, TextEffectPresenter.ResolveFrameDelta(0, isFirstTick: false));
        Assert.Equal(0, TextEffectPresenter.ResolveFrameDelta(-5, isFirstTick: false));
    }

    [Fact]
    public void CappedSteps_StillReachTheEnd_JustLate()
    {
        // Capping trades "skips the animation" for "runs it slightly slow": accumulating capped
        // steps must still cover a typical entrance rather than stalling short of it.
        double elapsed = 0;
        for (var frame = 0; frame < 12; frame++)
        {
            elapsed += TextEffectPresenter.ResolveFrameDelta(500, isFirstTick: false);
        }

        Assert.True(elapsed >= 600, $"capped steps only reached {elapsed}ms");
    }
}
