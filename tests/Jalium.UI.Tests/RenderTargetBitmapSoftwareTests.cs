using System.IO;
using System.IO.Compression;
using System.Reflection;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Shapes;

namespace Jalium.UI.Tests;

/// <summary>
/// Pins the offscreen software raster path (<see cref="RenderTargetBitmap.Render(Visual)"/> plus
/// its internal <c>SoftwareDrawingContext</c>) against the two distortions that made headless
/// control snapshots untrustworthy: child arrange offsets were dropped entirely, and every brush
/// was slammed into the buffer with no alpha compositing and no gradient support.
/// </summary>
public class RenderTargetBitmapSoftwareTests
{
    private const int Bpp = 4;

    private static readonly Color Background = Color.FromArgb(255, 0, 0, 0);

    private static (byte B, byte G, byte R, byte A) PixelAt(byte[] pixels, int stride, int x, int y)
    {
        var offset = (y * stride) + (x * Bpp);
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    private static byte[] RenderToPixels(Visual visual, int width, int height, Color clearColor, out int stride)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormat.Bgra32);
        target.Clear(clearColor);
        target.Render(visual);

        stride = width * Bpp;
        var pixels = new byte[stride * height];
        target.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        return pixels;
    }

    private static Canvas BuildCanvasWithOffsetChild(Brush fill)
    {
        var child = new Rectangle { Width = 10, Height = 10, Fill = fill };
        Canvas.SetLeft(child, 20);
        Canvas.SetTop(child, 10);

        var canvas = new Canvas { Width = 40, Height = 40 };
        canvas.Children.Add(child);
        canvas.Measure(new Size(40, 40));
        canvas.Arrange(new Rect(0, 0, 40, 40));
        return canvas;
    }

    [Fact]
    public void Render_ChildArrangeOffset_LandsAtArrangedPosition()
    {
        var canvas = BuildCanvasWithOffsetChild(new SolidColorBrush(Color.FromArgb(255, 255, 0, 0)));

        var pixels = RenderToPixels(canvas, 40, 40, Background, out var stride);

        // The child was arranged at (20, 10) and is 10x10; its centre is (25, 15).
        var inside = PixelAt(pixels, stride, 25, 15);
        Assert.Equal((byte)255, inside.R);
        Assert.Equal((byte)0, inside.G);
        Assert.Equal((byte)0, inside.B);

        // Where the child would land if the arrange offset were dropped (0, 0).
        var atOrigin = PixelAt(pixels, stride, 5, 5);
        Assert.Equal((byte)0, atOrigin.R);
        Assert.Equal((byte)0, atOrigin.G);
        Assert.Equal((byte)0, atOrigin.B);
    }

    [Fact]
    public void Render_TranslucentBrush_BlendsOverBackground()
    {
        var rect = new Rectangle
        {
            Width = 20,
            Height = 20,
            Fill = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)),
        };
        rect.Measure(new Size(20, 20));
        rect.Arrange(new Rect(0, 0, 20, 20));

        var pixels = RenderToPixels(rect, 20, 20, Color.FromArgb(255, 0, 0, 0), out var stride);

        var blended = PixelAt(pixels, stride, 10, 10);
        Assert.Equal((byte)255, blended.A);
        // 50% white over opaque black ≈ mid grey, definitely NOT pure white.
        Assert.InRange(blended.R, 112, 143);
        Assert.InRange(blended.G, 112, 143);
        Assert.InRange(blended.B, 112, 143);
    }

    [Fact]
    public void Render_TransparentBrush_LeavesBackgroundUntouched()
    {
        var grid = new Grid { Width = 20, Height = 20, Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)) };
        grid.Measure(new Size(20, 20));
        grid.Arrange(new Rect(0, 0, 20, 20));

        var pixels = RenderToPixels(grid, 20, 20, Color.FromArgb(255, 10, 20, 30), out var stride);

        var untouched = PixelAt(pixels, stride, 10, 10);
        Assert.Equal((byte)10, untouched.R);
        Assert.Equal((byte)20, untouched.G);
        Assert.Equal((byte)30, untouched.B);
        Assert.Equal((byte)255, untouched.A);
    }

    [Fact]
    public void Render_LinearGradientBrush_ProducesHorizontalRamp()
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
        };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 1));

        var rect = new Rectangle { Width = 40, Height = 10, Fill = gradient };
        rect.Measure(new Size(40, 10));
        rect.Arrange(new Rect(0, 0, 40, 10));

        var pixels = RenderToPixels(rect, 40, 10, Color.FromArgb(255, 255, 0, 0), out var stride);

        var left = PixelAt(pixels, stride, 2, 5);
        var middle = PixelAt(pixels, stride, 20, 5);
        var right = PixelAt(pixels, stride, 37, 5);

        Assert.True(left.R < 40, $"left end should be near-black, got {left.R}");
        Assert.True(right.R > 215, $"right end should be near-white, got {right.R}");
        Assert.True(left.R < middle.R && middle.R < right.R, "the ramp must increase left to right");
        // A gradient must not degenerate into a flat fill of one stop colour.
        Assert.True(right.R - left.R > 150);
    }

    [Fact]
    public void Render_ChildOpacity_IsComposited()
    {
        var child = new Rectangle
        {
            Width = 20,
            Height = 20,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            Opacity = 0.5,
        };
        var host = new Grid { Width = 20, Height = 20 };
        host.Children.Add(child);
        host.Measure(new Size(20, 20));
        host.Arrange(new Rect(0, 0, 20, 20));

        var pixels = RenderToPixels(host, 20, 20, Color.FromArgb(255, 0, 0, 0), out var stride);

        var blended = PixelAt(pixels, stride, 10, 10);
        Assert.InRange(blended.R, 112, 143);
    }

    [Fact]
    public void Render_RoundedRectangle_CarvesItsCorners()
    {
        var rect = new Rectangle
        {
            Width = 24,
            Height = 24,
            RadiusX = 12,
            RadiusY = 12,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
        };
        rect.Measure(new Size(24, 24));
        rect.Arrange(new Rect(0, 0, 24, 24));

        var pixels = RenderToPixels(rect, 24, 24, Color.FromArgb(255, 0, 0, 0), out var stride);

        Assert.Equal((byte)255, PixelAt(pixels, stride, 12, 12).R);
        Assert.True(PixelAt(pixels, stride, 0, 0).R < 40, "the top-left corner must stay outside a full-radius round rect");
        Assert.True(PixelAt(pixels, stride, 23, 23).R < 40, "the bottom-right corner must stay outside a full-radius round rect");
    }

    [Fact]
    public void Render_ClipToBounds_ClipsChildrenToTheHost()
    {
        var child = new Rectangle { Width = 40, Height = 40, Fill = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)) };
        Canvas.SetLeft(child, 10);
        Canvas.SetTop(child, 10);

        var canvas = new Canvas { Width = 20, Height = 20, ClipToBounds = true };
        canvas.Children.Add(child);
        canvas.Measure(new Size(20, 20));
        canvas.Arrange(new Rect(0, 0, 20, 20));

        var pixels = RenderToPixels(canvas, 40, 40, Color.FromArgb(255, 0, 0, 0), out var stride);

        Assert.Equal((byte)255, PixelAt(pixels, stride, 15, 15).R);
        Assert.True(PixelAt(pixels, stride, 30, 30).R < 40, "content past the ClipToBounds host must be clipped away");
    }

    [Fact]
    public void PngBitmapEncoder_Save_WritesADecodablePng()
    {
        var target = new RenderTargetBitmap(4, 2, 96, 96, PixelFormat.Bgra32);
        target.Clear(Color.FromArgb(255, 10, 20, 30));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        var bytes = stream.ToArray();

        Assert.True(bytes.Length > 0, "PngBitmapEncoder.Save wrote nothing at all");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        // IHDR: length(4) type(4) then width/height big-endian.
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(4, (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19]);
        Assert.Equal(2, (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]);
        Assert.Equal(8, bytes[24]);  // bit depth
        Assert.Equal(6, bytes[25]);  // colour type: truecolour + alpha

        var idat = ExtractChunk(bytes, "IDAT");
        Assert.NotNull(idat);
        using var deflated = new ZLibStream(new MemoryStream(idat!), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        deflated.CopyTo(raw);
        var scanlines = raw.ToArray();

        // 2 rows of (1 filter byte + 4 px * RGBA).
        Assert.Equal(2 * (1 + (4 * 4)), scanlines.Length);
        Assert.Equal(0, scanlines[0]);
        Assert.Equal(10, scanlines[1]);   // R
        Assert.Equal(20, scanlines[2]);   // G
        Assert.Equal(30, scanlines[3]);   // B
        Assert.Equal(255, scanlines[4]);  // A

        Assert.NotNull(ExtractChunk(bytes, "IEND"));
    }

    [Fact]
    public void BitmapEncoder_Create_OnlyResolvesPng()
    {
        Assert.IsType<PngBitmapEncoder>(
            BitmapEncoder.Create(new Guid("1b7cfaf4-713f-473c-bbcd-6137425faeaf")));

        // BMP / GIF / JPEG / TIFF / WMP used to resolve to shells whose Save wrote nothing.
        foreach (var containerFormat in new[]
                 {
                     "0af1d87e-fcfe-4188-bdeb-a7906471cbe3", // BMP
                     "1f8a5601-7d4d-4cbd-9c82-1bc8d4eeb9a5", // GIF
                     "19e4a5aa-5662-4fc5-a0c0-1758028e1057", // JPEG
                     "163bcc30-e2e9-4f0b-961d-a3e9fdb788a3", // TIFF
                     "57a37caa-367a-4540-916b-f183c5093a4b", // WMP
                 })
        {
            Assert.Throws<NotSupportedException>(() => BitmapEncoder.Create(new Guid(containerFormat)));
        }
    }

    [Fact]
    public void PngBitmapEncoder_WithoutFrames_Throws()
    {
        using var stream = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => new PngBitmapEncoder().Save(stream));
    }

    private static byte[]? ExtractChunk(byte[] png, string type)
    {
        var position = 8;
        while (position + 8 <= png.Length)
        {
            var length = (png[position] << 24) | (png[position + 1] << 16) | (png[position + 2] << 8) | png[position + 3];
            var chunkType = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
            if (chunkType == type)
            {
                return png.AsSpan(position + 8, length).ToArray();
            }

            position += 12 + length;
        }

        return null;
    }
}

