using System.Reflection;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Threading;
using ImageControl = Jalium.UI.Controls.Image;

namespace Jalium.UI.Tests;

/// <summary>
/// Pins the separation between a bitmap's INTRINSIC size and the display BUCKET its renderer
/// currently holds.
/// </summary>
/// <remarks>
/// <para>The deferred decoder publishes a raster sized for the slot the image is drawn into — a
/// 128x64 thumbnail for a 1024x512 asset in a 100-DIP card. That number was mirrored into
/// <c>Width</c>/<c>Height</c>/<c>PixelWidth</c>/<c>PixelHeight</c>, which are the ONLY sizing input
/// <c>Image.MeasureOverride</c> and <c>Image.OnRender</c> have. Two things followed, and this file
/// exists to stop either coming back.</para>
/// <para><b>The ratchet.</b> The intrinsic size is an input to layout, and mirroring the bucket
/// into it made it an output as well. With <c>Stretch.None</c>/<c>UpOnly</c> the draw rect IS the
/// intrinsic size, and the renderer turns the draw rect into the next decode hint; the bucket
/// policy's 25% headroom plus its next-power-of-two step then answered with a bigger bucket every
/// single time. An ordinary fixed-size grid cell paid three or more full native decodes and full
/// bilinear resamples, drew at the wrong scale in between, and defeated
/// <c>Image.OnSourceAsyncLoaded</c>'s measure-skip permanently, so every rung of the ladder also
/// cost a measure/arrange pass over the subtree.</para>
/// <para><b>The lie.</b> <c>PixelWidth</c>/<c>PixelHeight</c> and <c>CopyPixels</c> are what the
/// clipboard, the OLE drag source, the window icon, the notification backends and
/// <c>BitmapPalette</c> size their buffers from. Reporting the bucket handed every one of them
/// thumbnail-resolution data for a full-resolution asset, silently.</para>
/// </remarks>
[Collection("Application")]
public sealed class ImageIntrinsicSizeTests
{
    /// <summary>
    /// The intrinsic size is the canonical decode size and does not move as the bucket climbs.
    /// </summary>
    [Fact]
    public async Task IntrinsicSizeIsTheCanonicalDecodeSizeAndIsInvariantAcrossTheBucketLadder()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 1024, NaturalHeight = 512 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        await image.RequestDecodeAsync(100, 50, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        Assert.Equal((128, 64), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(1024, image.PixelWidth);
        Assert.Equal(512, image.PixelHeight);
        Assert.Equal(1024d, image.Width);
        Assert.Equal(512d, image.Height);

        // The RASTER pair is the one that tracks the bucket, and it is what describes RawPixelData.
        Assert.Equal(128, image.RasterPixelWidth);
        Assert.Equal(64, image.RasterPixelHeight);

        await image.RequestDecodeAsync(400, 200, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        Assert.Equal(2, decoder.DecodeCalls);
        Assert.Equal((512, 256), ImagePipelineTestHarness.PublishedBucket(image));

        // Four times the pixels, same intrinsic size. Nothing layout can see changed.
        Assert.Equal(1024, image.PixelWidth);
        Assert.Equal(512, image.PixelHeight);
        Assert.Equal(512, image.RasterPixelWidth);
        Assert.Equal(256, image.RasterPixelHeight);
    }

    /// <summary>
    /// <c>RawPixelData</c> is described by <c>RasterPixelWidth</c>/<c>RasterPixelHeight</c>/
    /// <c>PixelStride</c> and never by <c>PixelWidth</c>/<c>PixelHeight</c>.
    /// </summary>
    /// <remarks>
    /// The failure this guards is not subtle: a consumer that sizes a buffer from the canonical
    /// pair and then reads that many rows out of a bucketed one runs off the end of the array, and
    /// the ones that catch the exception render a black rectangle instead.
    /// </remarks>
    [Fact]
    public async Task RawPixelDataIsDescribedByTheRasterPairAndNotByTheCanonicalPair()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 1024, NaturalHeight = 512 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        await image.RequestDecodeAsync(100, 50, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        var raw = image.RawPixelData;
        Assert.NotNull(raw);
        Assert.True(
            image.RasterPixelWidth < image.PixelWidth,
            $"the canonical pair is reporting the resident bucket ({image.PixelWidth} == " +
            $"{image.RasterPixelWidth}) — the intrinsic size is layout-derived again");
        Assert.True(image.PixelStride >= image.RasterPixelWidth * 4);
        Assert.Equal((long)image.PixelStride * image.RasterPixelHeight, raw!.Length);

        // And the pairing that used to be correct is now provably out of range, which is exactly
        // why every in-tree consumer was moved onto the snapshot / CopyPixels seams.
        Assert.True((long)image.PixelStride * image.PixelHeight > raw.Length);
    }

    /// <summary>
    /// <c>CopyPixels</c> hands back FULL-RESOLUTION pixels for a source whose resident raster is a
    /// display bucket — the contract the clipboard, the drag source, the window icon, the
    /// notification backends and <c>BitmapPalette</c> all depend on.
    /// </summary>
    /// <remarks>
    /// The probe is a single bright pixel at (0,0) on an otherwise black image. A 4x downscale does
    /// not even sample it (bilinear at target x=0 reads source x=1 and x=2), so a bucket — whether
    /// returned raw or upscaled back to the canonical size — reads 0x00 there. Only the real
    /// full-resolution raster reads 0xFF. Asserting the DIMENSIONS alone would pass against an
    /// upscaled thumbnail, which is precisely the silent quality loss this closes.
    /// </remarks>
    [Fact]
    public async Task CopyPixelsResolvesFullResolutionPixelsForABucketedSource()
    {
        var decoder = new RecordingImageDecoder
        {
            NaturalWidth = 256,
            NaturalHeight = 256,
            Fill = 0x00,
            FirstPixelBytes = [0xFF, 0xFF, 0xFF, 0xFF],
        };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        await image.RequestDecodeAsync(32, 32, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        // 32 * 1.25 = 40, quantized up to the 64 bucket: a quarter of the source on each axis.
        Assert.Equal((64, 64), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(256, image.PixelWidth);
        Assert.Equal(256, image.PixelHeight);

        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);

        Assert.Equal(0xFF, pixels[0]);
        Assert.Equal(0x00, pixels[4]);

        // Rebuilt for the read and thrown away: publishing it would defeat the display bucket,
        // which exists so an image drawn small does not hold its full-resolution raster resident.
        Assert.Equal((64, 64), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(64, image.RasterPixelWidth);
        Assert.Equal(2, decoder.DecodeCalls);
    }

    /// <summary>
    /// A clone of a bucketed source reports the same intrinsic size as its original and keeps a
    /// self-consistent raster tuple.
    /// </summary>
    /// <remarks>
    /// <c>CopyBitmapStateFrom</c> validates the buffer it is about to clone against a dimension
    /// pair. Reading the canonical pair there would fail that check for every bucketed source and
    /// send it down the malformed-source branch, which produces a clone whose pixels no consumer
    /// will accept — a blank image with nothing to explain it.
    /// </remarks>
    [Fact]
    public void CloningABucketedSourceKeepsBothGeometryPairsConsistent()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 1024, NaturalHeight = 512 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        // Not awaited: Clone() reaches Freezable/dependency-property state, which is thread-affine.
        ImagePipelineTestHarness.AwaitDecodeOnThisThread(
            image.RequestDecodeAsync(100, 50, cover: false));

        var clone = (BitmapImage)image.Clone();

        Assert.Equal(1024, clone.PixelWidth);
        Assert.Equal(512, clone.PixelHeight);
        Assert.Equal(128, clone.RasterPixelWidth);
        Assert.Equal(64, clone.RasterPixelHeight);
        Assert.Equal((128, 64), ImagePipelineTestHarness.PublishedBucket(clone));

        var raw = clone.RawPixelData;
        Assert.NotNull(raw);
        Assert.Equal((long)clone.PixelStride * clone.RasterPixelHeight, raw!.Length);
    }

    /// <summary>
    /// A failed reload leaves the previous raster on screen, and the snapshot channel's legacy
    /// fallback still describes it correctly.
    /// </summary>
    /// <remarks>
    /// <para>This is the one reachable state in which the resident raster is a display bucket while
    /// the source is no longer deferred: assigning a new source to a bitmap that already has a URI
    /// raster runs <c>ResetDeferredSource</c>, which deliberately KEEPS the previous pixels so a
    /// decoder that throws leaves the last good image on screen, and then fails the re-decode.
    /// There is no published snapshot any more, so every reader falls through to the legacy
    /// fallback in <c>TryGetPixelSnapshot</c>.</para>
    /// <para>That fallback validated the buffer against <c>_width</c>/<c>_height</c>. Now that
    /// those carry the canonical size, reading them there asks whether a 128x64 buffer holds
    /// 1024x512 pixels, fails, and reports "no pixels" — turning the deliberate "keep the last good
    /// image" behaviour into a blank element with nothing to explain it. It reads the raster pair
    /// instead, and carries the canonical numbers through untouched.</para>
    /// </remarks>
    [Fact]
    public void AFailedReloadKeepsTheResidentBucketReadableThroughTheLegacyFallback()
    {
        var failReload = false;
        var decoder = new RecordingImageDecoder
        {
            NaturalWidth = 1024,
            NaturalHeight = 512,
            DecodeFault = () => Volatile.Read(ref failReload) ? new InvalidDataException("reload") : null,
        };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        // Not awaited: the StreamSource assignment below is a dependency-property write.
        ImagePipelineTestHarness.AwaitDecodeOnThisThread(
            image.RequestDecodeAsync(100, 50, cover: false));
        Assert.Equal((128, 64), ImagePipelineTestHarness.PublishedBucket(image));

        // Assigning a source after EndInit is Jalium's documented extension over WPF's one-shot
        // initialization, and it is the ordinary route into this state. The exception TYPE is
        // deliberately not pinned here — that belongs to the failure-contract suite — only that
        // the reload failed.
        Volatile.Write(ref failReload, true);
        using var stream = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
        Assert.NotNull(Record.Exception(() => image.StreamSource = stream));

        // No published snapshot survives a ResetDeferredSource, so this answer comes from the
        // legacy fallback — and it is the previous, still perfectly drawable, raster.
        Assert.True(image.TryGetPixelSnapshot(out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(128, snapshot!.Width);
        Assert.Equal(64, snapshot.Height);
        Assert.Equal(1024, snapshot.CanonicalWidth);
        Assert.Equal(512, snapshot.CanonicalHeight);
        Assert.True(snapshot.Pixels.Length >= snapshot.Stride * snapshot.Height);
        Assert.Equal(1024, image.PixelWidth);
        Assert.Equal(128, image.RasterPixelWidth);
    }

    /// <summary>
    /// The ratchet, closed. An <c>Image</c> whose stretch mode makes the draw rect equal to the
    /// intrinsic size settles after ONE upgrade instead of climbing a bucket per frame.
    /// </summary>
    /// <remarks>
    /// <para>The loop below is the feedback edge itself, written out: measure the element, take the
    /// size it decided to draw at, and hand that size back to the decoder exactly as
    /// <c>RenderTargetDrawingContext.DrawImage</c> does through <c>ToDeviceHint</c>. With the
    /// bucket mirrored into the intrinsic size this converges only when the ladder reaches the
    /// natural size — 128, 256, 512, 1024 for this source, i.e. four decodes and four
    /// measure/arrange passes for an element that never changed size. With the canonical size it
    /// takes at most two: the first publish (whatever the slot asked for) and one upgrade to the
    /// size <c>Stretch.None</c> actually draws at.</para>
    /// <para>The DesiredSize assertion is the other half. It is what
    /// <c>Image.OnSourceAsyncLoaded</c> compares to decide whether a publish needs a re-measure at
    /// all, so a DesiredSize that moved with the bucket put a full layout pass behind every
    /// decode.</para>
    /// </remarks>
    [Fact]
    public void AStretchNoneImageSettlesWithoutRatchetingItsOwnDecodeSize()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 1024, NaturalHeight = 512 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var bitmap = file.CreateDeferredImage();

        var element = new ImageControl
        {
            Source = bitmap,
            Stretch = Jalium.UI.Media.Stretch.None,
        };

        var slot = new Jalium.UI.Size(160, 80);
        var desiredSizes = new List<Jalium.UI.Size>();

        for (var frame = 0; frame < 8; frame++)
        {
            element.InvalidateMeasure();
            element.Measure(slot);
            element.Arrange(new Jalium.UI.Rect(0, 0, slot.Width, slot.Height));
            desiredSizes.Add(element.DesiredSize);

            // The renderer's edge: Stretch.None draws at the intrinsic size, and that rect becomes
            // the next decode hint.
            var drawWidth = element.DesiredSize.Width > 0 ? element.DesiredSize.Width : slot.Width;
            var drawHeight = element.DesiredSize.Height > 0 ? element.DesiredSize.Height : slot.Height;

            // Blocking rather than awaiting: Measure/Arrange read dependency properties, which are
            // thread-affine, and an await would resume the next iteration on a pool thread.
            ImagePipelineTestHarness.AwaitDecodeOnThisThread(
                bitmap.RequestDecodeAsync(
                    (int)Math.Ceiling(drawWidth), (int)Math.Ceiling(drawHeight), cover: false));
        }

        Assert.True(
            ImagePipelineTestHarness.SpinUntil(
                () => ImagePipelineTestHarness.PublishedBucket(bitmap) == (1024, 512),
                ImagePipelineTestHarness.DecodeTimeout),
            $"the ladder never reached the drawn size (bucket={ImagePipelineTestHarness.PublishedBucket(bitmap)})");

        Assert.True(
            decoder.DecodeCalls <= 2,
            $"the draw rect fed the decoder its own output: {decoder.DecodeCalls} decodes for an element that never changed size");

        // Every measure after the first publish agreed on the canonical size, so nothing after the
        // first one owed the subtree a layout pass.
        var settled = desiredSizes.Where(static size => size.Width > 0).ToList();
        Assert.NotEmpty(settled);
        Assert.All(settled, size =>
        {
            Assert.Equal(1024d, size.Width);
            Assert.Equal(512d, size.Height);
        });
    }

    /// <summary>
    /// An element measures at the source's real size as soon as the HEADER has been read, without
    /// waiting for — or getting — any pixels.
    /// </summary>
    /// <remarks>
    /// <para>WPF decodes inside <c>EndInit</c>, so a URI-backed <c>BitmapImage</c> has a size the
    /// instant the property setter returns and the very first measure is correct. This pipeline
    /// decodes off the UI thread on purpose, and until now nothing armed the metadata lane at all —
    /// <c>RequestMetadata</c> had no caller outside tests, so the scheduler's metadata queue and
    /// the whole probe path were unreachable production code. Every URI-backed image therefore
    /// measured at zero and the page reflowed when the decode landed, which on a page whose layout
    /// depends on its images is a visible jump.</para>
    /// <para>The decode is parked in the gate for the whole test, so the size asserted below cannot
    /// have come from a published raster: it is the header probe's answer, delivered through the
    /// notifier on the UI thread, which is the half that makes the lane worth arming — an intrinsic
    /// size nothing announces is one the element only reads if it happens to measure again.</para>
    /// </remarks>
    [Fact]
    public void AnElementMeasuresAtTheHeaderSizeBeforeAnyPixelsArePublished()
    {
        using var gate = new ManualResetEventSlim(initialState: false);
        var decoder = new RecordingImageDecoder
        {
            NaturalWidth = 1024,
            NaturalHeight = 512,
            DecodeGate = gate,
        };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        var previousMainDispatcher = ReadMainDispatcherSlot();
        try
        {
            // Own the main-dispatcher slot so the notifier QUEUES its announcement instead of
            // delivering it inline on the decode worker — the element's InvalidateMeasure has to
            // run on the thread that owns the element, here as in an application.
            Dispatcher.SetAsMainThread();
            BitmapDecodeNotifier.FlushPendingForTests();

            using var bitmap = file.CreateDeferredImage();
            var element = new ImageControl
            {
                Source = bitmap,
                Stretch = Jalium.UI.Media.Stretch.None,
            };

            var slot = new Jalium.UI.Size(160, 80);
            element.Measure(slot);

            // The measure pass is what arms the probe; it does not wait for it.
            Assert.True(
                ImagePipelineTestHarness.SpinUntil(
                    () => decoder.DimensionCalls >= 1, ImagePipelineTestHarness.DecodeTimeout),
                "measuring an unsized deferred source never armed the header probe");

            // Flushed in the loop because the announcement is posted just after the dimensions are
            // stored, so a single flush could land in between.
            Assert.True(
                ImagePipelineTestHarness.SpinUntil(
                    () =>
                    {
                        BitmapDecodeNotifier.FlushPendingForTests();
                        return !element.IsMeasureValid;
                    },
                    ImagePipelineTestHarness.DecodeTimeout),
                "the resolved intrinsic size was never announced, so nothing re-measured");

            element.Measure(slot);

            Assert.Equal(1024d, element.DesiredSize.Width);
            Assert.Equal(512d, element.DesiredSize.Height);

            // And it really is the header's answer: the decode is still parked in the gate, so no
            // raster exists at all.
            Assert.True(bitmap.IsDeferredDecodePending);
            Assert.Null(bitmap.RawPixelData);
        }
        finally
        {
            gate.Set();
            RestoreMainDispatcherSlot(previousMainDispatcher);
        }
    }

    /// <summary>
    /// The metadata lane stays bounded now that a per-pass layout caller actually drives it.
    /// </summary>
    /// <remarks>
    /// <para>This is the same bound <c>AFailingMetadataProbeStopsInsteadOfReReadingTheSourceForever</c>
    /// pins, re-measured through the production caller rather than through direct
    /// <c>RequestMetadata</c> calls, because the lane was unreachable when that bound was written.
    /// A probe that always throws leaves the dimensions at zero, so every single measure pass finds
    /// the same "no intrinsic size" state and asks again — RC1's exact shape, one lane over: native
    /// work driven by a layout loop with nothing recording that it already failed.</para>
    /// <para>The encoded bytes are cached BEFORE the header is parsed, so the two retries do not
    /// re-open the file either; <c>ImageData</c> being resident after a probe that never succeeded
    /// is what shows that.</para>
    /// </remarks>
    [Fact]
    public void MeasuringAnUnreadableSourceEveryPassStillProbesAtMostThreeTimes()
    {
        using var gate = new ManualResetEventSlim(initialState: false);
        var decoder = new RecordingImageDecoder
        {
            NaturalWidth = 1024,
            NaturalHeight = 512,
            DimensionFault = static () => new InvalidOperationException("no header reader"),
            DecodeGate = gate,
        };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        try
        {
            using var bitmap = file.CreateDeferredImage();
            var element = new ImageControl
            {
                Source = bitmap,
                Stretch = Jalium.UI.Media.Stretch.None,
            };

            var slot = new Jalium.UI.Size(160, 80);
            var passes = 0;
            var driving = System.Diagnostics.Stopwatch.StartNew();
            while (driving.Elapsed < TimeSpan.FromSeconds(2))
            {
                element.InvalidateMeasure();
                element.Measure(slot);
                passes++;
                Thread.Yield();
            }

            Assert.True(passes > 100, $"only {passes} measure passes — the drive loop proves nothing");
            Assert.True(decoder.DimensionCalls >= 1, "the measure pass never armed the header probe");
            Assert.True(
                decoder.DimensionCalls <= 3,
                $"{decoder.DimensionCalls} header probes for {passes} measure passes — " +
                "arming the metadata lane from layout made it unbounded");

            // Cached ahead of the parse, so the attempts that did run share one read of the file.
            Assert.NotNull(bitmap.ImageData);
        }
        finally
        {
            gate.Set();
        }
    }

    private static object? ReadMainDispatcherSlot() => MainDispatcherField.GetValue(null);

    private static void RestoreMainDispatcherSlot(object? value) =>
        MainDispatcherField.SetValue(null, value);

    /// <summary>
    /// The process main-dispatcher slot. A test that needs the notifier to QUEUE rather than
    /// deliver inline has to own it, because the first <c>GetForCurrentThread</c> anywhere in the
    /// process claims it permanently.
    /// </summary>
    private static readonly FieldInfo MainDispatcherField =
        (typeof(Dispatcher).Assembly.GetType("Jalium.UI.Threading.DispatcherCore")
            ?? throw new InvalidOperationException("DispatcherCore type not found."))
        .GetField("_mainDispatcher", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DispatcherCore._mainDispatcher field not found.");
}
