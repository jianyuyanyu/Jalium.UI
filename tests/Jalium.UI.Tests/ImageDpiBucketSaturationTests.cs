using Jalium.UI.Diagnostics;
using Jalium.UI.Media.Imaging;

namespace Jalium.UI.Tests;

/// <summary>
/// Pins the termination of the deferred decode chain — the defect that rendered
/// <c>&lt;Image&gt;</c> black on some machines and produced no error anywhere.
/// </summary>
/// <remarks>
/// <para>The old gate demanded that the published raster be at least as large as the request.
/// Two whole classes of input can never satisfy that:</para>
/// <list type="number">
/// <item>a request larger than the natural size — a 24x24 icon in a 24-DIP slot asks for 30, 36 or
/// 48 device pixels at 125%, 150% and 200% display scaling, and the resampler never upscales;</item>
/// <item>a request larger than <see cref="BitmapPixelResampler.MaxBucketEdge"/> — an 8000x6000
/// photo shown at 5120x3840 on a 4K/5K display publishes 4096x3072 forever.</item>
/// </list>
/// <para>Both re-enqueued at the SAME source version, so every version gate passed and a full
/// native decode re-ran forever; on a machine with seven or fewer logical cores the decode pool ran
/// a single worker, so one trapped image left every other image in the process queued and the whole
/// page went blank.</para>
/// <para>Every test here asserts an exact decoder-entry count. A test that only waited for pixels
/// and asserted they arrived would pass against the bug.</para>
/// </remarks>
[Collection("Application")]
public sealed class ImageDpiBucketSaturationTests
{
    private readonly record struct BucketCase(
        int SrcWidth,
        int SrcHeight,
        int RequestedWidth,
        int RequestedHeight,
        bool Cover,
        int ExpectedWidth,
        int ExpectedHeight);

    /// <summary>
    /// (natural size, requested size, cover) triples and the bucket the policy must produce.
    /// </summary>
    /// <remarks>
    /// These numbers are the contract between the producer
    /// (<see cref="BitmapPixelResampler.ResizeToDisplayBucket"/>) and the upgrade predicate
    /// (<c>BitmapImage.DecodeUpgradeNeededLocked</c>). If a change makes the two disagree by a
    /// single pixel, the predicate demands a bucket the producer will never emit and the decode
    /// chain stops terminating — which is exactly how the black screen was produced.
    /// </remarks>
    private static readonly BucketCase[] AllBucketCases =
    [
        // The reported bug: a 24x24 icon in a 24-DIP slot at 100/125/150/200% display scaling.
        // The resampler never upscales, so all four resolve to the natural size.
        new(24, 24, 24, 24, false, 24, 24),
        new(24, 24, 30, 30, false, 24, 24),
        new(24, 24, 36, 36, false, 24, 24),
        new(24, 24, 48, 48, false, 24, 24),

        // The 4K/5K photo: the request exceeds the 4096 bucket ceiling on both axes.
        new(8000, 6000, 5120, 3840, false, 4096, 3072),
        new(8192, 64, 5120, 40, false, 4096, 32),

        // Ordinary downscale, and the two requests that bracket a power-of-two bucket edge.
        new(1024, 512, 100, 50, false, 128, 64),
        new(1024, 512, 102, 51, false, 128, 64),
        new(1024, 512, 103, 52, false, 256, 128),
        new(2048, 1024, 400, 200, false, 512, 256),
        new(2048, 1024, 1500, 750, false, 2048, 1024),

        // Cover (Stretch.UniformToFill): an aspect-mismatched viewport can never be satisfied on
        // both axes, which the old "did we publish enough pixels?" gate treated as "decode again".
        new(1000, 100, 300, 300, true, 1000, 100),
        new(1000, 100, 100, 10, true, 128, 13),

        // A request larger than the natural size on both axes.
        new(100, 50, 200, 200, false, 100, 50),

        // One unbounded axis: Image.MeasureOverride sends 0 for an axis layout has not resolved.
        new(1024, 512, 300, 0, false, 512, 256),
        new(1024, 512, 0, 100, false, 256, 128),

        // "The caller does not know the size yet" and a nonsense request both mean "unbounded".
        new(1024, 512, 0, 0, false, 1024, 512),
        new(1024, 512, -5, -7, false, 1024, 512),

        // Degenerate shapes: a zero-sized source, a 1px source, an exact-fit request, and an
        // extreme aspect ratio whose bucket scale lands above 1 and has to clamp back.
        new(0, 0, 100, 100, false, 0, 0),
        new(1, 1, 64, 64, false, 1, 1),
        new(1024, 512, 1024, 512, false, 1024, 512),
        new(4000, 3, 2000, 2, false, 4000, 3),
    ];