/// <summary>
/// The end-to-end control snapshot the software path exists for: a themed, continuous-mode
/// <see cref="Slider"/> must come out of <see cref="RenderTargetBitmap"/> with its track
/// vertically centred, its thumb parked at the value, and its track / fill in visibly
/// different colours (the theme paints them with a translucent brush and a gradient).
/// </summary>
[Collection("Application")]
public class RenderTargetBitmapSliderSnapshotTests
{
    private const int Width = 260;
    private const int Height = 32;
    private const int Bpp = 4;

    private static void ResetApplicationState()
    {
        typeof(Application)
            .GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, null);

        typeof(Jalium.UI.Controls.Themes.ThemeManager)
            .GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)
            ?.Invoke(null, null);
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(byte[] pixels, int x, int y)
    {
        var offset = (y * Width * Bpp) + (x * Bpp);
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    private static bool IsBackground((byte B, byte G, byte R, byte A) pixel) =>
        pixel.R == 24 && pixel.G == 24 && pixel.B == 24;

    /// <summary>
    /// Headless layout settle. <c>Slider.UpdateSliderLayout</c> writes the thumb's Margin and the
    /// selection range's Width from <c>OnSizeChanged</c>, i.e. AFTER the arrange that produced that
    /// size. Those writes call <c>InvalidateMeasure</c> on the template PARTS, and the queue that
    /// turns that into a re-layout lives on the window's LayoutManager — which a detached tree has
    /// no access to. Re-measuring the root alone is not enough either: <c>Measure</c> short-circuits
    /// on a still-valid intermediate element and never reaches the invalidated part. So invalidate
    /// the whole subtree explicitly before the final pass.
    /// </summary>
    private static void InvalidateSubtree(Visual visual)
    {
        if (visual is UIElement element)
        {
            element.InvalidateMeasure();
            element.InvalidateArrange();
        }

        var count = VisualTreeHelper.GetChildrenCount(visual);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(visual, i) is Visual child) InvalidateSubtree(child);
        }
    }

    [Fact]
    public void ContinuousSlider_RendersCentredTrackWithDistinctFill()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            Assert.True(app.Resources.TryGetValue(typeof(Slider), out var styleObj));
            var slider = new Slider
            {
                Style = (Style)styleObj!,
                Minimum = 0,
                Maximum = 100,
                Value = 50,
                Width = Width,
                Height = Height,
            };

            var host = new Border { Width = Width, Height = Height };
            host.Child = slider;

            host.Measure(new Size(Width, Height));
            host.Arrange(new Rect(0, 0, Width, Height));
            slider.ApplyTemplate();
            InvalidateSubtree(host);
            host.Measure(new Size(Width, Height));
            host.Arrange(new Rect(0, 0, Width, Height));

            // Value=50% of a 260-wide slider with a 16px thumb: (260-16) * 0.5 = 122.
            var thumb = (FrameworkElement)slider.GetTemplateChild("PART_Thumb")!;
            var selectionRange = (FrameworkElement)slider.GetTemplateChild("PART_SelectionRange")!;
            Assert.Equal(122.0, thumb.VisualBounds.X);
            Assert.Equal(122.0, selectionRange.VisualBounds.Width);

            var target = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormat.Bgra32);
            target.Clear(Color.FromArgb(255, 24, 24, 24));
            target.Render(host);

            var pixels = new byte[Width * Height * Bpp];
            target.CopyPixels(new Int32Rect(0, 0, Width, Height), pixels, Width * Bpp, 0);

            // 1. The 4px track sits on the vertical centre, not at the top of the image.
            Assert.False(IsBackground(PixelAt(pixels, 200, Height / 2)), "the track must be painted on the centre row");
            Assert.True(IsBackground(PixelAt(pixels, 200, 2)), "nothing may be painted on the top row");

            // 2. Filled range (left of the thumb) and bare track differ in colour.
            var filled = PixelAt(pixels, 40, Height / 2);
            var bare = PixelAt(pixels, 220, Height / 2);
            var delta = Math.Abs(filled.R - bare.R) + Math.Abs(filled.G - bare.G) + Math.Abs(filled.B - bare.B);
            Assert.True(delta > 24, $"track and fill must be distinguishable, got filled={filled}, bare={bare}, delta={delta}");

            // 3. The thumb follows Value=50%, it does not stay pinned at x=0.
            var thumbCentre = Width / 2;
            Assert.False(IsBackground(PixelAt(pixels, thumbCentre, Height / 2)), "the thumb must be painted near the middle");
            Assert.True(IsBackground(PixelAt(pixels, 1, 4)), "the thumb must not be parked in the top-left corner");
        }
        finally
        {
            ResetApplicationState();
        }
    }
}
