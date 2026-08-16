using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

/// <summary>
/// Guards an effected child inside an effected ancestor. D3D12 owns one
/// element-effect capture surface, so the child degrades to pass-through while
/// the ancestor must still close and composite its capture.
/// </summary>
[Collection("Application")]
public sealed class D3D12NestedEffectCaptureTests
{
    private const int Width = 256;
    private const int Height = 224;

    [RequiresBackendFact(RenderBackend.D3D12)]
    public void NestedCaptureFailure_DoesNotDiscardOuterCapture()
    {
        const float outerX = 24f;
        const float outerY = 24f;
        const float outerWidth = 208f;
        const float outerHeight = 176f;

        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(
            RenderBackend.D3D12,
            GpuPreference.Auto,
            RenderingEngine.Impeller);
        using var renderTarget = context.CreateRenderTarget(
            window.Hwnd,
            Width,
            Height);
        using var red = context.CreateSolidBrush(0.95f, 0.05f, 0.05f, 1f);
        using var green = context.CreateSolidBrush(0.05f, 0.9f, 0.1f, 1f);

        Assert.Equal(RenderBackend.D3D12, context.Backend);
        Assert.True(renderTarget.IsValid);
        Assert.True(red.IsValid);
        Assert.True(green.IsValid);

        Assert.True(renderTarget.TryBeginDraw());
        renderTarget.Clear(0.02f, 0.03f, 0.04f);

        renderTarget.BeginEffectCapture(
            outerX,
            outerY,
            outerWidth,
            outerHeight);

        renderTarget.BeginEffectCapture(56f, 56f, 72f, 64f);
        renderTarget.FillRectangle(72f, 72f, 32f, 28f, red);
        renderTarget.EndEffectCapture();
        renderTarget.DrawDropShadowEffect(
            56f,
            56f,
            72f,
            64f,
            blurRadius: 8f,
            offsetX: 0f,
            offsetY: 2f,
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0.25f);

        // Every element-effect dispatcher sees the same failed nested scope.
        // They must all no-op without sampling slot 0, which still belongs to
        // the open ancestor capture. ColorMatrix and Emboss historically tried
        // to composite that live RTV as an SRV and could corrupt the outer card.
        renderTarget.DrawBlurEffect(56f, 56f, 72f, 64f, 3f);
        renderTarget.DrawOuterGlowEffect(
            56f, 56f, 72f, 64f,
            glowSize: 5f,
            r: 0f, g: 1f, b: 0f, a: 0.5f, intensity: 1f);
        renderTarget.DrawInnerShadowEffect(
            56f, 56f, 72f, 64f,
            blurRadius: 4f,
            offsetX: 1f,
            offsetY: 1f,
            r: 0f, g: 0f, b: 0f, a: 0.4f);
        renderTarget.DrawColorMatrixEffect(
            56f, 56f, 72f, 64f,
            [
                1f, 0f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f, 0f,
                0f, 0f, 1f, 0f, 0f,
                0f, 0f, 0f, 1f, 0f,
            ]);
        renderTarget.DrawEmbossEffect(
            56f, 56f, 72f, 64f,
            amount: 1f,
            lightDirX: 0.7f,
            lightDirY: -0.7f,
            relief: 1f);
        renderTarget.DrawShaderEffect(
            56f, 56f, 72f, 64f,
            shaderBytecode: [0, 1, 2, 3],
            constants: []);
        renderTarget.DrawShaderEffectFromSource(
            56f, 56f, 72f, 64f,
            "float4 main(float2 uv : TEXCOORD) : SV_Target { return float4(1,0,1,1); }",
            []);

        // This marker is emitted after the nested scope. Both markers must be
        // present when the outer scope is ended and composited.
        renderTarget.FillRectangle(160f, 128f, 32f, 28f, green);

        renderTarget.EndEffectCapture();
        renderTarget.DrawDropShadowEffect(
            outerX,
            outerY,
            outerWidth,
            outerHeight,
            blurRadius: 12f,
            offsetX: 0f,
            offsetY: 4f,
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0f);

        Assert.Equal(JaliumResult.Ok, renderTarget.RequestReadback());
        Assert.Equal(JaliumResult.Ok, renderTarget.TryEndDraw());

        var pixels = new byte[Width * Height * 4];
        Assert.Equal(
            JaliumResult.Ok,
            renderTarget.FetchReadback(
                pixels,
                Width * 4u,
                out int capturedWidth,
                out int capturedHeight));
        Assert.True(capturedWidth >= Width && capturedHeight >= Height);

        AssertMarker(pixels, 88, 86, expectRed: true);
        AssertMarker(pixels, 176, 142, expectRed: false);
    }

    private static void AssertMarker(
        byte[] pixels,
        int x,
        int y,
        bool expectRed)
    {
        int offset = (y * Width + x) * 4;
        byte blue = pixels[offset];
        byte green = pixels[offset + 1];
        byte red = pixels[offset + 2];

        bool matches = expectRed
            ? red >= 180 && green <= 80 && blue <= 80
            : green >= 160 && red <= 80 && blue <= 80;
        Assert.True(
            matches,
            $"Expected {(expectRed ? "red" : "green")} marker at ({x},{y}); " +
            $"got BGRA=({blue},{green},{red},{pixels[offset + 3]}).");
    }
}
