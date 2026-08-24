using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

[Collection("Application")]
public sealed class D3D12ResizeScratchTests
{
    private const int WideWidth = 2200;
    private const int NarrowWidth = 640;
    private const int Height = 480;

    // 600×450 device-space bounds from the (20,20) start point. The size is
    // load-bearing: D3D12RenderTarget::FillPath only takes the stencil-then-cover
    // route — the one that allocates the path scratch this test measures — for a
    // solid fill whose bounds fail PreferAnalyticFill (jalium_flatten.h, 512×512
    // px of area). Anything smaller goes to the analytic scanline rasterizer and
    // leaves PathBytes at 0. It also has to fit the NARROW viewport, so the same
    // path keeps allocating scratch after the resize.
    private static readonly float[] s_pathCommands =
    [
        0f, 620f, 20f,
        0f, 620f, 470f,
        0f, 20f, 470f,
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
