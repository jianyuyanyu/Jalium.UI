namespace Jalium.UI.Media.Imaging;

/// <summary>
/// One immutable publication unit for decoded pixels: the buffer and every number a consumer
/// needs to interpret it, produced together and published with a single reference write.
/// </summary>
/// <remarks>
/// <para>The deferred decoder used to publish pixels, width, height and stride as four separate
/// field writes under a lock, while the render thread read those four fields with no lock at all
/// (<c>JALIUM_RENDER_THREAD</c> defaults on for Windows). A reader that interleaved with a
/// publish could pair a 64x64 buffer with 512x512 dimensions and hand a 1 MiB dimension triple to
/// a 16 KiB buffer — an out-of-range upload whose only symptom is a black rectangle. Bundling the
/// tuple into one immutable object makes that state unrepresentable: a reader either sees the
/// whole previous snapshot or the whole new one.</para>
/// <para><see cref="Pixels"/> is never mutated after <see cref="Create"/> returns, so clones and
/// frozen copies may share the same instance by reference.</para>
/// </remarks>
/// <param name="Pixels">The decoded buffer. Owned by the snapshot; must never be written to.</param>
/// <param name="Width">Published (bucket) pixel width — the width of <paramref name="Pixels"/>.</param>
/// <param name="Height">Published (bucket) pixel height — the height of <paramref name="Pixels"/>.</param>
/// <param name="Stride">Bytes between two adjacent rows of <paramref name="Pixels"/>.</param>
/// <param name="Format">Channel order of <paramref name="Pixels"/>. Carried, not assumed: an
/// Android/Mali decoder legitimately returns <see cref="NativePixelFormat.Rgba8"/>, and dropping
/// it on the floor is what produces red/blue-swapped uploads.</param>
/// <param name="CanonicalWidth">Natural decoded width the bucket was derived from.</param>
/// <param name="CanonicalHeight">Natural decoded height the bucket was derived from.</param>
/// <param name="Generation">Process-unique, monotonically increasing publication id. Caches key
/// off this to notice that a raster was replaced by a larger decode.</param>
internal sealed record BitmapPixelSnapshot(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    NativePixelFormat Format,
    int CanonicalWidth,
    int CanonicalHeight,
    long Generation)
{
    private static long s_generationCounter;

    /// <summary>
    /// Creates a snapshot, stamping the next process-wide generation.
    /// </summary>
    /// <remarks>
    /// The geometry is validated here rather than at the consumers: every consumer of the old
    /// four-field publish had to re-derive "does this buffer actually hold these dimensions?",
    /// and the ones that skipped the check are exactly the ones that produced silent black
    /// output. Publishing an inconsistent snapshot is a framework bug, so it throws.
    /// </remarks>
    internal static BitmapPixelSnapshot Create(
        byte[] pixels,
        int width,
        int height,
        int stride,
        NativePixelFormat format,
        int canonicalWidth,
        int canonicalHeight)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var minimumStride = PixelBufferLayout.GetMinimumStride(width);
        if (stride < minimumStride)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stride),
                stride,
                $"Stride must be at least {minimumStride} bytes for a {width}px row.");
        }

        var requiredBytes = PixelBufferLayout.GetRequiredByteCount(width, height, stride);
        if (pixels.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"Pixel buffer holds {pixels.Length} bytes but {width}x{height} at stride {stride} needs {requiredBytes}.",
                nameof(pixels));
        }

        return new BitmapPixelSnapshot(
            pixels,
            width,
            height,
            stride,
            format,
            canonicalWidth > 0 ? canonicalWidth : width,
            canonicalHeight > 0 ? canonicalHeight : height,
            Interlocked.Increment(ref s_generationCounter));
    }

    /// <summary>
    /// Stamps a generation without re-validating. Used only for snapshots synthesised from a
    /// bitmap's legacy pixel fields, whose geometry has already been checked by the caller.
    /// </summary>
    internal static long NextGeneration() => Interlocked.Increment(ref s_generationCounter);
}
