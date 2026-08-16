using Jalium.UI.Diagnostics;
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
[Collection("Application")]
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

    /// <summary>
    /// A decoder whose output encodes each pixel's own source coordinate, so a crop that silently
    /// did not happen is visible in the bytes rather than only in the dimensions.
    /// </summary>
    private sealed class CoordinateDecoder : INativeImageDecoder
    {
        public int DecodeCalls;
        public int Width = 400;
        public int Height = 400;

        public DecodedImage Decode(ReadOnlySpan<byte> data, NativePixelFormat requestedFormat = NativePixelFormat.Bgra8)
        {
            DecodeCalls++;
            var stride = Width * 4;
            var buffer = new byte[stride * Height];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var p = (y * stride) + (x * 4);
                    buffer[p] = (byte)(x & 0xFF);
                    buffer[p + 1] = (byte)(y & 0xFF);
                    buffer[p + 2] = 0x40;
                    buffer[p + 3] = 0xFF;
                }
            }

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

    /// <summary>
    /// The reclaimed buffer held the TRANSFORMED raster, so rebuilding it has to replay the same
    /// transform. The eager restore branch used to publish the raw decode, which silently swapped a
    /// cropped, downscaled image for the uncropped full-size one under the same identity —
    /// SourceRect and DecodePixelWidth survived the load and were then discarded by the first
    /// reclaim/restore cycle, with no error anywhere.
    /// </summary>
    [Fact]
    public void RestoreReplaysDecodeOptionsInsteadOfRepublishingTheRawDecode()
    {
        var decoder = new CoordinateDecoder();
        BitmapImage.SetDecoder(decoder);

        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
        image.SourceRect = new Int32Rect(100, 100, 64, 64);
        image.DecodePixelWidth = 32;
        image.EndInit();

        Assert.Equal(32, image.PixelWidth);
        Assert.NotNull(image.RawPixelData);
        var transformed = (byte[])image.RawPixelData!.Clone();
        var stride = image.PixelStride;
        var height = image.PixelHeight;

        // The crop really moved the origin: pixel (0,0) must carry a source x/y at or past 100,
        // not the (0,0) the untransformed decode would put there.
        Assert.True(transformed[0] >= 100, $"expected cropped origin, got B={transformed[0]}");
        Assert.True(transformed[1] >= 100, $"expected cropped origin, got G={transformed[1]}");

        image.ReclaimIdleResources();
        Assert.Null(image.RawPixelData);

        Assert.True(image.TryRestorePixelData());

        // Geometry AND bytes must come back identical — the restore reproduces the raster, it does
        // not approximate it.
        Assert.Equal(32, image.PixelWidth);
        Assert.Equal(height, image.PixelHeight);
        Assert.Equal(stride, image.PixelStride);
        Assert.NotNull(image.RawPixelData);
        Assert.Equal(transformed, image.RawPixelData!);
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

    /// <summary>
    /// A reclaimed deferred source restores at the bucket layout currently needs, not at whatever
    /// the reclaimed decode happened to publish.
    /// </summary>
    /// <remarks>
    /// "Reclaimed" and "never decoded" are the same state to the deferred decoder, so the restore
    /// is free to aim at the live request. Re-requesting the previously published size instead
    /// would pin a card that has since grown to a permanently blurry raster, and it read fields the
    /// bucket-predicate rewrite deleted.
    /// </remarks>
    [Fact]
    public async Task ReclaimedDeferredSourceIsRestoredAtTheSizeLayoutCurrentlyNeeds()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 1024, NaturalHeight = 512 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        await image.RequestDecodeAsync(100, 50, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        Assert.Equal((128, 64), ImagePipelineTestHarness.PublishedBucket(image));

        await image.RequestDecodeAsync(400, 200, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        Assert.Equal((512, 256), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(2, decoder.DecodeCalls);

        image.ReclaimIdleResources();
        Assert.Null(image.RawPixelData);
        Assert.True(image.IsDeferredDecodePending);
        Assert.False(image.TryGetPixelSnapshot(out _));

        // The deferred branch schedules the decode and reports "not yet"; a renderer draws nothing
        // this frame and the pixels arrive on the next.
        Assert.False(image.TryRestorePixelData());

        Assert.True(
            ImagePipelineTestHarness.SpinUntil(
                () => image.RawPixelData is not null, ImagePipelineTestHarness.DecodeTimeout),
            "the reclaimed source was never restored");

        Assert.Equal(3, decoder.DecodeCalls);

        // The restore aimed at the size layout currently needs — the 512-wide bucket, not the
        // 128-wide one the first decode happened to publish. Read through the snapshot: the
        // intrinsic PixelWidth is the canonical 1024 and would answer the same either way.
        Assert.Equal((512, 256), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(1024, image.PixelWidth);
    }

    /// <summary>
    /// Repeated reclaim/restore cycles cost exactly one decode each and never trip the
    /// unproductive-attempt bound.
    /// </summary>
    /// <remarks>
    /// The bound stops a decode chain after three consecutive decodes that published no more pixels
    /// than the previous one — and a reclaim/restore cycle republishes exactly the same bucket. If
    /// the counter were not cleared along with the publication state, the fourth time an off-screen
    /// image was reclaimed it would come back blank forever, with the saturation record as the only
    /// evidence. Four cycles is the first count that would catch that, so this runs five.
    /// </remarks>
    [Fact]
    public async Task RepeatedReclaimRestoreCyclesNeverTripTheSaturationBound()
    {
        const int cycles = 5;
        var decoder = new RecordingImageDecoder { NaturalWidth = 1024, NaturalHeight = 512 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        await image.RequestDecodeAsync(100, 50, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        Assert.Equal(1, decoder.DecodeCalls);

        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            image.ReclaimIdleResources();
            Assert.Null(image.RawPixelData);

            await image.RequestDecodeAsync(100, 50, cover: false)
                .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

            Assert.Equal(1 + cycle, decoder.DecodeCalls);
            Assert.NotNull(image.RawPixelData);
            Assert.Equal((128, 64), ImagePipelineTestHarness.PublishedBucket(image));
        }

        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(2));
        Assert.Equal(0, recorder.Count(ImageDiagnosticKind.BucketSaturated));
        Assert.Equal(1 + cycles, decoder.DecodeCalls);
    }
}
