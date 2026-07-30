using Jalium.UI.Controls;
using Jalium.UI.Interop;
using Jalium.UI.Media;
using RenderTargetDrawingContext = Jalium.UI.Interop.RenderTargetDrawingContext;

namespace Jalium.UI.Tests;

/// <summary>
/// Regression coverage for transformed content rendered below a clip that was
/// established in surface space (the ScrollViewer + Viewbox zoom path).
/// </summary>
[Collection("Application")]
public sealed class ViewboxClipCullingTests
{
    [Fact]
    public void ViewboxAt257Percent_DoesNotCullVisibleChildInLowerHalf()
    {
        const int surfaceSize = 320;

        using var window = new HiddenNativeWindow(surfaceSize, surfaceSize);
        using var context = new RenderContext();
        using var renderTarget = context.CreateRenderTarget(
            window.Hwnd,
            surfaceSize,
            surfaceSize);
        Assert.True(renderTarget.IsValid);
        Assert.True(renderTarget.TryBeginDraw());

        var drawingContext = new RenderTargetDrawingContext(renderTarget, context);
        try
        {
            var upperHighlight = new CountingElement
            {
                Width = 20,
                Height = 10,
            };
            Canvas.SetLeft(upperHighlight, 10);
            Canvas.SetTop(upperHighlight, 10);

            var lowerHighlight = new CountingElement
            {
                Width = 20,
                Height = 10,
            };
            Canvas.SetLeft(lowerHighlight, 10);
            Canvas.SetTop(lowerHighlight, 75);

            var offscreenHighlight = new CountingElement
            {
                Width = 20,
                Height = 10,
            };
            Canvas.SetLeft(offscreenHighlight, 10);
            Canvas.SetTop(offscreenHighlight, 125);

            var sourceSurface = new Canvas
            {
                Width = 100,
                Height = 100,
            };
            sourceSurface.Children.Add(upperHighlight);
            sourceSurface.Children.Add(lowerHighlight);
            sourceSurface.Children.Add(offscreenHighlight);

            var viewbox = new Viewbox
            {
                Width = 257,
                Height = 257,
                Stretch = Stretch.Fill,
                Child = sourceSurface,
            };
            viewbox.Measure(new Size(257, 257));
            viewbox.Arrange(new Rect(0, 0, 257, 257));

            // Matches a ScrollViewer/damage clip established before Viewbox
            // pushes its 2.57x native scale. Inside that transform the drawing
            // context exposes the inverse-mapped clip (0..100), so descendants
            // must be culled in that same current drawing space.
            drawingContext.PushDirtyRegionClip(new Rect(0, 0, 257, 257));
            viewbox.Render(drawingContext);
            drawingContext.PopDirtyRegionClip();

            Assert.Equal(1, upperHighlight.RenderCount);
            Assert.Equal(1, lowerHighlight.RenderCount);
            Assert.Equal(0, offscreenHighlight.RenderCount);
        }
        finally
        {
            drawingContext.Close();
            renderTarget.TryEndDraw();
        }
    }

    private sealed class CountingElement : FrameworkElement
    {
        public int RenderCount { get; private set; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            RenderCount++;
        }
    }
}
