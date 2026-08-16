using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

[Collection("Application")]
public sealed class SoftwareEffectCaptureTests
{
    private const int Width = 128;
    private const int Height = 128;

    [Fact]
    public void NestedEffectCapture_RestoresOuterFramebufferAndCapturedContent()
    {
        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(RenderBackend.Software);
        using var renderTarget = context.CreateRenderTarget(window.Hwnd, Width, Height);
        using var red = context.CreateSolidBrush(0.95f, 0.05f, 0.05f, 1f);
        using var green = context.CreateSolidBrush(0.05f, 0.9f, 0.1f, 1f);

        Assert.True(renderTarget.TryBeginDraw());
        renderTarget.Clear(0.04f, 0.06f, 0.08f);

        renderTarget.BeginEffectCapture(8f, 8f, 112f, 112f);
        renderTarget.BeginEffectCapture(20f, 20f, 36f, 36f);
        renderTarget.FillRectangle(28f, 28f, 16f, 14f, red);
        renderTarget.EndEffectCapture();
        renderTarget.DrawBlurEffect(20f, 20f, 36f, 36f, 0f);

        renderTarget.FillRectangle(78f, 76f, 18f, 16f, green);
        renderTarget.EndEffectCapture();
        renderTarget.DrawBlurEffect(8f, 8f, 112f, 112f, 0f);

        var pixels = FinishAndRead(renderTarget);
        AssertColor(pixels, 34, 34, expectRed: true);
        AssertColor(pixels, 86, 82, expectRed: false);

        // Empty pixels inside the outer capture remain the opaque frame clear.
        // The old scalar capture state left this region transparent because the
        // inner Begin overwrote the outer scope's saved framebuffer.
        int empty = PixelOffset(62, 62);
        Assert.Equal(255, pixels[empty + 3]);
        Assert.InRange(pixels[empty], 15, 26);
    }

    [Fact]
    public void PaddedCapture_PassThroughReturnsContentToOriginalCoordinates()
    {
        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(RenderBackend.Software);
        using var renderTarget = context.CreateRenderTarget(window.Hwnd, Width, Height);
        using var red = context.CreateSolidBrush(0.95f, 0.05f, 0.05f, 1f);

        Assert.True(renderTarget.TryBeginDraw());
        renderTarget.Clear(0.04f, 0.06f, 0.08f);

        // Capture origin is ten pixels above/left of the element, matching the
        // managed EffectPadding + uvOffset contract.
        renderTarget.BeginEffectCapture(10f, 10f, 50f, 50f);
        renderTarget.FillRectangle(20f, 20f, 14f, 12f, red);
        renderTarget.EndEffectCapture();
        renderTarget.DrawBlurEffect(
            20f, 20f, 30f, 30f,
            radius: 0f,
            uvOffsetX: 10f,
            uvOffsetY: 10f);

        var pixels = FinishAndRead(renderTarget);
        AssertColor(pixels, 26, 25, expectRed: true);

        // The old code added uvOffset to the destination even though the
        // captured pixels already contained that margin, shifting by 2*padding.
        int shifted = PixelOffset(46, 45);
        Assert.True(pixels[shifted + 2] < 100);
    }

    [Theory]
    [InlineData(EffectDispatcher.Blur)]
    [InlineData(EffectDispatcher.DropShadow)]
    [InlineData(EffectDispatcher.OuterGlow)]
    [InlineData(EffectDispatcher.InnerShadow)]
    [InlineData(EffectDispatcher.ColorMatrix)]
    [InlineData(EffectDispatcher.Emboss)]
    [InlineData(EffectDispatcher.ShaderBytecode)]
    [InlineData(EffectDispatcher.ShaderSource)]
    public void EveryEffectDispatcher_CompositesCapturedContent(EffectDispatcher dispatcher)
    {
        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(RenderBackend.Software);
        using var renderTarget = context.CreateRenderTarget(window.Hwnd, Width, Height);
        using var red = context.CreateSolidBrush(0.95f, 0.05f, 0.05f, 1f);

        Assert.True(renderTarget.TryBeginDraw());
        renderTarget.Clear(0.04f, 0.06f, 0.08f);

        renderTarget.BeginEffectCapture(32f, 32f, 64f, 64f);
        renderTarget.FillRectangle(48f, 48f, 20f, 18f, red);
        renderTarget.EndEffectCapture();

        DispatchEffect(renderTarget, dispatcher);

        var pixels = FinishAndRead(renderTarget);
        int center = PixelOffset(56, 56);
        byte blue = pixels[center];
        byte green = pixels[center + 1];
        byte redChannel = pixels[center + 2];
        byte alpha = pixels[center + 3];

        Assert.True(
            alpha >= 200 && Math.Max(redChannel, Math.Max(green, blue)) >= 80,
            $"{dispatcher} lost its captured content; BGRA=({blue},{green},{redChannel},{alpha}).");
    }

