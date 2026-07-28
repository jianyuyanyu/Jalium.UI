using System.Reflection;
using Jalium.UI;
using Jalium.UI.Media;

namespace Jalium.UI.Tests;

[Collection("Application")]
public class CompositionTargetRenderingTests
{
    [Fact]
    public void UpdateRefreshRate_InvalidDriverSentinel_PreservesLastKnownGoodRate()
    {
        var originalRate = CompositionTarget.RefreshRate;

        try
        {
            Assert.True(CompositionTarget.UpdateRefreshRate(144));
            Assert.Equal(144, CompositionTarget.RefreshRate);
            Assert.Equal(6, CompositionTarget.FrameIntervalMs);

            // EnumDisplaySettings can report 0 or 1 for "default hardware
            // rate", especially while hybrid graphics changes display
            // ownership during a window move/resize. Neither is a literal Hz
            // value and neither may poison the process-wide frame clock.
            Assert.False(CompositionTarget.UpdateRefreshRate(1));
            Assert.Equal(144, CompositionTarget.RefreshRate);
            Assert.Equal(6, CompositionTarget.FrameIntervalMs);

            Assert.False(CompositionTarget.UpdateRefreshRate(0));
            Assert.Equal(144, CompositionTarget.RefreshRate);

            Assert.False(CompositionTarget.UpdateRefreshRate(1001));
            Assert.Equal(144, CompositionTarget.RefreshRate);
        }
        finally
        {
            CompositionTarget.UpdateRefreshRate(originalRate);
        }
    }

    [Fact]
    public void Rendering_WhenOneSubscriberThrows_StillInvokesRemainingSubscribers()
    {
        int callCount = 0;
        EventHandler throwing = (_, _) => throw new InvalidOperationException("Injected test failure.");
        EventHandler healthy = (_, _) => callCount++;

        CompositionTarget.Rendering += throwing;
        CompositionTarget.Rendering += healthy;

        try
        {
            var raiseRendering = typeof(CompositionTarget).GetMethod(
                "RaiseRendering",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(raiseRendering);
            raiseRendering!.Invoke(null, null);
        }
        finally
        {
            CompositionTarget.Rendering -= healthy;
            CompositionTarget.Rendering -= throwing;
        }

        Assert.Equal(1, callCount);
    }
}
