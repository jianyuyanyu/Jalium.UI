using Jalium.UI.Controls;
using Jalium.UI.Interop;
using Jalium.UI.Media;
using RenderTargetDrawingContext = Jalium.UI.Interop.RenderTargetDrawingContext;

namespace Jalium.UI.Tests;

/// <summary>
/// Pins the retained-GPU-layer composite contract for a pure <see cref="TranslateTransform"/>:
/// <see cref="RenderTargetDrawingContext.PushTransform(Transform)"/> folds a translate into the
/// managed <c>Offset</c> (no native matrix), and <c>CompositeLayer</c> hands native an absolute
/// <c>worldBounds</c> rectangle — so the composite must add that Offset delta itself, or a
/// TranslateTransform-animated container is composited at its un-translated slot while dirty
/// tracking and child culling follow the translated one (the element "does not move" and the
/// neighbours it passes over get partially erased — the docking tab-strip reorder symptom).
/// </summary>
[Collection("Application")]
public sealed class RetainedLayerTranslateCompositeTests
{
    private const int Width = 256;
    private const int Height = 256;
    private const double RectSize = 30;
    private const double RectOrigin = 10;
    private const double TranslateX = 90;
    private const double TranslateY = 20;

    /// <summary>
    /// Pure managed contract: the rectangle handed to native by <c>CompositeLayer</c> is
    /// <c>worldBounds</c> shifted by the TranslateTransform, while a non-translate transform
    /// (native matrix path) leaves the rectangle untouched.
    /// </summary>
    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void CompositeLayer_TranslateTransform_ShiftsCompositeRectByTranslation()
    {
        RunFrames((rt, ctx) =>
        {
            Assert.True(rt.TryBeginDraw());
            var dc = new RenderTargetDrawingContext(rt, ctx);
            try
            {
                if (!((ILayerCompositingDrawingContext)dc).SupportsRetainedLayers)
                {
                    return; // software / integrated backends: the fast path is never taken
                }

                var world = new Rect(40, 50, 30, 30);
                // A refused/zero handle returns before the native call, so realize a real
                // layer first: the composite then reaches native and records its rectangle.
                nint layer = dc.BeginLayerCapture(0, world);
                if (layer == 0) return;
                dc.EndLayerCapture(layer);

                dc.CompositeLayer(layer, world, 1.0, new TranslateTransform(TranslateX, TranslateY), 0, 0);
                Assert.Equal(1, dc.CompositedLayerCountForTests);
                var shifted = dc.LastCompositedLayerRectForTests;
                Assert.Equal(world.X + TranslateX, shifted.X, 1e-6);
                Assert.Equal(world.Y + TranslateY, shifted.Y, 1e-6);
                Assert.Equal(world.Width, shifted.Width, 1e-6);
                Assert.Equal(world.Height, shifted.Height, 1e-6);

                // Managed Offset must be restored after the composite (balanced push/pop).
                Assert.Equal(0, dc.Offset.X, 1e-9);
                Assert.Equal(0, dc.Offset.Y, 1e-9);

                dc.CompositeLayer(layer, world, 1.0, new ScaleTransform(1, 1), 0, 0);
                var unshifted = dc.LastCompositedLayerRectForTests;
                Assert.Equal(world.X, unshifted.X, 1e-6);
                Assert.Equal(world.Y, unshifted.Y, 1e-6);

                dc.CompositeLayer(layer, world, 1.0, null, 0, 0);
                var plain = dc.LastCompositedLayerRectForTests;
                Assert.Equal(world.X, plain.X, 1e-6);
                Assert.Equal(world.Y, plain.Y, 1e-6);
            }
            finally
            {
                dc.Close();
                Assert.Equal(JaliumResult.Ok, rt.TryEndDraw());
            }
        });
    }

