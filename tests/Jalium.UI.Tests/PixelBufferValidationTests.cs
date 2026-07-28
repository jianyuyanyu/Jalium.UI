using Jalium.UI.Interop;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Media.Native;

namespace Jalium.UI.Tests;

public sealed class PixelBufferValidationTests
{
    [Fact]
    public void BitmapImage_FromPixels_RejectsStrideSmallerThanOneRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BitmapImage.FromPixels(new byte[4], width: 2, height: 1, stride: 4));
    }

    [Fact]
    public void BitmapImage_FromPixels_RejectsRowByteOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BitmapImage.FromPixels(new byte[1], int.MaxValue, height: 1, stride: 1));
    }

    [Fact]
    public void DecodedImage_RejectsTruncatedPixelBuffer()
    {
        Assert.Throws<ArgumentException>(
            () => new DecodedImage(
                new byte[4],
                width: 2,
                height: 2,
                stride: 8,
                NativePixelFormat.Bgra8));
    }

    [Fact]
    public void MediaFramePool_RejectsRowByteOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DefaultMediaFramePool.Shared.Rent(
                int.MaxValue,
                height: 1,
                stride: 1,
                TimeSpan.Zero));
    }

    [Fact]
    public void NativeBitmapAbi_RejectsWrappedRowStride()
    {
        using var context = new RenderContext(RenderBackend.Software);
        nint bitmap = NativeMethods.BitmapCreateFromPixels(
            context.Handle,
            new byte[1],
            width: 0x4000_0000u,
            height: 1,
            stride: 1);

        try
        {
            Assert.Equal(nint.Zero, bitmap);
        }
        finally
        {
            if (bitmap != nint.Zero)
            {
                NativeMethods.BitmapDestroy(bitmap);
            }
        }
    }

    [Fact]
    public void NativeVideoSurfaceAbi_RejectsWrappedPackedSize()
    {
        using var context = new RenderContext(RenderBackend.Software);
        nint surface = NativeVideoSurfaceInterop.Create(
            context.Handle,
            width: 0x4000_0000u,
            height: 0x4000_0000u,
            formatHint: 0);

        try
        {
            Assert.Equal(nint.Zero, surface);
        }
        finally
        {
            if (surface != nint.Zero)
            {
                NativeVideoSurfaceInterop.Destroy(surface);
            }
        }
    }

    [Fact]
    public void RenderTargetBitmap_RejectsWrappedManagedBufferLayout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RenderTargetBitmap(
                (int.MaxValue / PixelBufferLayout.BytesPerPixel) + 2,
                1,
                96,
                96,
                PixelFormat.Bgra32));
    }

    [Fact]
    public void RenderTargetBitmap_CopyPixelsRejectsTruncatedDestinationRows()
    {
        var bitmap = new RenderTargetBitmap(2, 1, 96, 96, PixelFormat.Bgra32);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => bitmap.CopyPixels(
                new Int32Rect(0, 0, 2, 1),
                new byte[8],
                stride: 4,
                offset: 0));
    }
}
