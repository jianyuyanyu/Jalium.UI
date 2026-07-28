namespace Jalium.UI.Media.Imaging;

/// <summary>
/// Centralized checked arithmetic for the framework's packed 32-bpp pixel buffers.
/// Keeping this in one place prevents a large width from wrapping <c>width * 4</c>
/// and turning a rejected layout into a native out-of-bounds read.
/// </summary>
internal static class PixelBufferLayout
{
    internal const int BytesPerPixel = 4;

    internal static int GetMinimumStride(int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        if (width > int.MaxValue / BytesPerPixel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                "Pixel width is too large for a 32-bpp managed buffer.");
        }

        return width * BytesPerPixel;
    }

    internal static int GetRequiredByteCount(int width, int height, int stride)
    {
        int minimumStride = GetMinimumStride(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, minimumStride);

        if (height > int.MaxValue / stride)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                height,
                "Pixel dimensions and stride exceed the maximum managed buffer size.");
        }

        return stride * height;
    }
}