    public static TheoryData<int, int, int, int, bool, int, int> BucketMatrix =>
        BuildMatrix(static _ => true);

    /// <summary>
    /// <see cref="BucketMatrix"/> minus the cases whose natural buffer is too large to materialize
    /// (8000x6000 BGRA is 192 MiB) or is degenerate for <see cref="DecodedImage"/>.
    /// </summary>
    public static TheoryData<int, int, int, int, bool, int, int> ResampleMatrix =>
        BuildMatrix(static c =>
            c.SrcWidth > 0 && c.SrcHeight > 0 && (long)c.SrcWidth * c.SrcHeight <= 4_000_000L);

    private static TheoryData<int, int, int, int, bool, int, int> BuildMatrix(
        Func<BucketCase, bool> filter)
    {
        var data = new TheoryData<int, int, int, int, bool, int, int>();
        foreach (var bucketCase in AllBucketCases)
        {
            if (!filter(bucketCase))
            {
                continue;
            }

            data.Add(
                bucketCase.SrcWidth,
                bucketCase.SrcHeight,
                bucketCase.RequestedWidth,
                bucketCase.RequestedHeight,
                bucketCase.Cover,
                bucketCase.ExpectedWidth,
                bucketCase.ExpectedHeight);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BucketMatrix))]
    public void ResolveBucketProducesTheDocumentedBucketForEveryShape(
        int srcWidth,
        int srcHeight,
        int requestedWidth,
        int requestedHeight,
        bool cover,
        int expectedWidth,
        int expectedHeight)
    {
        var bucket = BitmapPixelResampler.ResolveBucket(
            srcWidth, srcHeight, requestedWidth, requestedHeight, cover);

        Assert.Equal((expectedWidth, expectedHeight), bucket);
    }

    /// <summary>
    /// The producer must land on exactly the predicate's answer. This is the single invariant that
    /// makes the decode chain terminate; nothing else in the pipeline enforces it.
    /// </summary>
    [Theory]
    [MemberData(nameof(ResampleMatrix))]
    public void ResizeToDisplayBucketLandsExactlyOnResolveBucket(
        int srcWidth,
        int srcHeight,
        int requestedWidth,
        int requestedHeight,
        bool cover,
        int expectedWidth,
        int expectedHeight)
    {
        var stride = srcWidth * 4;
        var source = new DecodedImage(
            new byte[stride * srcHeight], srcWidth, srcHeight, stride, NativePixelFormat.Bgra8);

        var resized = BitmapPixelResampler.ResizeToDisplayBucket(
            source, requestedWidth, requestedHeight, cover);

        Assert.Equal(expectedWidth, resized.Width);
        Assert.Equal(expectedHeight, resized.Height);
        Assert.Equal(
            BitmapPixelResampler.ResolveBucket(
                srcWidth, srcHeight, requestedWidth, requestedHeight, cover),
            (resized.Width, resized.Height));
        Assert.True(resized.Stride >= resized.Width * 4);
        Assert.True(resized.Pixels.Length >= resized.Stride * resized.Height);
    }

    /// <summary>
    /// The two structural properties that make an unsatisfiable request harmless: the bucket never
    /// exceeds the natural size, and once a downscale happens it never exceeds the ceiling.
    /// </summary>
    [Theory]
    [MemberData(nameof(BucketMatrix))]
    public void ResolveBucketNeverUpscalesAndHonoursTheBucketCeiling(
        int srcWidth,
        int srcHeight,
        int requestedWidth,
        int requestedHeight,
        bool cover,
        int expectedWidth,
        int expectedHeight)
    {
        _ = expectedWidth;
        _ = expectedHeight;

        var (width, height) = BitmapPixelResampler.ResolveBucket(
            srcWidth, srcHeight, requestedWidth, requestedHeight, cover);

        Assert.True(width <= srcWidth, $"bucket width {width} exceeds natural {srcWidth}");
        Assert.True(height <= srcHeight, $"bucket height {height} exceeds natural {srcHeight}");

        var isSourceUnchanged = width == srcWidth && height == srcHeight;
        Assert.True(
            isSourceUnchanged || Math.Max(width, height) <= BitmapPixelResampler.MaxBucketEdge,
            $"downscaled bucket {width}x{height} exceeds the {BitmapPixelResampler.MaxBucketEdge} ceiling");

        if (srcWidth > 0 && srcHeight > 0)
        {
            Assert.True(width >= 1 && height >= 1);
        }
    }

    /// <summary>
    /// The reported bug, end to end: a 24x24 source in a 24-DIP slot at every common display
    /// scaling. Before the fix, every case above 24 re-decoded forever.
    /// </summary>
    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(36)]
    [InlineData(48)]
    public async Task DecodeTerminatesAtEveryDpiScaleForAnIconSizedSlot(int requestedPixels)
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 24, NaturalHeight = 24 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        await image
            .RequestDecodeAsync(requestedPixels, requestedPixels, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        Assert.Equal(1, decoder.DecodeCalls);
        Assert.NotNull(image.RawPixelData);
        Assert.Equal(24, image.PixelWidth);
        Assert.Equal(24, image.PixelHeight);
        Assert.Equal(24, image.NaturalPixelWidth);

        // The resampler never upscales, so the bucket IS the natural size at every one of the four
        // scalings — which is the whole reason the old "did we publish at least what was asked
        // for?" gate could never be satisfied here.
        Assert.Equal((24, 24), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.False(image.IsDeferredDecodePending);

        // Re-asking for the same size, and for a smaller one, must both be free.
        await image
            .RequestDecodeAsync(requestedPixels, requestedPixels, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        await image
            .RequestDecodeAsync(24, 24, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        // A chain that re-enqueues itself does so from a decode worker, so give it the chance to
        // show up rather than sampling the counter the instant the first publish returns.
        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(2));

        Assert.Equal(1, decoder.DecodeCalls);
        Assert.Equal(1, recorder.CompletedDecodes);
        Assert.Equal(0, recorder.Count(ImageDiagnosticKind.BucketSaturated));
    }

    /// <summary>
    /// The second unsatisfiable class: the request exceeds the bucket ceiling, so the published
    /// raster stays strictly smaller than both the request AND the natural size forever. A gate
    /// that clamped the request to the natural size alone would still loop here.
    /// </summary>
    [Fact]
    public async Task DecodeTerminatesWhenTheRequestExceedsTheBucketCeiling()
    {
        // 8192x64 is the memory-cheap analogue of the reported 8000x6000 photo at 5120x3840: the
        // request is above the ceiling on the long edge and the bucket saturates at 4096. The
        // full-size numbers are pinned by ResolveBucketProducesTheDocumentedBucketForEveryShape.
        var decoder = new RecordingImageDecoder { NaturalWidth = 8192, NaturalHeight = 64 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        await image
            .RequestDecodeAsync(5120, 40, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(3));

        Assert.Equal(1, decoder.DecodeCalls);
        Assert.Equal(
            (BitmapPixelResampler.MaxBucketEdge, 32),
            ImagePipelineTestHarness.PublishedBucket(image));

        // The published bucket is smaller than the natural size, so "we already have everything"
        // cannot be what stopped the chain — only the bucket comparison can have. The bucket used
        // to be readable through PixelWidth; PixelWidth now reports the canonical 8192, which is
        // what an author asked for the intrinsic size means and what the clipboard, the drag
        // source and the window icon size their buffers from.
        Assert.Equal(8192, image.NaturalPixelWidth);
        Assert.Equal(8192, image.PixelWidth);
        Assert.Equal(64, image.PixelHeight);
        Assert.Equal(0, recorder.Count(ImageDiagnosticKind.BucketSaturated));
    }

    /// <summary>
    /// Cover mode requires both axes covered, which an aspect-preserving bucket can never deliver
    /// for a mismatched viewport.
    /// </summary>
    [Fact]
    public async Task CoverModeAspectMismatchDoesNotLoop()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 1000, NaturalHeight = 100 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        await image
            .RequestDecodeAsync(300, 300, cover: true)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(2));

        Assert.Equal(1, decoder.DecodeCalls);
        Assert.Equal((1000, 100), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(1000, image.PixelWidth);
        Assert.Equal(100, image.PixelHeight);
    }

    /// <summary>
    /// Growth inside one power-of-two bucket must cost nothing — a window-resize drag raises the
    /// request on every mouse move — while crossing a bucket edge must cost exactly one decode.
    /// </summary>
    [Fact]
    public async Task SubBucketRequestGrowthSchedulesNoAdditionalDecode()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 1024, NaturalHeight = 512 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        await image
            .RequestDecodeAsync(100, 50, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        Assert.Equal(1, decoder.DecodeCalls);

        // Asserted on the published BUCKET throughout. PixelWidth is the canonical 1024 at every
        // step now, so phrasing a ladder assertion on it would hold even if the ladder stopped
        // working entirely.
        Assert.Equal((128, 64), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(1024, image.PixelWidth);

        // 128 is the bucket for every request whose long edge times the 25% headroom still fits in
        // 128 device pixels, i.e. everything up to 102 on a 1024px source.
        for (var requested = 101; requested <= 102; requested++)
        {
            await image
                .RequestDecodeAsync(requested, requested / 2, cover: false)
                .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        }

        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(2));
        Assert.Equal(1, decoder.DecodeCalls);
        Assert.Equal((128, 64), ImagePipelineTestHarness.PublishedBucket(image));

        // 103 is the first request whose headroom crosses into the 256 bucket, and it must upgrade.
        await image
            .RequestDecodeAsync(103, 52, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(2));

        Assert.Equal(2, decoder.DecodeCalls);
        Assert.Equal((256, 128), ImagePipelineTestHarness.PublishedBucket(image));

        // And the intrinsic size did not move while the bucket doubled — the property that stops
        // the draw rect from ratcheting the next decode hint upward.
        Assert.Equal(1024, image.PixelWidth);
        Assert.Equal(512, image.PixelHeight);
    }

    /// <summary>
    /// The attempt bound counts only CONSECUTIVE unproductive decodes, so genuine growth across
    /// bucket boundaries is never refused however far the ladder climbs.
    /// </summary>
    [Fact]
    public async Task GenuineBucketGrowthStillUpgradesWithoutLimit()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 2048, NaturalHeight = 1024 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        await image.RequestDecodeAsync(100, 50, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        Assert.Equal((128, 64), ImagePipelineTestHarness.PublishedBucket(image));

        await image.RequestDecodeAsync(400, 200, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        Assert.Equal((512, 256), ImagePipelineTestHarness.PublishedBucket(image));

        await image.RequestDecodeAsync(1500, 750, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(3));

        Assert.Equal(3, decoder.DecodeCalls);
        Assert.Equal((2048, 1024), ImagePipelineTestHarness.PublishedBucket(image));

        // Three buckets, one intrinsic size. Anything that made this number move would put a full
        // measure/arrange pass behind every rung of the ladder.
        Assert.Equal(2048, image.PixelWidth);
        Assert.Equal(1024, image.PixelHeight);
        Assert.Equal(0, recorder.Count(ImageDiagnosticKind.BucketSaturated));
    }

    /// <summary>
    /// The hard bound at the top of the decode path is independent of the predicate: with a
    /// deliberately always-true upgrade predicate injected, the chain still stops.
    /// </summary>
    /// <remarks>
    /// This is the belt-and-braces half of the fix. The predicate is the exact thing that broke
    /// last time, so the bound must hold without trusting it: at most
    /// <c>1 + MaxUnproductiveDecodeAttempts</c> native decodes per source version, the awaiter
    /// released rather than hung, and the saturation reported so a support capture can name it.
    /// </remarks>
    [Fact]
    public async Task BrokenPredicateStillCannotExceedFourDecodesPerSourceVersion()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 256, NaturalHeight = 256 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();
        image.SetUpgradePredicateOverrideForTests(static () => true);

        await image.RequestDecodeAsync(64, 64, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        Assert.True(
            recorder.WaitForCount(
                ImageDiagnosticKind.BucketSaturated, 1, ImagePipelineTestHarness.DecodeTimeout),
            "the runaway chain was never stopped by the unproductive-attempt bound");
        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(3));

        Assert.Equal(4, decoder.DecodeCalls);
        Assert.Equal(1, recorder.Count(ImageDiagnosticKind.BucketSaturated));
        Assert.NotNull(image.RawPixelData);

        // A saturated source costs nothing at all on the next request: no decode, no queue entry,
        // and the caller is handed a completed task instead of waiting for a decode that will
        // never run.
        var afterSaturation = image.RequestDecodeAsync(64, 64, cover: false);
        Assert.True(afterSaturation.IsCompletedSuccessfully);
        Assert.Equal(4, decoder.DecodeCalls);
    }

    /// <summary>
    /// A reader must never be able to observe a request that decodes nothing: an all-zero request
    /// means "the caller does not know the size yet", and resolving it as the natural size must not
    /// make a later sized request re-decode.
    /// </summary>
    [Fact]
    public async Task UnsizedRequestDecodesTheNaturalSizeAndIsNeverUpgraded()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 640, NaturalHeight = 480 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        await image.RequestDecodeAsync(0, 0, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        Assert.Equal(1, decoder.DecodeCalls);
        Assert.Equal((640, 480), ImagePipelineTestHarness.PublishedBucket(image));

        // Layout resolves and now asks for a much smaller bucket. Nothing bigger is reachable, so
        // no decode may be scheduled — and, critically, the resident bucket must not SHRINK either.
        await image.RequestDecodeAsync(64, 48, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(2));

        Assert.Equal(1, decoder.DecodeCalls);
        Assert.Equal((640, 480), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(640, image.PixelWidth);
    }
}
