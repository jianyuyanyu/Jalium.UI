using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

/// <summary>
/// Guards Impeller path clips recorded inside a D3D12 element-effect capture.
/// Capture-local clips must not have the capture origin subtracted a second
/// time when pending Impeller batches are replayed into the offscreen target.
/// </summary>
[Collection("Application")]
public sealed class D3D12EffectCapturePathClipTests
{
    private const int Width = 1280;
    private const int Height = 256;

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void CaptureLocalClip_KeepsStrokePathsAcrossTheFullCaptureWidth()
    {
        const float captureX = 240f;
        const float captureY = 24f;
        const float captureWidth = 960f;
        const float captureHeight = 192f;
        float[] strokeXs = [320f, 560f, 800f, 1040f];

        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(
            RenderBackend.D3D12,
            GpuPreference.Auto,
            RenderingEngine.Impeller);
        using var target = context.CreateRenderTarget(window.Hwnd, Width, Height);
        using var green = context.CreateSolidBrush(0.05f, 0.95f, 0.10f, 1f);

        Assert.Equal(RenderBackend.D3D12, context.Backend);
        Assert.True(target.IsValid);
        Assert.True(green.IsValid);
        Assert.True(target.TryBeginDraw());

        target.Clear(0f, 0f, 0f);
        target.BeginEffectCapture(
            captureX,
            captureY,
            captureWidth,
            captureHeight);

        // This clip is pushed after the offscreen redirect. DirectRenderer
        // therefore stores it in capture-local coordinates. The former replay
        // path subtracted captureX/captureY once more, moving the right edge
        // from local x=920 to x=680 and clipping the fourth stroke entirely.
        target.PushClip(280f, 40f, 880f, 160f);
        foreach (float x in strokeXs)
        {
            target.StrokePath(
                x,
                80f,
                RectangleCommands(x, 80f, x + 16f, 96f),
                green,
                strokeWidth: 2f,
                closed: true);

            // Force the lazy Impeller stroke batch to replay while the
            // capture-local clip and offscreen render target are both active.
            target.FillRectangle(captureX + 2f, captureY + 2f, 1f, 1f, green);
        }
        target.PopClip();

        target.EndEffectCapture();
        target.DrawDropShadowEffect(
            captureX,
            captureY,
            captureWidth,
            captureHeight,
            blurRadius: 8f,
            offsetX: 0f,
            offsetY: 2f,
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0f);

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
        Assert.Equal(Width, capturedWidth);
        Assert.Equal(Height, capturedHeight);

        foreach (float x in strokeXs)
        {
            AssertStrokePresent(pixels, (int)x, 80);
        }
    }

    private static float[] RectangleCommands(
        float x0,
        float y0,
        float x1,
        float y1) =>
    [
        0f, x1, y0,
        0f, x1, y1,
        0f, x0, y1,
        5f,
    ];

    private static void AssertStrokePresent(byte[] pixels, int x, int y)
    {
        int greenPixels = 0;
        for (int py = y - 2; py <= y + 18; py++)
        {
            for (int px = x - 2; px <= x + 18; px++)
            {
                int offset = (py * Width + px) * 4;
                byte blue = pixels[offset];
                byte green = pixels[offset + 1];
                byte red = pixels[offset + 2];
                if (green >= 140 && red <= 100 && blue <= 100)
                {
                    greenPixels++;
                }
            }
        }

        Assert.True(
            greenPixels >= 8,
            $"Expected captured green stroke near ({x},{y}); found {greenPixels} matching pixels.");
    }
}
