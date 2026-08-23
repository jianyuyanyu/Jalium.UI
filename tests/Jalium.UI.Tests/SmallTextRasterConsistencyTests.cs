using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

/// <summary>
/// Pins the small-text rasterization contract that the glyph atlases share.
///
/// The reported symptom was "small text goes blurry, and some characters come
/// out bigger than others". The cause is sub-pixel positioning: the atlas kept
/// 8 phase variants per glyph, and at grid-fit sizes DirectWrite rasterizes a
/// measurably different bitmap per phase — a 12 ppem Latin glyph spans 4 px in
/// some phases and 5 px in others, with total ink varying by ~9 %. Running text
/// puts consecutive characters in different phases (advances are fractional),
/// so identical characters rendered visibly unlike each other, and the
/// half-covered edge columns that carry the difference are what read as blur.
///
/// The atlas now pins phase 0 whenever the font's gasp table asks for grid
/// fitting at that ppem, so every instance of a character shares one bitmap.
/// Above the grid-fit range the phases stay, since symmetric antialiasing
/// renders them consistently and exact spacing is worth more there.
///
/// Note this asserts the CONSISTENCY half of the fix, which is the part that is
/// deterministic. The companion change — letting the gasp table pick the
/// rendering mode instead of pinning NATURAL_SYMMETRIC — is a correctness fix
/// whose pixel effect is small at these sizes and too font-dependent to assert
/// without writing a test that passes either way.
/// </summary>
[Collection("Application")]
public sealed class SmallTextRasterConsistencyTests
{
    private const int Width = 512;
    private const int Height = 96;

    // Repeats are space-separated so each one lands in a different sub-pixel
    // phase (the space advance is fractional) AND so the column grouping below
    // can tell them apart — packed tightly, adjacent glyphs share ink columns
    // at these sizes and read as a single run.
    private const string RepeatedLatin = "i i i i i i";
    private const string RepeatedCjk = "国 国 国 国 国";

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_SmallText_RendersEveryInstanceOfACharacterIdentically() =>
        AssertRepeatedGlyphsAreIdentical(RenderBackend.D3D12);

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_SmallText_RendersEveryInstanceOfACharacterIdentically() =>
        AssertRepeatedGlyphsAreIdentical(RenderBackend.Vulkan);

    /// <summary>
    /// Every repeat of the same character must carry the same amount of ink.
    /// This is a hard equality rather than a tolerance: with phase 0 pinned,
    /// all instances share one atlas bitmap, so any difference means the
    /// sub-pixel phases came back.
    /// </summary>
    private static void AssertRepeatedGlyphsAreIdentical(RenderBackend backend)
    {
        foreach (var (text, size) in new[]
                 {
                     (RepeatedLatin, 12f),
                     (RepeatedCjk, 12f),
                     (RepeatedLatin, 9f),
                 })
        {
            using var window = new HiddenNativeWindow(Width, Height);
            using var context = new RenderContext(backend);
            using var target = context.CreateRenderTarget(window.Hwnd, Width, Height);
            using var brush = context.CreateSolidBrush(1f, 1f, 1f, 1f);
            using var format = context.CreateTextFormat("Microsoft YaHei UI", size);

            var pixels = Render(target, brush, format, text);
            var columns = InkPerColumn(pixels);
            var glyphs = SplitIntoGlyphs(columns);

            Assert.True(
                glyphs.Count >= 3,
                $"{backend} {size}px '{text}': expected the repeats to render as " +
                $"separate ink groups, found {glyphs.Count}.");

            // The first and last group can be clipped by the layout box; compare
            // the interior ones, which are unambiguously whole glyphs.
            var interior = glyphs.GetRange(1, glyphs.Count - 2);
            var reference = interior[0];
            foreach (var glyph in interior)
            {
                Assert.True(
                    glyph.Ink == reference.Ink && glyph.Width == reference.Width,
                    $"{backend} {size}px '{text}': repeats of the same character " +
                    $"rasterized differently — reference ink={reference.Ink} " +
                    $"width={reference.Width}, saw ink={glyph.Ink} width={glyph.Width}. " +
                    "Sub-pixel phases are being used at a grid-fit size.");
            }
        }
    }

    private static byte[] Render(
        RenderTarget target, NativeBrush brush, NativeTextFormat format, string text)
    {
        target.SetDpi(96f, 96f);
        for (var frame = 0; frame < 2; frame++)
        {
            target.SetFullInvalidation();
            Assert.True(
                TryBeginDrawWithRetry(target),
                $"{target.Backend}: TryBeginDraw remained busy on frame {frame}.");
            target.Clear(0f, 0f, 0f);
            target.DrawText(text, format, 8f, 8f, Width - 16f, Height - 16f, brush);
            if (frame == 1)
            {
                Assert.Equal(JaliumResult.Ok, target.RequestReadback());
            }
            Assert.Equal(JaliumResult.Ok, target.TryEndDraw());
        }

        var pixels = new byte[Width * Height * 4];
        Assert.Equal(
            JaliumResult.Ok,
            target.FetchReadback(pixels, Width * 4u, out var w, out var h));
        Assert.Equal(Width, w);
        Assert.Equal(Height, h);
        return pixels;
    }

    private static int[] InkPerColumn(byte[] pixels)
    {
        var columns = new int[Width];
        for (var x = 0; x < Width; x++)
        {
            var sum = 0;
            for (var y = 0; y < Height; y++)
            {
                var o = (y * Width + x) * 4;
                sum += Math.Max(pixels[o], Math.Max(pixels[o + 1], pixels[o + 2]));
            }
            columns[x] = sum;
        }
        return columns;
    }

    /// Split the column profile into runs of inked columns separated by blank
    /// ones. For a string of repeated characters each run is one glyph.
    private static List<(int Ink, int Width)> SplitIntoGlyphs(int[] columns)
    {
        var glyphs = new List<(int, int)>();
        var runInk = 0;
        var runWidth = 0;
        foreach (var column in columns)
        {
            if (column > 0)
            {
                runInk += column;
                runWidth++;
            }
            else if (runWidth > 0)
            {
                glyphs.Add((runInk, runWidth));
                runInk = 0;
                runWidth = 0;
            }
        }
        if (runWidth > 0)
        {
            glyphs.Add((runInk, runWidth));
        }
        return glyphs;
    }

    private static bool TryBeginDrawWithRetry(RenderTarget target)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            if (target.TryBeginDraw())
            {
                return true;
            }
            Thread.Sleep(1);
        }
        while (stopwatch.ElapsedMilliseconds < 250);
        return false;
    }
}
