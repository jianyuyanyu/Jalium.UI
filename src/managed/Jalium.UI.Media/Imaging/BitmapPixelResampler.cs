namespace Jalium.UI.Media.Imaging;

/// <summary>
/// Display-size bitmap resampling used by the deferred decoder before pixels are published.
/// </summary>
internal static class BitmapPixelResampler
{
    /// <summary>
    /// Hard ceiling on the long edge of a decode bucket. Retained pixel memory grows with the
    /// square of this number, so it is deliberately far below the 16384 the upload path allows.
    /// </summary>
    /// <remarks>
    /// This ceiling is the reason <see cref="ResolveBucket"/> must exist: a request larger than
    /// the ceiling — a 8000x6000 photo shown at 5120x3840 on a 5K display — can never be
    /// satisfied literally. Any predicate that asks "did we publish at least the requested
    /// number of pixels?" therefore says "no" forever and re-decodes forever. The only safe
    /// question is "would another decode produce a bigger bucket than the one we published?",
    /// and that question can only be answered by calling the same function the producer calls.
    /// </remarks>
    internal const int MaxBucketEdge = 4096;

    /// <summary>
    /// The single source of truth for the pixel size a decode will publish for a
    /// (natural size, requested size, cover) triple. Pure, allocation-free and deterministic.
    /// </summary>
    /// <remarks>
    /// <para><see cref="ResizeToDisplayBucket"/> calls this and resamples to exactly its output,
    /// so the producer and any caller that needs to predict the producer cannot disagree.
    /// <b>Never</b> reimplement or approximate this policy anywhere else — the deferred decoder
    /// decides whether to run another decode by comparing this function's output against the
    /// bucket it already published, and a copy that drifts by a single pixel turns that
    /// comparison into an unbounded decode loop.</para>
    /// <para>Every early-out returns <c>(srcWidth, srcHeight)</c>, matching
    /// <see cref="ResizeToDisplayBucket"/>'s "return the source unchanged" behaviour.</para>
    /// </remarks>
    /// <param name="srcWidth">Natural (decoded) pixel width.</param>
    /// <param name="srcHeight">Natural (decoded) pixel height.</param>
    /// <param name="requestedWidth">Requested viewport width in device pixels; 0 means unbounded.</param>
    /// <param name="requestedHeight">Requested viewport height in device pixels; 0 means unbounded.</param>
    /// <param name="cover">Whether the viewport must be fully covered (<c>Stretch.UniformToFill</c>).</param>
    internal static (int Width, int Height) ResolveBucket(
        int srcWidth,
        int srcHeight,
        int requestedWidth,
        int requestedHeight,
        bool cover)
    {
        if (requestedWidth <= 0 && requestedHeight <= 0)
            return (srcWidth, srcHeight);

        if (srcWidth <= 0 || srcHeight <= 0)
            return (srcWidth, srcHeight);

        double scale;
        if (requestedWidth > 0 && requestedHeight > 0)
        {
            var scaleX = requestedWidth / (double)srcWidth;
            var scaleY = requestedHeight / (double)srcHeight;
            scale = cover ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
        }
        else if (requestedWidth > 0)
        {
            scale = requestedWidth / (double)srcWidth;
        }
        else
        {
            scale = requestedHeight / (double)srcHeight;
        }

        // Never enlarge encoded raster data. Add 25% headroom and quantize the long
        // edge to a power-of-two bucket so small layout/DPI changes reuse the decode.
        scale = Math.Min(1d, Math.Max(0d, scale * 1.25d));
        if (!(scale > 0d) || scale >= 0.999d)
            return (srcWidth, srcHeight);

        var provisionalWidth = Math.Max(1, (int)Math.Ceiling(srcWidth * scale));
        var provisionalHeight = Math.Max(1, (int)Math.Ceiling(srcHeight * scale));
        var longEdge = Math.Max(provisionalWidth, provisionalHeight);
        var bucketEdge = Math.Min(MaxBucketEdge, NextPowerOfTwo(longEdge));
        var bucketScale = bucketEdge / (double)Math.Max(srcWidth, srcHeight);
        var targetWidth = Math.Clamp((int)Math.Ceiling(srcWidth * bucketScale), 1, srcWidth);
        var targetHeight = Math.Clamp((int)Math.Ceiling(srcHeight * bucketScale), 1, srcHeight);
        return (targetWidth, targetHeight);
    }