    /// <summary>
    /// End to end through the visual tree on D3D12 (the backend with retained layers): frame 1
    /// renders the translated container inline (content dirty → no layer), frame 2 takes the
    /// layer path (content clean, live TranslateTransform). The readback of frame 2 must show the
    /// container's ink at the TRANSLATED position and nothing at the un-translated slot.
    /// </summary>
    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void TranslatedContainer_CompositedFromRetainedLayer_LandsAtTranslatedPosition()
    {
        RunFrames((rt, ctx) =>
        {
            var inner = new WhiteRectElement { Width = RectSize, Height = RectSize };
            var translated = new Grid
            {
                Width = RectSize,
                Height = RectSize,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(RectOrigin, RectOrigin, 0, 0),
                RenderTransform = new TranslateTransform(TranslateX, TranslateY),
            };
            translated.Children.Add(inner);
            var root = new Grid { Width = Width, Height = Height };
            root.Children.Add(translated);
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));

            // Frame 1: content dirty → inline path (layer is only realized once clean).
            RenderFrame(rt, ctx, root, readback: false, out _, out _);

            // Frame 2: content clean + live TranslateTransform → retained-layer composite.
            var pixels = RenderFrame(rt, ctx, root, readback: true, out var layerComposites, out var supported);
            if (!supported) return; // no retained layers on this host: nothing to pin

            Assert.True(layerComposites >= 1, "frame 2 should have composited the translated container from its retained layer");

            double sx = rt.DpiScaleX, sy = rt.DpiScaleY;
            var translatedCentre = (
                x: (int)Math.Round((RectOrigin + TranslateX + RectSize / 2) * sx),
                y: (int)Math.Round((RectOrigin + TranslateY + RectSize / 2) * sy));
            var untranslatedCentre = (
                x: (int)Math.Round((RectOrigin + RectSize / 2) * sx),
                y: (int)Math.Round((RectOrigin + RectSize / 2) * sy));

            Assert.True(IsBright(pixels, translatedCentre.x, translatedCentre.y),
                $"expected the composited layer at the translated position {translatedCentre}, got {Describe(pixels, translatedCentre.x, translatedCentre.y)}");
            Assert.False(IsBright(pixels, untranslatedCentre.x, untranslatedCentre.y),
                $"the un-translated slot {untranslatedCentre} must stay background, got {Describe(pixels, untranslatedCentre.x, untranslatedCentre.y)}");
        });
    }

    private static byte[] RenderFrame(RenderTarget rt, RenderContext ctx, UIElement root, bool readback,
        out int layerComposites, out bool supported)
    {
        Assert.True(rt.TryBeginDraw());
        rt.Clear(0.12f, 0.12f, 0.18f);
        var dc = new RenderTargetDrawingContext(rt, ctx);
        try
        {
            supported = ((ILayerCompositingDrawingContext)dc).SupportsRetainedLayers;
            root.Render(dc);
            layerComposites = dc.CompositedLayerCountForTests;
            if (readback)
            {
                Assert.Equal(JaliumResult.Ok, rt.RequestReadback());
            }
        }
        finally
        {
            dc.Close();
            Assert.Equal(JaliumResult.Ok, rt.TryEndDraw());
        }

        if (!readback) return Array.Empty<byte>();
        var pixels = new byte[Width * Height * 4];
        Assert.Equal(JaliumResult.Ok, rt.FetchReadback(pixels, (uint)(Width * 4), out _, out _));
        return pixels;
    }

    private static void RunFrames(Action<RenderTarget, RenderContext> body)
    {
        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(RenderBackend.D3D12);
        Assert.Equal(RenderBackend.D3D12, context.Backend);
        using var renderTarget = context.CreateRenderTarget(window.Hwnd, Width, Height);
        Assert.True(renderTarget.IsValid);
        body(renderTarget, context);
    }

    private static bool IsBright(byte[] bgra, int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
        int i = (y * Width + x) * 4;
        return bgra[i] > 200 && bgra[i + 1] > 200 && bgra[i + 2] > 200;
    }

    private static string Describe(byte[] bgra, int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return "out of range";
        int i = (y * Width + x) * 4;
        return $"B={bgra[i]} G={bgra[i + 1]} R={bgra[i + 2]} A={bgra[i + 3]}";
    }

    private sealed class WhiteRectElement : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext) =>
            drawingContext.DrawRectangle(
                Brushes.White,
                null,
                new Rect(0, 0, RenderSize.Width, RenderSize.Height));
    }
}
