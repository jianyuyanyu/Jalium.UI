using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

/// <summary>
/// GPU pixel regression guards for the geometry-bounds path-run damage in
/// <c>computePathRunDamage</c> (src/native/jalium.native.d3d12/src/d3d12_direct_renderer.cpp).
/// An un-clipped stencil-path batch used to force its whole run's MSAA scratch
/// clear + resolve + blit to the full content extent; on a promoted full frame
/// (scrolling) that meant window-area bandwidth per path run. The damage rect is
/// now the union of each draw's device-space geometry bounds (∩ batch scissor),
/// so these tests pin the invariants the narrowing relies on:
///   1. every path still resolves/blits completely (nothing clipped away),
///   2. scratch texels outside one run's damage never leak into a later run
///      (the later run's clear must cover everything its resolve reads),
///   3. batch scissors still clip the path exactly as before,
///   4. off-extent and transformed geometry clamp safely.
///
/// GEOMETRY SIZE IS LOAD-BEARING. <c>D3D12RenderTarget::FillPath</c> only takes
/// the stencil-then-cover route for a solid fill whose device-space bounds fail
/// <c>PreferAnalyticFill</c> (jalium_flatten.h): at or under 512×512 px of area
/// the fill goes to the analytic scanline rasterizer instead, which never
/// allocates the MSAA scratch and never reaches <c>computePathRunDamage</c>.
/// Every path below is therefore deliberately larger than that gate — icon-scale
/// geometry here would leave the whole class green while covering nothing (which
/// is exactly what the <c>PathBytes</c> guard in <see cref="Render"/> catches).
/// </summary>
[Collection("Application")]
public sealed class StencilPathDamageScopeTests
{
    private const int Width = 1280;
    private const int Height = 768;