    internal static DecodedImage ResizeToDisplayBucket(
        DecodedImage source,
        int requestedWidth,
        int requestedHeight,
        bool cover)
    {
        // One call, one policy. The resample below must land on exactly these numbers;
        // anything else silently breaks the deferred decoder's termination guarantee.
        var (targetWidth, targetHeight) =
            ResolveBucket(source.Width, source.Height, requestedWidth, requestedHeight, cover);

        return ResampleTo(source, targetWidth, targetHeight);
    }

    /// <summary>
    /// Resamples to an already-resolved bucket size.
    /// </summary>
    /// <remarks>
    /// The deferred decoder resolves its target through its own accumulated-request policy — which
    /// is the maximum over several <see cref="ResolveBucket"/> answers, not a single one — and then
    /// resamples to exactly that number through this entry point. Keeping "decide" and "resample"
    /// as separate steps is what lets the producer and the upgrade predicate share one decision
    /// instead of each recomputing it from a request pair and risking a one-pixel disagreement,
    /// which is an unbounded decode loop.
    /// </remarks>
    internal static DecodedImage ResampleTo(DecodedImage source, int targetWidth, int targetHeight)
    {
        var srcWidth = source.Width;
        var srcHeight = source.Height;

        if (targetWidth == srcWidth && targetHeight == srcHeight)
            return source;

        if (targetWidth <= 0 || targetHeight <= 0 || srcWidth <= 0 || srcHeight <= 0)
            return source;

        var sourcePixels = source.Pixels.Span;
        var targetStride = checked(targetWidth * 4);
        var targetPixels = new byte[checked(targetStride * targetHeight)];

        // Bilinear sampling is O(display pixels), unlike the old scalar box filter whose
        // work remained O(source pixels) for every thumbnail. The half-pixel mapping keeps
        // edges stable while a card moves between adjacent decode buckets.
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = ((y + 0.5d) * srcHeight / targetHeight) - 0.5d;
            var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, srcHeight - 1);
            var y1 = Math.Min(y0 + 1, srcHeight - 1);
            var fy = Math.Clamp(sourceY - y0, 0d, 1d);
            var row0 = y0 * source.Stride;
            var row1 = y1 * source.Stride;
            var targetRow = y * targetStride;

            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = ((x + 0.5d) * srcWidth / targetWidth) - 0.5d;
                var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, srcWidth - 1);
                var x1 = Math.Min(x0 + 1, srcWidth - 1);
                var fx = Math.Clamp(sourceX - x0, 0d, 1d);
                var p00 = row0 + (x0 * 4);
                var p10 = row0 + (x1 * 4);
                var p01 = row1 + (x0 * 4);
                var p11 = row1 + (x1 * 4);
                var destination = targetRow + (x * 4);

                for (var channel = 0; channel < 4; channel++)
                {
                    var top = sourcePixels[p00 + channel] +
                              ((sourcePixels[p10 + channel] - sourcePixels[p00 + channel]) * fx);
                    var bottom = sourcePixels[p01 + channel] +
                                 ((sourcePixels[p11 + channel] - sourcePixels[p01 + channel]) * fx);
                    targetPixels[destination + channel] =
                        (byte)Math.Clamp((int)Math.Round(top + ((bottom - top) * fy)), 0, 255);
                }
            }
        }

        return new DecodedImage(
            targetPixels,
            targetWidth,
            targetHeight,
            targetStride,
            source.Format);
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
            return 1;

        var result = 1;
        while (result < value && result < MaxBucketEdge)
        {
            result <<= 1;
        }
        return result;
    }
}
