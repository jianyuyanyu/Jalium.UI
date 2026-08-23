using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

/// <summary>
/// GPU pixel regression guards for the damage-scoped partial-frame path of the
/// Vulkan render target (DrawReplayFrame in
/// src/native/jalium.native.vulkan/src/vulkan_render_target.cpp).
///
/// A partial frame (dirty rects, no full invalidation) must
///   1. keep every pixel outside the damage rect from the PREVIOUS frame even
///      though the swap chain rotates through several images — the acquired
///      image is seeded from the retained baseline for exactly the region that
///      went stale since that image was last rendered (per-image content
///      sequence + damage ring), and only the frame's damage rect is captured
///      back;
///   2. render its own draws inside the damage rect (the frame render pass is
///      renderArea-scoped to that rect and every main-target scissor is clamped
///      to it), while a draw that lies entirely outside the damage rect cannot
///      touch retained pixels.
/// Several partial frames in a row cycle through all swap-chain images, so a
/// broken seed (wrong stale region, wrong image bookkeeping) shows up as a
/// stale colour in a region that an earlier partial frame had already updated.
/// </summary>
[Collection("Application")]
public sealed class VulkanPartialFrameRetentionTests
{
    private const int Width = 256;
    private const int Height = 256;

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_PartialFrames_RetainUndamagedPixelsAcrossRotatingSwapchainImages()
    {
        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(
            RenderBackend.Vulkan,
            GpuPreference.Auto,
            RenderingEngine.Impeller);
        using var target = context.CreateRenderTarget(window.Hwnd, Width, Height);
        Assert.Equal(RenderBackend.Vulkan, context.Backend);
        Assert.True(target.IsValid);
        Assert.True(target.SupportsPartialPresentation,
            "swap chain lacks TRANSFER usage — the retained-frame path is not available on this device");

        using var red = context.CreateSolidBrush(1f, 0f, 0f, 1f);
        using var blue = context.CreateSolidBrush(0f, 0f, 1f, 1f);
        using var green = context.CreateSolidBrush(0f, 1f, 0f, 1f);
        using var yellow = context.CreateSolidBrush(1f, 1f, 0f, 1f);
        using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);

        // Frame 0 (full): left half red, right half blue. A hidden window's very
        // first present may be reported as a transient PresentFailed by the
        // WSI (pre-existing, independent of the retention path); the frame is
        // then repeated in full exactly like the host would.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Assert.True(target.TryBeginDraw());
            target.SetFullInvalidation();
            target.Clear(0f, 0f, 0f);
            target.FillRectangle(0f, 0f, 128f, 256f, red);
            target.FillRectangle(128f, 0f, 128f, 256f, blue);
            var r0 = target.TryEndDraw();
            if (r0 == JaliumResult.Ok) break;
            Assert.Equal(JaliumResult.PresentFailed, r0);
        }

        // Frame 1 (partial, damage = right half): repaint the right half green.
        Assert.True(target.TryBeginDraw());
        target.AddDirtyRect(128f, 0f, 128f, 256f);
        target.FillRectangle(128f, 0f, 128f, 256f, green);
        Assert.Equal(JaliumResult.Ok, target.TryEndDraw());

        // Frame 2 (partial, damage = left half): repaint the left half yellow.
        // With three swap-chain images this frame lands on an image that has
        // never been rendered (full seed) or on frame 0's image (stale union =
        // frame 1's right half) — both must yield yellow | green.
        Assert.True(target.TryBeginDraw());
        target.AddDirtyRect(0f, 0f, 128f, 256f);
        target.FillRectangle(0f, 0f, 128f, 256f, yellow);
        Assert.Equal(JaliumResult.Ok, target.TryEndDraw());

        // Frame 3 (partial, damage = centre square): white square, plus a red
        // rect entirely OUTSIDE the damage rect that must be clipped away.
        Assert.True(target.TryBeginDraw());
        target.AddDirtyRect(96f, 96f, 64f, 64f);
        target.FillRectangle(96f, 96f, 64f, 64f, white);
        target.FillRectangle(8f, 200f, 40f, 40f, red);   // outside damage → must not land
        Assert.Equal(JaliumResult.Ok, target.RequestReadback());
        Assert.Equal(JaliumResult.Ok, target.TryEndDraw());

        var pixels = new byte[Width * Height * 4];
        Assert.Equal(
            JaliumResult.Ok,
            target.FetchReadback(pixels, Width * 4u, out var w, out var h));
        Assert.Equal(Width, w);
        Assert.Equal(Height, h);

        // Left half (outside every later damage rect except frame 2): yellow.
        AssertColor(pixels, 32, 32, r: 255, g: 255, b: 0, "left half retained yellow (frame 2)");
        AssertColor(pixels, 32, 220, r: 255, g: 255, b: 0, "left-bottom retained yellow (out-of-damage red rect must be clipped)");
        AssertColor(pixels, 20, 128, r: 255, g: 255, b: 0, "left-middle retained yellow");
        // Right half: green from frame 1, retained through frames 2 and 3.
        AssertColor(pixels, 224, 32, r: 0, g: 255, b: 0, "right half retained green (frame 1)");
        AssertColor(pixels, 200, 220, r: 0, g: 255, b: 0, "right-bottom retained green");
        // Centre square: white from frame 3.
        AssertColor(pixels, 128, 128, r: 255, g: 255, b: 255, "centre square white (frame 3)");
        AssertColor(pixels, 100, 100, r: 255, g: 255, b: 255, "centre square top-left white");
        // Just outside the square, both halves keep their retained colours.
        AssertColor(pixels, 90, 128, r: 255, g: 255, b: 0, "left of square retained yellow");
        AssertColor(pixels, 166, 128, r: 0, g: 255, b: 0, "right of square retained green");
    }

    private static void AssertColor(byte[] pixels, int x, int y, int r, int g, int b, string what)
    {
        int i = (y * Width + x) * 4;
        int pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2];
        Assert.True(Math.Abs(pr - r) <= 3 && Math.Abs(pg - g) <= 3 && Math.Abs(pb - b) <= 3,
            $"{what}: expected ({r},{g},{b}) at ({x},{y}) but got ({pr},{pg},{pb})");
    }
}
