using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;

namespace Jalium.UI.Tests;

/// <summary>
/// Pins the reclaim/restore round-trip on <see cref="BitmapImage"/>. The idle
/// resource reclaimer drops the decoded pixel buffer of off-screen images; before
/// <c>TryRestorePixelData</c> existed, every later GPU cache miss re-ran a full
/// native decode on the render thread and permanently bypassed the downscale
/// cache (which requires <see cref="BitmapImage.RawPixelData"/>). Scrolling a
/// catalog of images past the bitmap cache made that the steady state.
/// </summary>
public sealed class BitmapImageReclaimRestoreTests
{
    private sealed class CountingDecoder : INativeImageDecoder
    {
        public int DecodeCalls;
        public int Width = 4;
        public int Height = 3;
        public byte Fill = 0x7A;

        public DecodedImage Decode(ReadOnlySpan<byte> data, NativePixelFormat requestedFormat = NativePixelFormat.Bgra8)
        {
            DecodeCalls++;
            var stride = Width * 4;
            var buffer = new byte[stride * Height];
            Array.Fill(buffer, Fill);
            return new DecodedImage(buffer, Width, Height, stride, requestedFormat);
        }

        public DecodedImage Decode(Stream stream, NativePixelFormat requestedFormat = NativePixelFormat.Bgra8)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Decode(ms.ToArray(), requestedFormat);
        }

        public DecodedImage DecodeFile(string filePath, NativePixelFormat requestedFormat = NativePixelFormat.Bgra8)
            => Decode(File.ReadAllBytes(filePath), requestedFormat);

        public bool TryReadDimensions(ReadOnlySpan<byte> data, out int width, out int height)
        {
            width = Width;
            height = Height;
            return true;
        }
    }

    [Fact]
    public void ReclaimedPixelsAreRestoredFromEncodedBytes()
    {
        var decoder = new CountingDecoder { Width = 8, Height = 6, Fill = 0x5C };
        BitmapImage.SetDecoder(decoder);

        var image = BitmapImage.FromBytes([0x89, 0x50, 0x4E, 0x47]);
        Assert.Equal(1, decoder.DecodeCalls);
        Assert.NotNull(image.RawPixelData);

        image.ReclaimIdleResources();
        Assert.Null(image.RawPixelData);

        Assert.True(image.TryRestorePixelData());
        Assert.Equal(2, decoder.DecodeCalls);
        Assert.NotNull(image.RawPixelData);
        Assert.Equal(8, image.PixelWidth);
        Assert.Equal(6, image.PixelHeight);
        Assert.Equal(8 * 4, image.PixelStride);
        Assert.All(image.RawPixelData!, b => Assert.Equal(0x5C, b));

        // Restoring again must not re-decode — the buffer is already back.
        Assert.True(image.TryRestorePixelData());
        Assert.Equal(2, decoder.DecodeCalls);
    }

    [Fact]
    public void RestoreIsNoOpWhilePixelsAreStillPresent()
    {
        var decoder = new CountingDecoder();
        BitmapImage.SetDecoder(decoder);

        var image = BitmapImage.FromBytes([0x89, 0x50, 0x4E, 0x47]);
        Assert.Equal(1, decoder.DecodeCalls);

        Assert.True(image.TryRestorePixelData());

        Assert.Equal(1, decoder.DecodeCalls);
    }

    [Fact]
    public void PixelOnlyImageKeepsItsBufferAndCannotRestore()
    {
        var decoder = new CountingDecoder();
        BitmapImage.SetDecoder(decoder);

        // No encoded bytes exist for a pixel-sourced bitmap, so reclamation must
        // leave the pixels alone — dropping them would lose the image for good.
        var pixels = new byte[8 * 8 * 4];
        Array.Fill(pixels, (byte)0x33);
        var image = BitmapImage.FromPixels(pixels, 8, 8);

        image.ReclaimIdleResources();

        Assert.NotNull(image.RawPixelData);
        Assert.All(image.RawPixelData!, b => Assert.Equal(0x33, b));
        Assert.Equal(0, decoder.DecodeCalls);

        // Already-present pixels short-circuit the restore path.
        Assert.True(image.TryRestorePixelData());
        Assert.Equal(0, decoder.DecodeCalls);
    }
}
