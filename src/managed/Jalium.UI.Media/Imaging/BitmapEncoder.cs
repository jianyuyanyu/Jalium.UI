using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Compression;
using Jalium.UI.Threading;

namespace Jalium.UI.Media.Imaging;

/// <summary>
/// Encodes a collection of BitmapFrame objects to an image stream.
/// </summary>
/// <remarks>
/// <see cref="PngBitmapEncoder"/> is the only implementation. The BMP / GIF / JPEG / TIFF / WMP
/// classes that used to sit alongside it were empty shells whose <c>Save</c> wrote nothing and
/// returned, handing callers a zero-byte file; they have been removed rather than kept as
/// throwing stubs. <see cref="Create(Guid)"/> reports any other container format as unsupported.
/// </remarks>
public abstract class BitmapEncoder : DispatcherObject
{
    private static readonly Guid PngContainerFormat = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");

    private IList<BitmapFrame> _frames = new List<BitmapFrame>();

    /// <summary>
    /// Gets the collection of frames in this encoder.
    /// </summary>
    public virtual IList<BitmapFrame> Frames
    {
        get => _frames;
        set => _frames = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets the codec info for this encoder.
    /// </summary>
    public virtual BitmapCodecInfo? CodecInfo => null;

    /// <summary>
    /// Gets or sets the color profile associated with this encoder.
    /// </summary>
    public virtual ReadOnlyCollection<ColorContext>? ColorContexts { get; set; }

    /// <summary>Gets or sets container metadata.</summary>
    public virtual BitmapMetadata? Metadata { get; set; }

    /// <summary>
    /// Gets or sets the bitmap palette.
    /// </summary>
    public virtual BitmapPalette? Palette { get; set; }

    /// <summary>
    /// Gets or sets the preview thumbnail.
    /// </summary>
    public virtual BitmapSource? Preview { get; set; }

    /// <summary>
    /// Gets or sets the thumbnail for the bitmap.
    /// </summary>
    public virtual BitmapSource? Thumbnail { get; set; }

    /// <summary>
    /// Encodes <see cref="Frames"/> to the specified stream.
    /// </summary>
    public abstract void Save(Stream stream);

    /// <summary>Creates an encoder for a WIC container format GUID.</summary>
    /// <exception cref="NotSupportedException">No encoder exists for the requested format.</exception>
    public static BitmapEncoder Create(Guid containerFormat)
    {
        if (containerFormat == PngContainerFormat)
        {
            return new PngBitmapEncoder();
        }

        throw new NotSupportedException(
            $"No bitmap encoder is registered for container format '{containerFormat}'. " +
            $"PNG ('{PngContainerFormat}') is the only supported format.");
    }
}

/// <summary>
/// Defines an encoder that is used to encode PNG format images.
/// </summary>
/// <remarks>
/// Writes 8-bit truecolour-with-alpha (colour type 6), non-interlaced, with every scanline on
/// filter type 0. PNG only needs zlib and CRC-32, both of which are available in the BCL, so this
/// codec has no native dependency and works headless — which is what makes
/// <see cref="RenderTargetBitmap"/> usable as a snapshot tool in tests.
/// </remarks>
public sealed class PngBitmapEncoder : BitmapEncoder
{
    private const byte ColorTypeTruecolorAlpha = 6;

    /// <inheritdoc />
    public override void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (Frames.Count == 0)
        {
            throw new InvalidOperationException("The encoder has no frames to save.");
        }

        var frame = Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The frame has no pixels to encode.");
        }

        var rgba = ReadFrameAsRgba(frame, width, height);

        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), (uint)height);
        header[8] = 8;                          // bit depth
        header[9] = ColorTypeTruecolorAlpha;    // colour type
        header[10] = 0;                         // compression: deflate
        header[11] = 0;                         // filter method: adaptive
        header[12] = 0;                         // interlace: none
        WriteChunk(stream, "IHDR", header);

        WriteChunk(stream, "IDAT", Deflate(rgba, width, height));
        WriteChunk(stream, "IEND", ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Reads the frame into tightly packed, top-down, straight-alpha RGBA.</summary>
    private static byte[] ReadFrameAsRgba(BitmapFrame frame, int width, int height)
    {
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        frame.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);

        var format = frame.Format;
        if (format == PixelFormat.Rgba32)
        {
            return pixels;
        }

        if (format != PixelFormat.Bgra32 && format != PixelFormat.Pbgra32 && format != PixelFormat.Rgb32)
        {
            throw new NotSupportedException($"PngBitmapEncoder cannot encode pixel format '{format}'.");
        }

        var premultiplied = format == PixelFormat.Pbgra32;
        var opaque = format == PixelFormat.Rgb32;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            var a = opaque ? (byte)255 : pixels[i + 3];

            if (premultiplied && a is > 0 and < 255)
            {
                r = (byte)Math.Min(255, r * 255 / a);
                g = (byte)Math.Min(255, g * 255 / a);
                b = (byte)Math.Min(255, b * 255 / a);
            }

            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return pixels;
    }

    /// <summary>Prefixes each scanline with filter type 0 and zlib-compresses the result.</summary>
    private static byte[] Deflate(byte[] rgba, int width, int height)
    {
        var rowBytes = width * 4;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var filterByte = new byte[1];
            for (var y = 0; y < height; y++)
            {
                zlib.Write(filterByte);
                zlib.Write(rgba, y * rowBytes, rowBytes);
            }
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        stream.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(typeBytes, data));
        stream.Write(crc);
    }

    /// <summary>PNG's CRC-32 (IEEE 802.3, reflected, initial and final xor of 0xFFFFFFFF).</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in type)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            foreach (var value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var i = 0u; i < 256u; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
