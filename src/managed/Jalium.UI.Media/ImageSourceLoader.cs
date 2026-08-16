using Jalium.UI.Media.Imaging;

namespace Jalium.UI.Media;

/// <summary>
/// Resolves an encoded image reference (URI / file path / byte array) into the
/// concrete <see cref="ImageSource"/> that best fits the payload: a multi-frame
/// source (animated GIF / APNG / animated WebP) becomes an
/// <see cref="AnimatedBitmap"/> so it plays back, while a single-frame source
/// stays a plain <see cref="BitmapImage"/>.
/// </summary>
/// <remarks>
/// This is the single choke point the framework's string/URI → <see cref="ImageSource"/>
/// conversions route through (the <c>ImageSourceConverter</c>, the XAML reader,
/// and the GPU bundle renderer), so <c>&lt;Image Source="cat.gif"/&gt;</c> animates
/// without callers having to know about <see cref="AnimatedBitmap"/>. Frame count
/// is probed from the encoded bytes' metadata only (no pixel decode), so the
/// dominant single-frame PNG/JPEG case pays just one cheap header read.
///
/// <para>The eager swap below only fires when the encoded bytes are already in hand.
/// A URI-backed source's are not: <c>BitmapImage</c> defers reading them entirely, so
/// <see cref="BitmapImage.ImageData"/> is null here for every file, manifest-resource,
/// disk-relative and <c>http(s)</c> URI. Those animate through
/// <c>BitmapImage.AnimatedSubstitute</c> instead — the deferred metadata probe reads the
/// frame count on a worker and builds an internal <see cref="AnimatedBitmap"/> that every
/// consumer resolves through, one dispatcher turn after the source is set and with no
/// blocking read on this, the synchronous type-converter path. This method deliberately
/// does NOT wait for that: it would put unbounded file or network I/O on the XAML
/// reader's thread.</para>
/// </remarks>
public static class ImageSourceLoader
{
    /// <summary>
    /// Resolves encoded image <paramref name="data"/> into an animated or static
    /// <see cref="ImageSource"/> based on its frame count.
    /// </summary>
    public static ImageSource FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return BitmapImage.ProbeFrameCount(data) > 1
            ? AnimatedBitmap.FromBytes(data)
            : BitmapImage.FromBytes(data);
    }

    /// <summary>
    /// Reads <paramref name="filePath"/> and resolves it into an animated or
    /// static <see cref="ImageSource"/> based on its frame count.
    /// </summary>
    public static ImageSource FromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        return FromBytes(System.IO.File.ReadAllBytes(filePath));
    }

    /// <summary>
    /// Resolves <paramref name="uri"/> into an <see cref="ImageSource"/>, never blocking the
    /// calling thread on I/O.
    /// </summary>
    /// <remarks>
    /// A source whose encoded bytes are already materialised is upgraded to
    /// <see cref="AnimatedBitmap"/> outright when it is multi-frame. Every URI-backed source
    /// defers its bytes, so it comes back as a <see cref="BitmapImage"/> and animates through the
    /// internal substitute its metadata probe builds — see the type-level remarks. Either way the
    /// caller gets a usable <see cref="ImageSource"/> synchronously.
    /// </remarks>
    public static ImageSource FromUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // Reuse BitmapImage's own URI resolver (file / manifest resource / disk-relative / http).
        var bitmap = new BitmapImage(uri);

        // Kept for the already-materialised case only. Reading the bytes HERE to answer the
        // question would put an unbounded File.ReadAllBytes — or worse, a manifest-resource
        // decompression — on the XAML type-converter's thread, once per image, which is exactly
        // the UI-thread I/O the deferred loader exists to remove.
        var encoded = bitmap.ImageData;
        if (encoded is not null && BitmapImage.ProbeFrameCount(encoded) > 1)
        {
            bitmap.Dispose();
            return AnimatedBitmap.FromBytes(encoded);
        }

        return bitmap;
    }
}
