using Jalium.UI.Media.Imaging;

namespace Jalium.UI.Tests;

/// <summary>
/// Pins the decode scheduler's two liveness rules: a worker floor that no single image can consume,
/// and metadata jobs that a burst of pixel decodes cannot delay.
/// </summary>
/// <remarks>
/// The worker cap used to be <c>Math.Clamp(ProcessorCount / 4, 1, 2)</c>, which is exactly ONE
/// worker on any machine with seven or fewer logical cores. That is the amplifier that turned a
/// single non-terminating decode into a whole page of blank images: the trapped decode owned the
/// only worker, so every other image in the process stayed queued forever. The decode loop is
/// bounded now, but the floor stays — a bounded loop is not the same as a fast one, and a decoder
/// blocked on a network share must not be able to take the page with it.
/// </remarks>
[Collection("Application")]
public sealed class BitmapDecodeSchedulerTests
{
    [Fact]
    public void SchedulerRunsAtLeastTwoWorkersSoOneImageCannotOwnThePool()
    {
        Assert.True(
            BitmapDecodeScheduler.MaxWorkers >= 2,
            $"the decode pool must never drop to a single worker (MaxWorkers={BitmapDecodeScheduler.MaxWorkers})");
    }

    /// <summary>
    /// Every image on a page asks for the shape that used to decode forever. All of them must
    /// still finish, and each must cost exactly one native decode.
    /// </summary>
    /// <remarks>
    /// A 24x24 source requested at 48x48 is the reported 200%-display case. When that request never
    /// terminated, N such images saturated any finite worker pool and the whole page rendered
    /// blank, which is why this test uses the fatal shape for every image rather than mixing in
    /// healthy ones.
    /// </remarks>
    [Fact]
    public async Task AnUnsatisfiableRequestOnEveryImageDoesNotStarveThePool()
    {
        const int imageCount = 8;
        var decoder = new RecordingImageDecoder { NaturalWidth = 24, NaturalHeight = 24 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        var images = new List<BitmapImage>(imageCount);
        try
        {
            for (var i = 0; i < imageCount; i++)
            {
                images.Add(file.CreateDeferredImage());
            }

            var decodes = images
                .Select(image => image.RequestDecodeAsync(48, 48, cover: false))
                .ToArray();
            await Task.WhenAll(decodes).WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

            Assert.Equal(imageCount, decoder.DecodeCalls);
            Assert.All(images, image =>
            {
                Assert.NotNull(image.RawPixelData);
                Assert.Equal(24, image.PixelWidth);
                Assert.False(image.IsDeferredDecodePending);
            });

            // Nothing re-enqueued itself behind our back.
            Assert.True(
                ImagePipelineTestHarness.SpinUntil(
                    () => BitmapDecodeScheduler.QueueLength == 0 &&
                          BitmapDecodeScheduler.ActiveWorkers == 0,
                    TimeSpan.FromSeconds(3)),
                $"the scheduler never went idle (queue={BitmapDecodeScheduler.QueueLength}, " +
                $"workers={BitmapDecodeScheduler.ActiveWorkers})");
            Assert.Equal(imageCount, decoder.DecodeCalls);
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>
    /// Even a pipeline that can only run ONE decode at a time must finish every image on the page.
    /// </summary>
    /// <remarks>
    /// <para>This is the reported machine: seven or fewer logical cores, where the old worker
    /// formula produced a single decode worker. The blackout was not caused by the worker count on
    /// its own — it was the product of a single worker and a decode that never terminated, and
    /// removing either factor removes the blackout. The floor removes one; this test pins the
    /// other, by forcing decode concurrency back to one and asserting the page still completes.</para>
    /// <para>Serialization is imposed at the decoder rather than at the scheduler because the worker
    /// count is resolved once per process from <c>Environment.ProcessorCount</c> and cannot be
    /// varied per test — and serializing at the decoder holds for any pool size, so this test says
    /// the same thing on a 4-core and a 64-core host.</para>
    /// </remarks>
    [Fact]
    public async Task ASingleWorkerPipelineStillFinishesEveryImageOnThePage()
    {
        const int imageCount = 8;
        var decoder = new RecordingImageDecoder
        {
            NaturalWidth = 24,
            NaturalHeight = 24,
            DecodeSerializer = new object(),
        };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        var images = new List<BitmapImage>(imageCount);
        try
        {
            for (var i = 0; i < imageCount; i++)
            {
                images.Add(file.CreateDeferredImage());
            }

            // The unsatisfiable 200%-display shape on every image: a 24x24 source asked for at 48.
            var decodes = images
                .Select(image => image.RequestDecodeAsync(48, 48, cover: false))
                .ToArray();
            await Task.WhenAll(decodes).WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

            Assert.Equal(1, decoder.MaxConcurrentDecodes);
            Assert.Equal(imageCount, decoder.DecodeCalls);
            Assert.All(images, image =>
            {
                Assert.False(image.IsDeferredDecodePending);
                Assert.Equal(24, image.PixelWidth);
            });
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>
    /// A metadata job queued behind a burst of pixel decodes runs as soon as any worker frees up,
    /// never after the rest of the burst.
    /// </summary>
    /// <remarks>
    /// <para>A metadata probe reads a header and returns in microseconds while a pixel decode can
    /// hold a worker for hundreds of milliseconds, and the very first layout pass needs intrinsic
    /// sizes. Draining metadata first is what keeps an image-heavy page from measuring at zero.</para>
    /// <para><b>Why permits and not an event.</b> The decoder's completion counter advances when a
    /// decode LEAVES the decoder, while the metadata order is stamped when the probe runs — after
    /// its worker has re-dequeued and re-read the source bytes. Opening a
    /// <c>ManualResetEventSlim</c> releases every queued decode at once, so the remaining pixel
    /// jobs run un-gated and the two measurements race: this test was written that way and
    /// measured positions 5 through 9 for the same, correct, scheduler. Releasing exactly
    /// <c>MaxWorkers</c> permits frees exactly the decodes already parked in the decoder and
    /// nothing else, so the position the probe lands at is a fact about the dequeue order rather
    /// than about thread-pool timing.</para>
    /// </remarks>
    [Fact]
    public async Task MetadataJobsAreDrainedAheadOfQueuedPixelJobs()
    {
        const int pixelJobCount = 8;
        var workers = BitmapDecodeScheduler.MaxWorkers;
        // Deliberately unbounded: the finally below over-releases so a failed assertion cannot
        // leave a decode worker parked in the decoder for the rest of the run.
        using var permits = new SemaphoreSlim(0);
        var decoder = new RecordingImageDecoder
        {
            NaturalWidth = 64,
            NaturalHeight = 64,
            DecodePermits = permits,
        };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        var images = new List<BitmapImage>(pixelJobCount);
        BitmapImage? metadataImage = null;
        try
        {
            for (var i = 0; i < pixelJobCount; i++)
            {
                images.Add(file.CreateDeferredImage());
            }

            var decodes = images
                .Select(image => image.RequestDecodeAsync(64, 64, cover: false))
                .ToArray();

            // Every worker is now parked inside the decoder and the rest of the burst is queued, so
            // nothing can dequeue between here and the metadata enqueue below.
            Assert.True(
                ImagePipelineTestHarness.SpinUntil(
                    () => decoder.ActiveDecodes >= workers && BitmapDecodeScheduler.QueueLength >= 1,
                    TimeSpan.FromSeconds(5)),
                "the pixel burst never filled the worker pool");

            metadataImage = file.CreateDeferredImage();
            metadataImage.RequestMetadata();

            // Exactly the parked decodes, so every worker frees up exactly once. Any pixel job a
            // worker picks up next blocks immediately on an exhausted permit, which is what makes
            // the assertion below an equality rather than a range.
            permits.Release(workers);

            Assert.True(
                ImagePipelineTestHarness.SpinUntil(
                    () => decoder.MetadataCompletionOrder > 0,
                    ImagePipelineTestHarness.DecodeTimeout),
                "the metadata job never ran");

            var order = decoder.MetadataCompletionOrder;
            Assert.True(
                order == workers + 1,
                $"metadata ran at position {order}, behind more than the {workers} decodes that " +
                "were already in flight when it was queued");

            Assert.True(metadataImage.TryGetNaturalMetadataSize(out var width, out var height));
            Assert.Equal(64, width);
            Assert.Equal(64, height);

            permits.Release(pixelJobCount - workers);
            await Task.WhenAll(decodes).WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

            // A metadata job must not decode pixels: one probe, no extra decode.
            Assert.Equal(pixelJobCount, decoder.DecodeCalls);
            Assert.Equal(1, decoder.DimensionCalls);
        }
        finally
        {
            permits.Release(pixelJobCount);
            metadataImage?.Dispose();
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }
}
