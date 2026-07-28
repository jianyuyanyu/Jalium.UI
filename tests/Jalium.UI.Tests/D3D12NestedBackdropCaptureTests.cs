using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

/// <summary>
/// Guards immediate backdrop draws nested inside an element-effect capture.
/// The backdrop samples the swap-chain image, but its output and all following
/// element content must stay in the active offscreen render target.
/// </summary>
[Collection("Application")]
public sealed class D3D12NestedBackdropCaptureTests
{
    private const int Width = 256;
    private const int Height = 256;

    [RequiresBackendFact(RenderBackend.D3D12)]
    public void BackdropInsideDropShadowCapture_KeepsFollowingContentAtSurfacePosition()
    {
        const float captureX = 32f;
        const float captureY = 48f;
        const float captureWidth = 160f;
        const float captureHeight = 96f;

        const float markerX = 152f;
        const float markerY = 80f;
        const float markerWidth = 24f;
        const float markerHeight = 32f;

        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(RenderBackend.D3D12);
        Assert.Equal(RenderBackend.D3D12, context.Backend);

        using var renderTarget = context.CreateRenderTarget(window.Hwnd, Width, Height);
        Assert.True(renderTarget.IsValid);

        using var marker = context.CreateSolidBrush(0.95f, 0.05f, 0.05f, 1f);
        Assert.True(marker.IsValid);

        var pixels = new byte[Width * Height * 4];

        Assert.True(renderTarget.TryBeginDraw());
        renderTarget.Clear(0.08f, 0.12f, 0.16f);

        renderTarget.BeginEffectCapture(
            captureX,
            captureY,
            captureWidth,
            captureHeight);

        renderTarget.DrawBackdropFilterEx(
            captureX,
            captureY,
            captureWidth,
            captureHeight,
            backdropFilter: string.Empty,
            material: string.Empty,
            materialTint: "#202830",
            materialTintOpacity: 0.85f,
            materialBlurRadius: 0f,
            noiseIntensity: 0f,
            saturation: 1f,
            luminosity: 1f,
            cornerRadiusTL: 0f,
            cornerRadiusTR: 0f,
            cornerRadiusBR: 0f,
            cornerRadiusBL: 0f);

        // This draw is intentionally after the immediate backdrop pass. If that
        // pass leaks the swap-chain RTV/viewport into the surrounding capture,
        // the marker is emitted in capture-local coordinates onto the main RTV.
        renderTarget.FillRectangle(
            markerX,
            markerY,
            markerWidth,
            markerHeight,
            marker);

        renderTarget.EndEffectCapture();
        renderTarget.DrawDropShadowEffect(
            captureX,
            captureY,
            captureWidth,
            captureHeight,
            blurRadius: 12f,
            offsetX: 0f,
            offsetY: 4f,
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0f);

        Assert.Equal(JaliumResult.Ok, renderTarget.RequestReadback());
        Assert.Equal(JaliumResult.Ok, renderTarget.TryEndDraw());
        Assert.Equal(
            JaliumResult.Ok,
            renderTarget.FetchReadback(
                pixels,
                (uint)(Width * 4),
                out int capturedWidth,
                out int capturedHeight));
        Assert.True(capturedWidth >= Width && capturedHeight >= Height);

        int sampleX = (int)(markerX + markerWidth / 2);
        int sampleY = (int)(markerY + markerHeight / 2);
        int offset = (sampleY * Width + sampleX) * 4;
        byte blue = pixels[offset];
        byte green = pixels[offset + 1];
        byte red = pixels[offset + 2];

        Assert.True(
            red >= 180 && green <= 80 && blue <= 80,
            $"Expected the post-backdrop marker at ({sampleX},{sampleY}); " +
            $"got BGRA=({blue},{green},{red},{pixels[offset + 3]}).");
    }
}
