using Jalium.UI;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;

namespace Jalium.UI.Tests;

public sealed class WriteableBitmapSafetyTests
{
    [Fact]
    public void Constructor_RejectsOverflowingManagedBufferLayout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WriteableBitmap(
                int.MaxValue,
                2,
                96,
                96,
                PixelFormat.Bgra32,
                palette: null));
    }

    [Fact]
    public void WritePixels_RejectsRectangleCoordinateOverflow()
    {
        var bitmap = new WriteableBitmap(2, 2, 96, 96, PixelFormat.Bgra32, palette: null);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => bitmap.WritePixels(
                new Int32Rect(int.MaxValue, 0, 2, 1),
                new byte[8],
                stride: 8,
                offset: 0));
    }

    [Fact]
    public void WritePixels_RejectsOverflowingSourceStrideLayout()
    {
        var bitmap = new WriteableBitmap(1, 2, 96, 96, PixelFormat.Bgra32, palette: null);

        Assert.Throws<ArgumentException>(
            () => bitmap.WritePixels(
                new Int32Rect(0, 0, 1, 2),
                new byte[8],
                stride: int.MaxValue,
                offset: 0));
    }

    [Fact]
    public void BitmapSourceConstructor_PreservesHighDpiPixelDimensionsAndPayload()
    {
        byte[] sourcePixels =
        [
            1, 2, 3, 4,
            5, 6, 7, 8,
        ];
        var source = BitmapSource.Create(
            pixelWidth: 2,
            pixelHeight: 1,
            dpiX: 192,
            dpiY: 96,
            PixelFormat.Bgra32,
            palette: null,
            sourcePixels,
            stride: 8);

        Assert.Equal(1.0, source.Width);
        var bitmap = new WriteableBitmap(source);
        var copied = new byte[sourcePixels.Length];
        bitmap.CopyPixels(copied, stride: 8, offset: 0);

        Assert.Equal(2, bitmap.PixelWidth);
        Assert.Equal(1, bitmap.PixelHeight);
        Assert.Equal(192, bitmap.DpiX);
        Assert.Equal(sourcePixels, copied);
    }

    [Fact]
    public void UnmanagedWritePixels_RejectsNegativeBufferSizeBeforeCopy()
    {
        var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormat.Bgra32, palette: null);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => bitmap.WritePixels(
                new Int32Rect(0, 0, 1, 1),
                (nint)1,
                bufferSize: -1,
                stride: 4));
    }
}