    // Division of labor: this test pins "damage never clips a path away" and
    // the blit copy semantics; the stale-scratch LEAK direction (a run's
    // resolve reading texels its clear didn't cover) is only detectable with
    // translucency and is pinned by
    // D3D12_OverlappingTranslucentPathRuns_CompositeExactlyOnce below.
    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_SeparatedPathRuns_RenderBothAndLeaveGapsClean()
    {
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            // Run 1: 640×640 block on the left.
            target.FillPath(32f, 32f, RectPathCommands(32f, 32f, 672f, 672f), white);
            // Non-path batch splits the two paths into separate runs. It sits in
            // the 32px corridor between them, clear of both.
            target.FillRectangle(688f, 16f, 8f, 8f, white);
            // Run 2: 544×640 block on the right, offset down so the corners of
            // the frame stay background.
            target.FillPath(704f, 96f, RectPathCommands(704f, 96f, 1248f, 736f), white);
        });

        Assert.True(GetBlue(pixels, 350, 350) >= 250, $"run-1 path missing: {GetBlue(pixels, 350, 350)}");
        Assert.True(GetBlue(pixels, 976, 416) >= 250, $"run-2 path missing: {GetBlue(pixels, 976, 416)}");
        Assert.True(GetBlue(pixels, 692, 20) >= 250, $"separator rect missing: {GetBlue(pixels, 692, 20)}");

        // Sentinels far away from both paths and the separator must stay
        // background — a mis-sized damage rect that resolves stale scratch
        // texels would light one of these up.
        Assert.True(GetBlue(pixels, 900, 40) <= 2, $"sentinel (900,40) polluted: {GetBlue(pixels, 900, 40)}");
        Assert.True(GetBlue(pixels, 16, 720) <= 2, $"sentinel (16,720) polluted: {GetBlue(pixels, 16, 720)}");
        Assert.True(GetBlue(pixels, 1264, 24) <= 2, $"sentinel (1264,24) polluted: {GetBlue(pixels, 1264, 24)}");
        Assert.True(GetBlue(pixels, 350, 720) <= 2, $"sentinel (350,720) polluted: {GetBlue(pixels, 350, 720)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_OverlappingTranslucentPathRuns_CompositeExactlyOnce()
    {
        // Two 50%-alpha white paths in separate runs, overlapping in the middle.
        // Run 2's damage-scoped clear must wipe run 1's leftovers from the MSAA
        // scratch before run 2 rasterizes: if it doesn't, run 2's resolve+blit
        // re-composites run 1's coverage a second time and the overlap band
        // brightens well past a single over-blend.
        var pixels = Render((target, context) =>
        {
            using var half = context.CreateSolidBrush(1f, 1f, 1f, 0.5f);
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            target.FillPath(32f, 64f, RectPathCommands(32f, 64f, 736f, 704f), half);
            // Run separator, clear of both paths.
            target.FillRectangle(620f, 8f, 8f, 8f, white);
            target.FillPath(544f, 64f, RectPathCommands(544f, 64f, 1248f, 704f), half);
        });

        int p1Only = GetBlue(pixels, 200, 384);   // run 1 exclusive
        int p2Only = GetBlue(pixels, 1080, 384);  // run 2 exclusive
        int overlap = GetBlue(pixels, 640, 384);  // covered by both
        int gap = GetBlue(pixels, 640, 740);      // covered by neither

        // Both exclusive regions must be a single 0.5-alpha blend over black and
        // identical to each other (run 2 must not re-blit run 1's region either).
        Assert.True(Math.Abs(p1Only - p2Only) <= 6,
            $"exclusive regions diverge: p1={p1Only}, p2={p2Only}");
        Assert.True(p1Only >= 100 && p1Only <= 200,
            $"exclusive region out of range for one 0.5-alpha blend: {p1Only}");

        // One extra over-blend brightens the overlap; exactly one.
        // Single blend: linear 0.75 (=191) or sRGB-encoded 0.75 (=225).
        // Double-composited run-1 leftovers would push this to ≥223 linear /
        // ≥240 sRGB — always ≥ +32 over the correct value in the same space.
        int expectedOverlap = ExpectedSecondBlend(p1Only);
        Assert.True(Math.Abs(overlap - expectedOverlap) <= 14,
            $"overlap composited more than once: overlap={overlap}, expected≈{expectedOverlap} (exclusive={p1Only})");

        Assert.True(gap <= 2, $"gap polluted: {gap}");
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_ClippedPath_HonorsClipAfterDamageNarrowing()
    {
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            target.PushClip(0f, 0f, 256f, Height);
            // Path much wider than the clip: damage = clip ∩ geometry bounds.
            target.FillPath(32f, 64f, RectPathCommands(32f, 64f, 1248f, 704f), white);
            target.PopClip();
        });

        Assert.True(GetBlue(pixels, 128, 384) >= 250, $"clipped-in path missing: {GetBlue(pixels, 128, 384)}");
        Assert.True(GetBlue(pixels, 400, 384) <= 2, $"path escaped clip at (400,384): {GetBlue(pixels, 400, 384)}");
        Assert.True(GetBlue(pixels, 1000, 384) <= 2, $"path escaped clip at (1000,384): {GetBlue(pixels, 1000, 384)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_OffscreenSpanningPath_ClampsDamageSafely()
    {
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            // Path straddles the top-left corner; the damage clamp must keep
            // the on-screen part renderable and the rect casts in range.
            target.FillPath(
                -Width / 2f, -Height / 2f,
                RectPathCommands(-Width / 2f, -Height / 2f, Width / 2f, Height / 2f),
                white);
        });

        Assert.True(GetBlue(pixels, 8, 8) >= 250, $"on-screen part missing: {GetBlue(pixels, 8, 8)}");
        Assert.True(GetBlue(pixels, 900, 600) <= 2, $"sentinel polluted: {GetBlue(pixels, 900, 600)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_BottomRightSpanningPath_ClampsDamageSafely()
    {
        // Exercises the right/bottom damage clamp (min against the content
        // extent), the direction the top-left test cannot cover.
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            target.FillPath(
                Width / 2f, Height / 2f,
                RectPathCommands(Width / 2f, Height / 2f, Width * 1.5f, Height * 1.5f),
                white);
        });

        Assert.True(GetBlue(pixels, Width - 8, Height - 8) >= 250,
            $"on-screen part missing: {GetBlue(pixels, Width - 8, Height - 8)}");
        Assert.True(GetBlue(pixels, 100, 100) <= 2, $"sentinel (100,100) polluted: {GetBlue(pixels, 100, 100)}");
        Assert.True(GetBlue(pixels, 560, 600) <= 2, $"sentinel (560,600) polluted: {GetBlue(pixels, 560, 600)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_RotatedPath_RendersFullyInsideTransformedBounds()
    {
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            // 45° rotation around (640,384): the 520×520 rect becomes a diamond
            // whose vertices sit 368px from the centre along both axes. The
            // damage AABB comes from transforming the local bounds, so a
            // transform bug would clip the resolve and hollow out the diamond.
            const float c = 0.70710678f;
            float dx = 640f - (640f * c + 384f * -c);
            float dy = 384f - (640f * c + 384f * c);
            target.PushTransform([c, c, -c, c, dx, dy]);
            target.FillPath(380f, 124f, RectPathCommands(380f, 124f, 900f, 644f), white);
            target.PopTransform();
        });

        Assert.True(GetBlue(pixels, 640, 384) >= 250, $"diamond center missing: {GetBlue(pixels, 640, 384)}");
        Assert.True(GetBlue(pixels, 200, 120) <= 2, $"sentinel (200,120) polluted: {GetBlue(pixels, 200, 120)}");
        Assert.True(GetBlue(pixels, 1060, 384) <= 2, $"outside diamond polluted: {GetBlue(pixels, 1060, 384)}");
    }

    /// <summary>
    /// Closed rectangle path commands for <see cref="RenderTarget.FillPath(float, float, float[], NativeBrush, int, int)"/>:
    /// the caller passes (x0, y0) as the start point, these walk the remaining
    /// corners. Tags: 0 = LineTo, 5 = ClosePath (jalium_triangulate.h).
    /// </summary>
    private static float[] RectPathCommands(float x0, float y0, float x1, float y1) =>
    [
        0f, x1, y0,
        0f, x1, y1,
        0f, x0, y1,
        5f,
    ];

    /// <summary>
    /// Expected value of a second 0.5-alpha white over-blend given the observed
    /// single-blend value, tolerant of the swap chain being linear or
    /// sRGB-encoded: overlap = encode(decode(single) + 0.5 · (1 − decode(single))).
    /// </summary>
    private static int ExpectedSecondBlend(int singleBlend)
    {
        // Treat the observed single blend as the source of truth for the
        // transfer curve: linear 0.5 → 128, sRGB 0.5 → 188.
        double s = singleBlend / 255.0;
        bool looksSrgb = singleBlend > 160;
        double linearSingle = looksSrgb ? SrgbToLinear(s) : s;
        double linearOverlap = linearSingle + 0.5 * (1.0 - linearSingle);
        double encoded = looksSrgb ? LinearToSrgb(linearOverlap) : linearOverlap;
        return (int)Math.Round(encoded * 255.0);
    }

    private static double SrgbToLinear(double v) =>
        v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double v) =>
        v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;

    private static byte[] Render(Action<RenderTarget, RenderContext> draw)
    {
        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(
            RenderBackend.D3D12,
            GpuPreference.Auto,
            RenderingEngine.Impeller);
        using var target = context.CreateRenderTarget(window.Hwnd, Width, Height);

        Assert.Equal(RenderBackend.D3D12, context.Backend);
        Assert.True(target.IsValid);

        // The scratch is viewport-sized per sample; 2× keeps this frame's
        // allocation modest on CI while running the identical stencil-then-cover
        // clear / resolve / blit that the damage rect scopes. Every assertion
        // below samples deep inside or far outside a shape, so the coverage
        // quantization the sample count controls cannot move them.
        target.SetPathMsaaSampleCount(2);

        Assert.True(target.TryBeginDraw());
        target.Clear(0f, 0f, 0f);
        draw(target, context);
        Assert.Equal(JaliumResult.Ok, target.RequestReadback());
        Assert.Equal(JaliumResult.Ok, target.TryEndDraw());

        // Guard against silent downgrade: if solid FillPath ever stops routing
        // through the stencil-then-cover MSAA pipeline (stencil PSO compile
        // failure, a tightened PreferAnalyticFill area gate, analytic-only
        // default flip), every damage assertion in this class would be
        // exercising the wrong code path while staying green. The MSAA path
        // scratch is allocated on first stencil-path flush, so a non-zero
        // PathBytes proves the pipeline actually ran.
        Assert.True(target.TryQueryGpuStats(out var gpuStats));
        Assert.True(gpuStats.PathBytes > 0,
            "FillPath did not route through the stencil-then-cover MSAA pipeline " +
            "(PathBytes == 0) — these damage tests are not covering computePathRunDamage. " +
            "Check the path geometry still exceeds PreferAnalyticFill's area gate.");

        var pixels = new byte[Width * Height * 4];
        Assert.Equal(
            JaliumResult.Ok,
            target.FetchReadback(
                pixels,
                Width * 4u,
                out var capturedWidth,
                out var capturedHeight));
        Assert.Equal(Width, capturedWidth);
        Assert.Equal(Height, capturedHeight);
        return pixels;
    }

    private static byte GetBlue(byte[] pixels, int x, int y) =>
        pixels[(y * Width + x) * 4];
}
