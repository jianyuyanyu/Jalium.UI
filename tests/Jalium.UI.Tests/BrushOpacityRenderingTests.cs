using Jalium.UI.Interop;
using Jalium.UI.Media;

namespace Jalium.UI.Tests;

public sealed class BrushOpacityRenderingTests
{
    [Fact]
    public void EffectiveBrushAlpha_MultipliesColorAndBrushOpacity()
    {
        float alpha = RenderTargetDrawingContext.EffectiveBrushAlpha(
            128,
            0.25);

        Assert.Equal(128f / 255f * 0.25f, alpha, precision: 6);
    }

    [Fact]
    public void GradientStopMarshalling_AppliesBrushOpacityToEveryStop()
    {
        var stops = new GradientStopCollection
        {
            new(Color.FromArgb(255, 255, 128, 0), 0),
            new(Color.FromArgb(128, 128, 0, 255), 1),
        };

        float[] marshalled =
            RenderTargetDrawingContext.MarshalGradientStops(stops, 0.5);

        Assert.Equal(0.5f, marshalled[4], precision: 6);
        Assert.Equal(128f / 255f * 0.5f, marshalled[9], precision: 6);
    }

    [Fact]
    public void GradientOpacityMutation_InvalidatesCachedContentHash()
    {
        var brush = new LinearGradientBrush(
            Colors.Orange,
            Colors.Purple,
            new Point(0, 0.5),
            new Point(1, 0.5));
        long before = brush.ComputeContentHash();

        brush.Opacity = 0.25;
        long after = brush.ComputeContentHash();

        Assert.NotEqual(before, after);
    }
}