    private static void DispatchEffect(RenderTarget renderTarget, EffectDispatcher dispatcher)
    {
        const float x = 48f;
        const float y = 48f;
        const float width = 20f;
        const float height = 18f;
        const float padding = 16f;

        switch (dispatcher)
        {
            case EffectDispatcher.Blur:
                renderTarget.DrawBlurEffect(x, y, width, height, 0f, padding, padding);
                break;
            case EffectDispatcher.DropShadow:
                renderTarget.DrawDropShadowEffect(
                    x, y, width, height,
                    blurRadius: 0f, offsetX: 0f, offsetY: 0f,
                    r: 0f, g: 0f, b: 0f, a: 0f,
                    uvOffsetX: padding, uvOffsetY: padding);
                break;
            case EffectDispatcher.OuterGlow:
                renderTarget.DrawOuterGlowEffect(
                    x, y, width, height,
                    glowSize: 1f,
                    r: 0f, g: 1f, b: 0f, a: 0f, intensity: 1f,
                    uvOffsetX: padding, uvOffsetY: padding);
                break;
            case EffectDispatcher.InnerShadow:
                renderTarget.DrawInnerShadowEffect(
                    x, y, width, height,
                    blurRadius: 0f, offsetX: 0f, offsetY: 0f,
                    r: 0f, g: 0f, b: 0f, a: 0f,
                    uvOffsetX: padding, uvOffsetY: padding);
                break;
            case EffectDispatcher.ColorMatrix:
                renderTarget.DrawColorMatrixEffect(
                    x, y, width, height,
                    [
                        1f, 0f, 0f, 0f, 0f,
                        0f, 1f, 0f, 0f, 0f,
                        0f, 0f, 1f, 0f, 0f,
                        0f, 0f, 0f, 1f, 0f,
                    ]);
                break;
            case EffectDispatcher.Emboss:
                renderTarget.DrawEmbossEffect(
                    x, y, width, height,
                    amount: 1f, lightDirX: 0.7f, lightDirY: -0.7f, relief: 1f);
                break;
            case EffectDispatcher.ShaderBytecode:
                renderTarget.DrawShaderEffect(
                    x, y, width, height,
                    shaderBytecode: [0, 1, 2, 3],
                    constants: []);
                break;
            case EffectDispatcher.ShaderSource:
                renderTarget.DrawShaderEffectFromSource(
                    x, y, width, height,
                    "float4 main(float2 uv : TEXCOORD) : SV_Target { return float4(1,0,1,1); }",
                    []);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dispatcher), dispatcher, null);
        }
    }

    private static byte[] FinishAndRead(RenderTarget renderTarget)
    {
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
        Assert.Equal(Width, capturedWidth);
        Assert.Equal(Height, capturedHeight);
        return pixels;
    }

    private static void AssertColor(byte[] pixels, int x, int y, bool expectRed)
    {
        int offset = PixelOffset(x, y);
        byte blue = pixels[offset];
        byte green = pixels[offset + 1];
        byte red = pixels[offset + 2];
        bool matches = expectRed
            ? red >= 180 && green <= 80 && blue <= 80
            : green >= 160 && red <= 80 && blue <= 80;
        Assert.True(matches, $"Unexpected BGRA at ({x},{y}): ({blue},{green},{red},{pixels[offset + 3]}).");
    }

    private static int PixelOffset(int x, int y) => (y * Width + x) * 4;

    public enum EffectDispatcher
    {
        Blur,
        DropShadow,
        OuterGlow,
        InnerShadow,
        ColorMatrix,
        Emboss,
        ShaderBytecode,
        ShaderSource,
    }
}
