using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

/// <summary>
/// Guards idle Effect-cache eviction while the command list that references
/// the cached custom-shader PSO is still open.
/// </summary>
[Collection("Application")]
public sealed class D3D12EffectPsoLifetimeTests
{
    private const int Width = 192;
    private const int Height = 160;

    [RequiresBackendFact(RenderBackend.D3D12)]
    public void ReclaimIdleResources_DuringAndAfterEffectFrame_DefersPsoReleaseUntilFence()
    {
        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(
            RenderBackend.D3D12,
            GpuPreference.Auto,
            RenderingEngine.Impeller);
        using var target = context.CreateRenderTarget(
            window.Hwnd,
            Width,
            Height);
        using var fill = context.CreateSolidBrush(0.15f, 0.82f, 0.48f, 1f);

        Assert.Equal(RenderBackend.D3D12, context.Backend);
        Assert.True(target.IsValid);
        Assert.True(fill.IsValid);

        Assert.True(target.TryBeginDraw());
        target.Clear(0.02f, 0.04f, 0.03f);
        target.BeginEffectCapture(32f, 32f, 112f, 80f);
        target.FillRectangle(48f, 48f, 80f, 48f, fill);
        target.EndEffectCapture();
        target.DrawColorMatrixEffect(
            32f,
            32f,
            112f,
            80f,
            [
                1f, 0f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f, 0f,
                0f, 0f, 1f, 0f, 0f,
                0f, 0f, 0f, 1f, 0f,
            ]);

        // This used to clear() the last PSO ComPtr even though the open D3D12
        // command list referenced it, producing debug-layer error #921.
        target.ReclaimIdleResources();

        Assert.Equal(JaliumResult.Ok, target.RequestReadback());
        Assert.Equal(JaliumResult.Ok, target.TryEndDraw());

        var pixels = new byte[Width * Height * 4];
        Assert.Equal(
            JaliumResult.Ok,
            target.FetchReadback(
                pixels,
                Width * 4u,
                out int capturedWidth,
                out int capturedHeight));
        Assert.True(capturedWidth >= Width && capturedHeight >= Height);

        int offset = (72 * Width + 88) * 4;
        byte blue = pixels[offset];
        byte green = pixels[offset + 1];
        byte red = pixels[offset + 2];
        Assert.True(
            green >= 150 && red <= 90 && blue <= 150,
            $"Expected the color-matrix capture to survive PSO eviction; " +
            $"got BGRA=({blue},{green},{red},{pixels[offset + 3]}).");

        // Recreate the lazily-built PSO, submit it, then evict while that frame
        // is in flight. This is the ordering observed in Jalium.One's idle
        // reclaimer crash rather than the still-open-list variant above.
        Assert.True(target.TryBeginDraw());
        target.Clear(0.02f, 0.04f, 0.03f);
        target.BeginEffectCapture(32f, 32f, 112f, 80f);
        target.FillRectangle(48f, 48f, 80f, 48f, fill);
        target.EndEffectCapture();
        target.DrawColorMatrixEffect(
            32f,
            32f,
            112f,
            80f,
            [
                1f, 0f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f, 0f,
                0f, 0f, 1f, 0f, 0f,
                0f, 0f, 0f, 1f, 0f,
            ]);
        Assert.Equal(JaliumResult.Ok, target.RequestReadback());
        Assert.Equal(JaliumResult.Ok, target.TryEndDraw());

        target.ReclaimIdleResources();

        Assert.Equal(
            JaliumResult.Ok,
            target.FetchReadback(
                pixels,
                Width * 4u,
                out capturedWidth,
                out capturedHeight));
        Assert.True(capturedWidth >= Width && capturedHeight >= Height);
    }
}
