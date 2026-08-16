using Jalium.UI;

namespace Jalium.UI.Tests;

public sealed class WindowTrackSizeTests
{
    [Fact]
    public void MinimumClientWidth_ShouldReserveTheNonClientFrame()
    {
        int trackWidth = Window.ComputeWindowTrackSizeFromClientDip(
            clientSizeDip: 980,
            dpiScale: 1,
            nonClientSizePixels: 14,
            isMinimum: true);

        Assert.Equal(994, trackWidth);
    }

    [Theory]
    [InlineData(true, 142)]
    [InlineData(false, 141)]
    public void FractionalDpi_ShouldRoundTowardTheConstraint(
        bool isMinimum,
        int expected)
    {
        int trackSize = Window.ComputeWindowTrackSizeFromClientDip(
            clientSizeDip: 100.1,
            dpiScale: 1.25,
            nonClientSizePixels: 16,
            isMinimum);

        Assert.Equal(expected, trackSize);
    }

    [Fact]
    public void NegativeNonClientSize_ShouldNotReduceTheClientConstraint()
    {
        int trackWidth = Window.ComputeWindowTrackSizeFromClientDip(
            clientSizeDip: 980,
            dpiScale: 1,
            nonClientSizePixels: -14,
            isMinimum: true);

        Assert.Equal(980, trackWidth);
    }
}
