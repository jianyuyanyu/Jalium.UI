using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

[Collection("Application")]
public sealed class D3D12ResizeScratchTests
{
    private const int WideWidth = 2200;
    private const int NarrowWidth = 640;
    private const int Height = 480;

    private static readonly float[] s_pathCommands =
    [
        0f, 420f, 40f,
        0f, 420f, 300f,
        0f, 40f, 300f,
        5f
    ];

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void Resize_FromUltrawideToNarrow_RebuildsPathScratchAtCurrentViewport()
    {
        using var window = new HiddenNativeWindow(WideWidth, Height);
        using var context = new RenderContext(
            RenderBackend.D3D12,
            GpuPreference.Auto,
            RenderingEngine.Impeller);
        using var brush = context.CreateSolidBrush(0.10f, 0.72f, 0.65f, 1f);
        using var target = context.CreateRenderTarget(
            window.Hwnd,
            WideWidth,
            Height);

        Assert.True(target.IsValid);
        Assert.Equal(RenderingEngine.Impeller, target.RenderingEngine);
        Assert.True(brush.IsValid);

        // Keep the allocation modest for CI while still exercising the same
        // shared color/depth/resolve stencil scratch used by the Gallery.
        target.SetPathMsaaSampleCount(2);
        DrawPathFrame(target, brush);
        Assert.True(target.TryQueryGpuStats(out var wideStats));
        Assert.True(
            wideStats.PathBytes > 0,
            "The wide frame did not allocate D3D12 path scratch resources.");

        Assert.Equal(JaliumResult.Ok, target.Resize(NarrowWidth, Height));
        DrawPathFrame(target, brush);
        Assert.True(target.TryQueryGpuStats(out var narrowStats));

        Assert.True(
            narrowStats.PathBytes * 2 < wideStats.PathBytes,
            $"Path scratch retained its ultrawide high-water mark: " +
            $"wide={wideStats.PathBytes:N0} bytes, " +
            $"narrow={narrowStats.PathBytes:N0} bytes.");
    }

    private static void DrawPathFrame(RenderTarget target, NativeBrush brush)
    {
        Assert.True(target.TryBeginDraw());
        target.Clear(0.06f, 0.08f, 0.09f);
        target.FillPath(20f, 20f, s_pathCommands, brush, fillRule: 1);
        Assert.Equal(JaliumResult.Ok, target.TryEndDraw());
    }
}
