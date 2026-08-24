using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

/// <summary>
/// GPU pixel regression guards for the span-damage narrowing in the Vulkan
/// stencil-then-cover path (RenderEngineBatches in
/// src/native/jalium.native.vulkan/src/vulkan_render_target.cpp) — the Vulkan
/// port of D3D12's <c>computePathRunDamage</c> (see StencilPathDamageScopeTests).
/// Each EngineBatchSpan used to open its offscreen MSAA pass with a full-window
/// renderArea (full-window loadOp CLEAR + full-window auto-resolve) and
/// composite with a full-window scissor; the renderArea/composite scissor are
/// now the union of each record's pixel-space geometry bounds ∩ batch scissor.
/// These tests pin the invariants the narrowing relies on:
///   1. every span still resolves/composites completely (nothing clipped away),
///   2. resolve texels outside one span's renderArea (UNDEFINED after the
///      narrowing) never leak into the composite of that or a later span,
///   3. batch scissors still clip the path exactly as before,
///   4. off-extent, transformed, and granularity-unaligned geometry clamps
///      safely.
/// </summary>
[Collection("Application")]
public sealed class VulkanStencilPathDamageScopeTests
{
    private const int Width = 256;
    private const int Height = 128;

    // Division of labor: this test pins "damage never clips a path away" and
    // the composite copy semantics; the stale-scratch LEAK direction (a span's
    // composite sampling texels its renderArea-scoped clear didn't cover) is
    // only detectable with translucency and is pinned by
    // Vulkan_OverlappingTranslucentPathRuns_CompositeExactlyOnce below.
    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_SeparatedPathRuns_RenderBothAndLeaveGapsClean()
    {
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            // Span 1: small path in the top-left corner.
            target.FillPath(16f, 16f, RectPathCommands(16f, 16f, 48f, 48f), white);
            // A solid rect becomes a replay command, which flushes the pending
            // engine batches into their own EngineBatchSpan — the Vulkan
            // analogue of D3D12's non-path batch splitting two path runs.
            target.FillRectangle(120f, 56f, 8f, 8f, white);
            // Span 2: small path in the bottom-right corner.
            target.FillPath(208f, 80f, RectPathCommands(208f, 80f, 240f, 112f), white);
        });

        Assert.True(GetBlue(pixels, 32, 32) >= 250, $"span-1 path missing: {GetBlue(pixels, 32, 32)}");
        Assert.True(GetBlue(pixels, 224, 96) >= 250, $"span-2 path missing: {GetBlue(pixels, 224, 96)}");
        Assert.True(GetBlue(pixels, 124, 60) >= 250, $"separator rect missing: {GetBlue(pixels, 124, 60)}");

        // Sentinels far away from both paths and the separator must stay
        // background — a mis-sized damage rect that composites stale resolve
        // texels would light one of these up.
        Assert.True(GetBlue(pixels, 200, 20) <= 2, $"sentinel (200,20) polluted: {GetBlue(pixels, 200, 20)}");
        Assert.True(GetBlue(pixels, 20, 100) <= 2, $"sentinel (20,100) polluted: {GetBlue(pixels, 20, 100)}");
        Assert.True(GetBlue(pixels, 120, 20) <= 2, $"sentinel (120,20) polluted: {GetBlue(pixels, 120, 20)}");
        Assert.True(GetBlue(pixels, 60, 90) <= 2, $"sentinel (60,90) polluted: {GetBlue(pixels, 60, 90)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_OverlappingTranslucentPathRuns_CompositeExactlyOnce()
    {
        // Two 50%-alpha white paths in separate spans, overlapping in the
        // middle. Span 2's renderArea-scoped CLEAR only wipes span 2's damage,
        // and its composite scissor must not exceed it: if the composite
        // samples texels the clear didn't cover (span 1 leftovers or UNDEFINED
        // memory), the overlap band brightens past a single extra over-blend.
        var pixels = Render((target, context) =>
        {
            using var half = context.CreateSolidBrush(1f, 1f, 1f, 0.5f);
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            target.FillPath(40f, 24f, RectPathCommands(40f, 24f, 120f, 104f), half);
            // Span separator far from both paths.
            target.FillRectangle(232f, 4f, 8f, 8f, white);
            target.FillPath(80f, 24f, RectPathCommands(80f, 24f, 160f, 104f), half);
        });

        int p1Only = GetBlue(pixels, 60, 64);    // span 1 exclusive
        int p2Only = GetBlue(pixels, 140, 64);   // span 2 exclusive
        int overlap = GetBlue(pixels, 100, 64);  // covered by both
        int gap = GetBlue(pixels, 200, 64);      // covered by neither

        // Both exclusive regions must be a single 0.5-alpha blend over black
        // and identical to each other (span 2 must not re-composite span 1's
        // region either).
        Assert.True(Math.Abs(p1Only - p2Only) <= 6,
            $"exclusive regions diverge: p1={p1Only}, p2={p2Only}");
        Assert.True(p1Only >= 100 && p1Only <= 200,
            $"exclusive region out of range for one 0.5-alpha blend: {p1Only}");

        // One extra over-blend brightens the overlap; exactly one.
        // Single blend: linear 0.75 (=191) or sRGB-encoded 0.75 (=225).
        // Double-composited span-1 leftovers would push this to ≥223 linear /
        // ≥240 sRGB — always ≥ +32 over the correct value in the same space.
        int expectedOverlap = ExpectedSecondBlend(p1Only);
        Assert.True(Math.Abs(overlap - expectedOverlap) <= 14,
            $"overlap composited more than once: overlap={overlap}, expected≈{expectedOverlap} (exclusive={p1Only})");

        Assert.True(gap <= 2, $"gap polluted: {gap}");

        // Span-split canary: the separator rect must have rendered. If span
        // splitting silently stopped (both paths folding into one span), the
        // overlap assertions above would go vacuous — same pixels, wrong
        // mechanism under test.
        Assert.True(GetBlue(pixels, 236, 8) >= 250,
            $"separator rect missing — span split may be broken: {GetBlue(pixels, 236, 8)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_PainterOrderAcrossSpans_ReplayRectStaysOnTopOfEarlierSpan()
    {
        // path1 → red replay rect covering path1's center → path2 far away.
        // The rect must occlude path1: span 1 composites BEFORE the rect
        // draws. If engine batches silently folded to the end of the frame
        // (span split regression), path1 would paint over the rect instead —
        // the ordering canary the pure-overlap tests cannot provide.
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            using var red = context.CreateSolidBrush(1f, 0f, 0f, 1f);
            target.FillPath(40f, 24f, RectPathCommands(40f, 24f, 120f, 104f), white);
            target.FillRectangle(64f, 48f, 32f, 32f, red);
            target.FillPath(208f, 80f, RectPathCommands(208f, 80f, 240f, 112f), white);
        });

        Assert.True(GetBlue(pixels, 80, 64) <= 2,
            $"path 1 painted over the later replay rect (span order broken): blue={GetBlue(pixels, 80, 64)}");
        Assert.True(GetRed(pixels, 80, 64) >= 250,
            $"replay rect missing where it should occlude path 1: red={GetRed(pixels, 80, 64)}");
        Assert.True(GetBlue(pixels, 48, 32) >= 250, $"path 1 outside rect missing: {GetBlue(pixels, 48, 32)}");
        Assert.True(GetBlue(pixels, 224, 96) >= 250, $"path 2 missing: {GetBlue(pixels, 224, 96)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_MixedClipRecordsInOneSpan_PerDrawScissorStillClips()
    {
        // PushClip/PopClip is pure recording state — no replay command, no
        // span split — so BOTH paths land in ONE span with heterogeneous
        // per-record scissors. The span's union damage (and renderArea)
        // stretches far beyond record 1's clip, so only the per-draw batch
        // scissor keeps record 1's out-of-clip geometry dark: this is the
        // case where a "scissor = renderArea" regression escapes every
        // single-record clip test.
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            target.PushClip(0f, 0f, 64f, 128f);
            target.FillPath(32f, 32f, RectPathCommands(32f, 32f, 224f, 96f), white);
            target.PopClip();
            target.FillPath(180f, 40f, RectPathCommands(180f, 40f, 220f, 80f), white);
        });

        Assert.True(GetBlue(pixels, 48, 64) >= 250, $"clipped-in record missing: {GetBlue(pixels, 48, 64)}");
        Assert.True(GetBlue(pixels, 200, 60) >= 250, $"unclipped record missing: {GetBlue(pixels, 200, 60)}");
        // Inside record 1's geometry, outside its clip, inside the span-union
        // renderArea, outside record 2 — only the per-draw scissor keeps it dark.
        Assert.True(GetBlue(pixels, 100, 64) <= 2,
            $"record 1 escaped its clip inside the span renderArea: {GetBlue(pixels, 100, 64)}");
        Assert.True(GetBlue(pixels, 240, 110) <= 2, $"sentinel outside union polluted: {GetBlue(pixels, 240, 110)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_ClippedPath_HonorsClipAfterDamageNarrowing()
    {
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            target.PushClip(0f, 0f, 64f, 128f);
            // Path much wider than the clip: damage = clip ∩ geometry bounds.
            target.FillPath(32f, 32f, RectPathCommands(32f, 32f, 224f, 96f), white);
            target.PopClip();
        });

        Assert.True(GetBlue(pixels, 48, 64) >= 250, $"clipped-in path missing: {GetBlue(pixels, 48, 64)}");
        Assert.True(GetBlue(pixels, 80, 64) <= 2, $"path escaped clip at (80,64): {GetBlue(pixels, 80, 64)}");
        Assert.True(GetBlue(pixels, 200, 64) <= 2, $"path escaped clip at (200,64): {GetBlue(pixels, 200, 64)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_OffscreenSpanningPath_ClampsDamageSafely()
    {
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            // Path straddles the top-left corner; the damage clamp must keep
            // the on-screen part renderable and the rect casts in range.
            target.FillPath(-32f, -32f, RectPathCommands(-32f, -32f, 32f, 32f), white);
        });

        Assert.True(GetBlue(pixels, 8, 8) >= 250, $"on-screen part missing: {GetBlue(pixels, 8, 8)}");
        Assert.True(GetBlue(pixels, 100, 100) <= 2, $"sentinel polluted: {GetBlue(pixels, 100, 100)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_BottomRightSpanningPath_ClampsDamageSafely()
    {
        // Exercises the right/bottom damage clamp (min against the scratch
        // extent), the direction the top-left test cannot cover.
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            target.FillPath(
                Width - 32f, Height - 32f,
                RectPathCommands(Width - 32f, Height - 32f, Width + 32f, Height + 32f),
                white);
        });

        Assert.True(GetBlue(pixels, Width - 8, Height - 8) >= 250,
            $"on-screen part missing: {GetBlue(pixels, Width - 8, Height - 8)}");
        Assert.True(GetBlue(pixels, Width - 100, Height - 100) <= 2,
            $"sentinel polluted: {GetBlue(pixels, Width - 100, Height - 100)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_RotatedPath_RendersFullyInsideTransformedBounds()
    {
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            // 45° rotation around (128,64): the 32×32 rect becomes a diamond.
            // Vulkan stencil contours arrive pre-transformed in pixel space,
            // so the damage AABB is the AABB of the rotated contour — a
            // transform bug would clip the renderArea and hollow the diamond.
            const float c = 0.70710678f;
            float dx = 128f - (128f * c + 64f * -c);
            float dy = 64f - (128f * c + 64f * c);
            target.PushTransform([c, c, -c, c, dx, dy]);
            target.FillPath(112f, 48f, RectPathCommands(112f, 48f, 144f, 80f), white);
            target.PopTransform();
        });

        Assert.True(GetBlue(pixels, 128, 64) >= 250, $"diamond center missing: {GetBlue(pixels, 128, 64)}");
        // Tip probes: a damage rect derived from the UNtransformed local
        // bounds (112..144 × 48..80) would clip the diamond's left/right tips
        // (|x−128| ∈ 17..22.6) while leaving the center probe green.
        Assert.True(GetBlue(pixels, 112, 64) >= 250, $"left tip clipped: {GetBlue(pixels, 112, 64)}");
        Assert.True(GetBlue(pixels, 144, 64) >= 250, $"right tip clipped: {GetBlue(pixels, 144, 64)}");
        Assert.True(GetBlue(pixels, 40, 40) <= 2, $"sentinel (40,40) polluted: {GetBlue(pixels, 40, 40)}");
        Assert.True(GetBlue(pixels, 156, 64) <= 2, $"outside diamond polluted: {GetBlue(pixels, 156, 64)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_FullyOffscreenSpan_SkipsWithoutDisturbingFrame()
    {
        // A span whose only record lies entirely off-extent folds to an empty
        // damage union — RenderEngineBatches now skips its MSAA pass and
        // composite outright (returns false, the designed degenerate-span
        // path). The rest of the frame, including a later normal span, must
        // be byte-identical to the never-drawn scene.
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            // Span 1: fully off-extent path (top-left, never visible).
            target.FillPath(-96f, -96f, RectPathCommands(-96f, -96f, -32f, -32f), white);
            // Separator flushes span 1.
            target.FillRectangle(120f, 56f, 8f, 8f, white);
            // Span 2: normal on-screen path.
            target.FillPath(16f, 16f, RectPathCommands(16f, 16f, 48f, 48f), white);
        });

        Assert.True(GetBlue(pixels, 32, 32) >= 250, $"on-screen span missing: {GetBlue(pixels, 32, 32)}");
        Assert.True(GetBlue(pixels, 124, 60) >= 250, $"separator rect missing: {GetBlue(pixels, 124, 60)}");
        Assert.True(GetBlue(pixels, 4, 4) <= 2, $"origin corner polluted: {GetBlue(pixels, 4, 4)}");
        Assert.True(GetBlue(pixels, 200, 100) <= 2, $"sentinel polluted: {GetBlue(pixels, 200, 100)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_OddCoordinatePath_SurvivesRenderAreaGranularityAlignment()
    {
        // Deliberately granularity-unfriendly rect (odd offsets, odd extent).
        // The renderArea is aligned to vkGetRenderAreaGranularity by rounding
        // the offset down and the extent up — a rounding bug in either
        // direction would clip an edge of the path or shift the composite.
        var pixels = Render((target, context) =>
        {
            using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            target.FillPath(17f, 23f, RectPathCommands(17f, 23f, 49f, 55f), white);
        });

        Assert.True(GetBlue(pixels, 18, 24) >= 250, $"top-left interior missing: {GetBlue(pixels, 18, 24)}");
        Assert.True(GetBlue(pixels, 47, 53) >= 250, $"bottom-right interior missing: {GetBlue(pixels, 47, 53)}");
        Assert.True(GetBlue(pixels, 33, 39) >= 250, $"center missing: {GetBlue(pixels, 33, 39)}");
        Assert.True(GetBlue(pixels, 60, 60) <= 2, $"sentinel (60,60) polluted: {GetBlue(pixels, 60, 60)}");
        Assert.True(GetBlue(pixels, 10, 70) <= 2, $"sentinel (10,70) polluted: {GetBlue(pixels, 10, 70)}");
    }

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_ForcedRenderAreaGranularity_AlignmentDoesNotClipOrShift()
    {
        // Desktop GPUs report a 1×1 render-area granularity, which leaves the
        // alignment math dead in normal runs. JALIUM_VK_FORCE_RENDER_AREA_GRAN
        // (read live via Win32 in the native code) forces a coarse 16×16
        // granularity so the offset-down/extent-up rounding and the
        // framebuffer-edge clamp actually execute. A rounding bug in either
        // direction would clip a path edge or light up the aligned margin.
        Environment.SetEnvironmentVariable("JALIUM_VK_FORCE_RENDER_AREA_GRAN", "16");
        try
        {
            var pixels = Render((target, context) =>
            {
                using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
                // Span 1: granularity-unfriendly odd rect (interior aligned
                // renderArea).
                target.FillPath(17f, 23f, RectPathCommands(17f, 23f, 49f, 55f), white);
                target.FillRectangle(120f, 56f, 8f, 8f, white);
                // Span 2: rect whose aligned renderArea clamps at the
                // bottom-right framebuffer edge.
                target.FillPath(208f, 80f, RectPathCommands(208f, 80f, 240f, 112f), white);
            });

            Assert.True(GetBlue(pixels, 18, 24) >= 250, $"span-1 top-left missing: {GetBlue(pixels, 18, 24)}");
            Assert.True(GetBlue(pixels, 47, 53) >= 250, $"span-1 bottom-right missing: {GetBlue(pixels, 47, 53)}");
            Assert.True(GetBlue(pixels, 224, 96) >= 250, $"span-2 missing: {GetBlue(pixels, 224, 96)}");
            Assert.True(GetBlue(pixels, 239, 111) >= 250, $"span-2 edge pixel missing: {GetBlue(pixels, 239, 111)}");
            // The aligned margin (renderArea ∖ damage) is cleared but must
            // never be composited.
            Assert.True(GetBlue(pixels, 60, 60) <= 2, $"aligned margin polluted: {GetBlue(pixels, 60, 60)}");
            Assert.True(GetBlue(pixels, 200, 20) <= 2, $"sentinel polluted: {GetBlue(pixels, 200, 20)}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("JALIUM_VK_FORCE_RENDER_AREA_GRAN", null);
        }
    }

    /// <summary>
    /// Closed rectangle path commands for <see cref="RenderTarget.FillPath"/>:
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
            RenderBackend.Vulkan,
            GpuPreference.Auto,
            RenderingEngine.Impeller);
        using var target = context.CreateRenderTarget(window.Hwnd, Width, Height);

        // The RenderContext ctor silently falls back when the backend cannot
        // materialize — assert we really got Vulkan.
        Assert.Equal(RenderBackend.Vulkan, context.Backend);
        Assert.True(target.IsValid);

        // Vulkan readback is armed on the second replay frame (see
        // SuperEllipseNativeRenderingTests.Render): render the scene twice and
        // arm inside the last TryBeginDraw..TryEndDraw scope.
        for (var frame = 0; frame < 2; frame++)
        {
            Assert.True(target.TryBeginDraw());
            target.Clear(0f, 0f, 0f);
            draw(target, context);
            if (frame == 1)
            {
                Assert.Equal(JaliumResult.Ok, target.RequestReadback());
            }
            Assert.Equal(JaliumResult.Ok, target.TryEndDraw());
        }

        // Guard against silent downgrade: if solid FillPath ever stops routing
        // through the stencil-then-cover MSAA pipeline (stencil PSO compile
        // failure, gate flipped off), every damage assertion in this class
        // would be exercising the wrong code path while staying green. The
        // MSAA path scratch is allocated by the first stencil-path span, so a
        // non-zero PathBytes proves the pipeline actually ran.
        Assert.True(target.TryQueryGpuStats(out var gpuStats));
        Assert.True(gpuStats.PathBytes > 0,
            "FillPath did not route through the stencil-then-cover MSAA pipeline " +
            "(PathBytes == 0) — these damage tests are not covering the span-damage narrowing.");

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

    private static byte GetRed(byte[] pixels, int x, int y) =>
        pixels[(y * Width + x) * 4 + 2];
}
