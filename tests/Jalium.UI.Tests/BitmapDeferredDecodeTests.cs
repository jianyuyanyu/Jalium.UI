using Jalium.UI.Media.Imaging;

namespace Jalium.UI.Tests;

// BitmapImage.SetDecoder installs a PROCESS-GLOBAL decoder, so every test class that injects one
// must be serialized against the others or a decoder swap lands mid-decode in an unrelated test.
[Collection("Application")]
public sealed class BitmapDeferredDecodeTests
{
    private sealed class DelayedDecoder : INativeImageDecoder
    {
        private int _decodeCalls;
        private int _concurrentDecodes;
        private int _maxConcurrentDecodes;

        public int DecodeCalls => Volatile.Read(ref _decodeCalls);
        public int MaxConcurrentDecodes => Volatile.Read(ref _maxConcurrentDecodes);

        public DecodedImage Decode(
            ReadOnlySpan<byte> data,
            NativePixelFormat requestedFormat = NativePixelFormat.Bgra8)
        {
            Interlocked.Increment(ref _decodeCalls);
            var concurrent = Interlocked.Increment(ref _concurrentDecodes);
            UpdateMaximum(ref _maxConcurrentDecodes, concurrent);
            try
            {
                Thread.Sleep(35);
                const int width = 1024;
                const int height = 512;
                var stride = width * 4;
                return new DecodedImage(new byte[stride * height], width, height, stride, requestedFormat);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentDecodes);
            }
        }

        public DecodedImage Decode(Stream stream, NativePixelFormat requestedFormat = NativePixelFormat.Bgra8)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return Decode(copy.ToArray(), requestedFormat);
        }

        public DecodedImage DecodeFile(string filePath, NativePixelFormat requestedFormat = NativePixelFormat.Bgra8)
            => Decode(File.ReadAllBytes(filePath), requestedFormat);

        public bool TryReadDimensions(ReadOnlySpan<byte> data, out int width, out int height)
        {
            width = 1024;
            height = 512;
            return true;
        }

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            while (true)
            {
                var observed = Volatile.Read(ref maximum);
                if (candidate <= observed ||
                    Interlocked.CompareExchange(ref maximum, candidate, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    [Fact]
    public async Task UriConstructionDefersDecodeUntilARealizedSizeIsRequested()
    {
        var decoder = new DelayedDecoder();
        BitmapImage.SetDecoder(decoder);
        var filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(filePath, [0x89, 0x50, 0x4E, 0x47]);
            using var image = new BitmapImage(new Uri(filePath));

            Assert.Equal(0, decoder.DecodeCalls);
            Assert.Null(image.RawPixelData);
            Assert.True(image.IsDeferredDecodePending);

            await image.RequestDecodeAsync(100, 50, cover: false).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, decoder.DecodeCalls);
            Assert.NotNull(image.RawPixelData);

            // The intrinsic size is the CANONICAL one — 1024x512 — not the 128x64 display bucket
            // the decode published for a 100x50 request. This assertion used to read 128x64, which
            // enshrined the defect: the bucket is a function of the DPI and of the layout slot, so
            // mirroring it into PixelWidth made the intrinsic size an OUTPUT of layout as well as
            // its input, and an <Image Stretch="None"/> then re-decoded and re-laid-out itself
            // several times as it grew into its own reported size.
            Assert.Equal(1024, image.PixelWidth);
            Assert.Equal(512, image.PixelHeight);

            // The bucket itself is still exactly what it was; it is just no longer the public size.
            Assert.Equal((128, 64), ImagePipelineTestHarness.PublishedBucket(image));
            Assert.Equal(128, image.RasterPixelWidth);
            Assert.Equal(64, image.RasterPixelHeight);
            Assert.False(image.IsDeferredDecodePending);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SchedulerBoundsConcurrentNativeDecodesAcrossImageBurst()
    {
        var decoder = new DelayedDecoder();
        BitmapImage.SetDecoder(decoder);
        var filePath = Path.GetTempFileName();
        var images = new List<BitmapImage>();
        try
        {
            await File.WriteAllBytesAsync(filePath, [0x89, 0x50, 0x4E, 0x47]);
            for (var i = 0; i < 8; i++)
            {
                var image = new BitmapImage(new Uri(filePath));
                images.Add(image);
            }

            var decodes = images
                .Select(image => image.RequestDecodeAsync(168, 72, cover: true))
                .ToArray();
            await Task.WhenAll(decodes).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(images.Count, decoder.DecodeCalls);
            // The scheduler's worker floor is 2 (a single worker let one non-terminating decode
            // blank every image in the process), and the stall watchdog may add exactly one
            // relief worker above the cap of 3.
            Assert.InRange(decoder.MaxConcurrentDecodes, 1, 4);
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// The task handed back by <c>RequestDecodeAsync</c> means "there are pixels for the size you
    /// asked for", not "the bucket ladder has finished climbing".
    /// </summary>
    /// <remarks>
    /// The completion source used to be signalled only on the branch that scheduled no upgrade, so
    /// an awaiter whose request was superseded mid-decode waited out the entire upgrade chain — and
    /// on the pathological chain this campaign fixed, waited forever.
    /// </remarks>
    [Fact]
    public async Task AwaitingRequestDecodeAsyncCompletesEvenWhenAnUpgradeIsScheduled()
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
        using var image = file.CreateDeferredImage();
        try
        {
            var pending = image.RequestDecodeAsync(100, 50, cover: false);

            // The in-flight decode has already latched 100x50; growing the request now can only be
            // served by a second decode.
            Assert.True(
                ImagePipelineTestHarness.SpinUntil(
                    () => decoder.ActiveDecodes >= 1, TimeSpan.FromSeconds(5)),
                "the first decode never started");
            image.RequestDecode(2000, 1000, cover: false);

            gate.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(2));

            // The first publish released the awaiter; the upgrade runs behind it. Observed on the
            // published BUCKET, not on PixelWidth: PixelWidth is the canonical 1024 from the very
            // first publish, so the old form of this wait was satisfied before the upgrade ran.
            Assert.True(
                ImagePipelineTestHarness.SpinUntil(
                    () => decoder.DecodeCalls == 2 &&
                          ImagePipelineTestHarness.PublishedBucket(image) == (1024, 512),
                    ImagePipelineTestHarness.DecodeTimeout),
                $"the upgrade never landed (decodes={decoder.DecodeCalls}, " +
                $"bucket={ImagePipelineTestHarness.PublishedBucket(image)})");
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// <c>SourceRect</c> is author-controlled and resolves against the NATURAL image, so the same
    /// asset cropped the same way produces byte-identical content at every display scaling.
    /// </summary>
    /// <remarks>
    /// <para>The crop used to be applied post-hoc, to whatever display bucket the decode happened
    /// to land on — so a rect an author measured against their 400x300 asset landed somewhere else
    /// entirely at 125%, and on a bucket smaller than the rect it threw outright. That makes an
    /// author-visible property silently DPI- and layout-dependent, which is the worst kind of
    /// parity gap: it works on the machine it was written on.</para>
    /// <para>The crop is also what defines the canonical size the bucket ladder measures against.
    /// Asserting it here is what stops a future change from anchoring the ladder on the uncropped
    /// natural size, where a request between the crop size and the natural size would look like
    /// reachable growth forever.</para>
    /// </remarks>
    [Fact]
    public async Task DecodeOptionsResolveAgainstNaturalCoordinatesAtEveryDpiScale()
    {
        var decoder = new RecordingImageDecoder
        {
            NaturalWidth = 400,
            NaturalHeight = 300,
            PositionEncodedPixels = true,
        };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();

        // The 100/125/150/200% display-scaling ladder for a 100-DIP slot.
        int[] requestedSizes = [100, 125, 150, 200];
        var probesByRequest = new Dictionary<int, byte[]>();

        foreach (var requested in requestedSizes)
        {
            // Constructed exactly the way the XAML type converter does it — BeginInit, properties,
            // EndInit — which is the only window in which decode options are read.
            using var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = file.Uri;
            image.SourceRect = new Jalium.UI.Int32Rect(10, 10, 100, 100);
            image.EndInit();

            await image
                .RequestDecodeAsync(requested, requested, cover: false)
                .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

            Assert.True(image.TryGetPixelSnapshot(out var snapshot));
            Assert.NotNull(snapshot);

            // The cropped image IS the canonical image: the ladder's ceiling is the author's
            // 100x100, not the 400x300 the file happens to hold.
            Assert.Equal(100, snapshot!.CanonicalWidth);
            Assert.Equal(100, snapshot.CanonicalHeight);

            // Every request is at or above the canonical size, and the resampler never upscales,
            // so all four publish the crop unchanged.
            Assert.Equal(100, snapshot.Width);
            Assert.Equal(100, snapshot.Height);

            probesByRequest[requested] = SampleCorners(snapshot);
        }

        var reference = probesByRequest[requestedSizes[0]];
        foreach (var requested in requestedSizes)
        {
            Assert.True(
                reference.AsSpan().SequenceEqual(probesByRequest[requested]),
                $"the crop moved between a {requestedSizes[0]}px and a {requested}px request — " +
                "SourceRect was resolved against the display bucket, not the natural image");
        }

        // Self-consistency is not enough: the crop must land where the AUTHOR put it. Each pixel
        // encodes its own natural coordinate, so the crop's top-left must read (10,10).
        Assert.Equal(10, reference[0]); // B = natural x
        Assert.Equal(10, reference[1]); // G = natural y

        // Bottom-right of a 100x100 crop starting at (10,10) is natural (109,109).
        Assert.Equal(109, reference[12]);
        Assert.Equal(109, reference[13]);

        // One decode per image, four images: the option transform runs inside the decode and is
        // never re-applied to an already-published raster.
        Assert.Equal(requestedSizes.Length, decoder.DecodeCalls);
    }

    /// <summary>
    /// An explicit <c>DecodePixelWidth</c> is the decode RESOLUTION, not an upper bound the display
    /// bucket may shrink further — and it ends the ladder on the first decode.
    /// </summary>
    /// <remarks>
    /// <para>WPF's contract is <c>PixelWidth == DecodePixelWidth</c> exactly, and it is the standard
    /// mechanism an application uses to bound image memory and to guarantee a known raster size for
    /// <c>CopyPixels</c> and encode work. Running the bucket ladder over it demoted the author's
    /// number to a hint, silently and DPI-dependently: this source asked for 800 and got a 128-wide
    /// thumbnail because the element it happened to be measured in was small.</para>
    /// <para>The second half of the test is the termination argument, measured rather than assumed.
    /// The producer and the upgrade predicate resolve the target through the SAME function, so a
    /// later request — larger or smaller — resolves to the size already published, compares as no
    /// gain, and enqueues nothing. A predicate that disagreed with the producer by even one pixel
    /// is RC1 exactly.</para>
    /// </remarks>
    [Fact]
    public void AnExplicitDecodeSizeIsPublishedVerbatimAndEndsTheLadderOnTheFirstDecode()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 1024, NaturalHeight = 512 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = file.Uri;
        image.DecodePixelWidth = 800;
        image.EndInit();

        // A 100x50 slot: the bucket ladder answers 128x64 for this source, which is what used to
        // be published for an author who asked for 800.
        ImagePipelineTestHarness.AwaitDecodeOnThisThread(
            image.RequestDecodeAsync(100, 50, cover: false));

        Assert.Equal((800, 400), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(800, image.PixelWidth);
        Assert.Equal(400, image.PixelHeight);
        Assert.Equal(800, image.RasterPixelWidth);
        Assert.Equal(400, image.RasterPixelHeight);
        Assert.Equal(1, decoder.DecodeCalls);

        // Neither growth nor shrinkage can reach a different answer, so no further native decode is
        // ever enqueued for this source version.
        ImagePipelineTestHarness.AwaitDecodeOnThisThread(
            image.RequestDecodeAsync(4000, 2000, cover: false));
        ImagePipelineTestHarness.AwaitDecodeOnThisThread(
            image.RequestDecodeAsync(16, 8, cover: false));

        Assert.Equal(1, decoder.DecodeCalls);
        Assert.Equal((800, 400), ImagePipelineTestHarness.PublishedBucket(image));
    }

    /// <summary>
    /// The same decode options produce the same raster whether the bytes arrive through a stream or
    /// through a URI.
    /// </summary>
    /// <remarks>
    /// The eager path (<c>StreamSource</c> -&gt; <c>AdoptDecoded</c>) has always applied the
    /// author's decode size with no bucket at all. While the deferred path bucketed it, identical
    /// markup produced a full-size raster one way and a thumbnail the other — a parity gap between
    /// two spellings of the same thing, invisible until someone read the pixels back.
    /// </remarks>
    [Fact]
    public void AnExplicitDecodeSizeAgreesBetweenTheEagerAndTheDeferredPath()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 1024, NaturalHeight = 512 };
        BitmapImage.SetDecoder(decoder);

        using var eager = new BitmapImage();
        using var stream = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
        eager.BeginInit();
        eager.StreamSource = stream;
        eager.DecodePixelWidth = 256;
        eager.EndInit();

        using var file = new TempImageFile();
        using var deferred = new BitmapImage();
        deferred.BeginInit();
        deferred.UriSource = file.Uri;
        deferred.DecodePixelWidth = 256;
        deferred.EndInit();

        ImagePipelineTestHarness.AwaitDecodeOnThisThread(
            deferred.RequestDecodeAsync(100, 50, cover: false));

        Assert.Equal(eager.PixelWidth, deferred.PixelWidth);
        Assert.Equal(eager.PixelHeight, deferred.PixelHeight);
        Assert.Equal(eager.RasterPixelWidth, deferred.RasterPixelWidth);
        Assert.Equal(eager.RasterPixelHeight, deferred.RasterPixelHeight);
        Assert.Equal((256, 128), ImagePipelineTestHarness.PublishedBucket(deferred));
    }

    /// <summary>
    /// <c>CacheOption.OnLoad</c> reads the file during <c>EndInit</c>, so the load-then-delete idiom
    /// works again.
    /// </summary>
    /// <remarks>
    /// <para>Downloading to a temp file, handing it to a <c>BitmapImage</c> and deleting it is a
    /// standard pattern, and so is overwriting an image in place after loading it. Both worked
    /// before the source became deferred, because the file was read inside the property setter.
    /// The deferred pipeline captures a path and reads it on a decode worker some time after the
    /// first layout pass, so the delete now wins the race — and <c>CacheOption</c>, the property
    /// that exists to say "read it all now", had no reader anywhere in the pipeline.</para>
    /// <para>The second half proves the option is what makes the difference rather than the timing
    /// of this particular test host: the same sequence with the default option reaches the file
    /// late and fails, loudly, with the real <see cref="FileNotFoundException"/>.</para>
    /// </remarks>
    [Fact]
    public void CacheOptionOnLoadReadsTheBytesEagerlySoTheFileMayBeDeletedImmediately()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 64, NaturalHeight = 64 };
        BitmapImage.SetDecoder(decoder);

        var eagerPath = Path.GetTempFileName();
        var lazyPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(eagerPath, [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllBytes(lazyPath, [0x89, 0x50, 0x4E, 0x47]);

            using var onLoad = new BitmapImage();
            onLoad.BeginInit();
            onLoad.CacheOption = BitmapCacheOption.OnLoad;
            onLoad.UriSource = new Uri(eagerPath);
            onLoad.EndInit();

            using var deferredRead = new BitmapImage();
            deferredRead.BeginInit();
            deferredRead.UriSource = new Uri(lazyPath);
            deferredRead.EndInit();

            File.Delete(eagerPath);
            File.Delete(lazyPath);

            // Still a deferred decode — only the byte acquisition moved forward — so this is the
            // ordinary request path and the raster is still sized for the slot.
            ImagePipelineTestHarness.AwaitDecodeOnThisThread(
                onLoad.RequestDecodeAsync(64, 64, cover: false));

            Assert.Equal(1, decoder.DecodeCalls);
            Assert.Equal(64, onLoad.PixelWidth);
            Assert.NotNull(onLoad.RawPixelData);

            var missed = Record.Exception(() =>
                ImagePipelineTestHarness.AwaitDecodeOnThisThread(
                    deferredRead.RequestDecodeAsync(64, 64, cover: false)));

            Assert.IsType<FileNotFoundException>(missed);
        }
        finally
        {
            File.Delete(eagerPath);
            File.Delete(lazyPath);
        }
    }

    /// <summary>
    /// A <c>CacheOption.OnLoad</c> source whose file cannot be read reports AND throws from
    /// <c>EndInit</c>, rather than deferring the discovery to a decode worker.
    /// </summary>
    /// <remarks>
    /// Throwing is the point of the option — the caller asked to find out now — and it is what both
    /// WPF and this framework's own pre-deferred <c>LoadFromFile</c> did. It must still be reported
    /// on the source, because <c>Image</c> learns about a broken source through
    /// <c>LoadFailure</c>/<c>LoadFailed</c> and not through the exception the setter threw.
    /// </remarks>
    [Fact]
    public void CacheOptionOnLoadReportsAndThrowsWhenTheEagerReadFails()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 64, NaturalHeight = 64 };
        BitmapImage.SetDecoder(decoder);

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);

            // An exclusive handle is the transient shape this path has to survive: an on-access
            // scanner or another process holding the asset open.
            using var exclusive = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None);

            using var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);

            var failure = Record.Exception(image.EndInit);

            Assert.NotNull(failure);
            Assert.IsAssignableFrom<IOException>(failure);
            Assert.Same(failure, image.LoadFailure);
            Assert.Equal(0, decoder.DecodeCalls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// <c>PreloadAsync</c> is the public "I know this source is about to be shown" hook: it decodes
    /// a deferred source without anything drawing it, at the display bucket the given device-pixel
    /// slot resolves to, and completes once those pixels are published.
    /// </summary>
    /// <remarks>
    /// Before it existed the only way to get ahead of the first draw was the eager
    /// <c>FromBytes</c> path — a synchronous full-resolution decode that also disables the bucket
    /// ladder — so a sprite animation built from URI-backed frames had each frame miss its first
    /// draw (the decode started when the frame was shown and the frame painted nothing until it
    /// landed), which read as a flicker on the first playback and never again.
    /// </remarks>
    [Fact]
    public async Task PreloadAsyncDecodesADeferredSourceBeforeAnythingDrawsIt()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 1024, NaturalHeight = 512 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        Assert.Equal(0, decoder.DecodeCalls);
        Assert.True(image.IsDeferredDecodePending);

        await image.PreloadAsync(100, 50).WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        // Pixels are resident at the bucket the slot resolves to, so a draw at that size can no
        // longer miss; the intrinsic size is canonical, exactly as after a draw-driven decode.
        Assert.Equal(1, decoder.DecodeCalls);
        Assert.False(image.IsDeferredDecodePending);
        Assert.True(image.TryGetPixelSnapshot(out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal((128, 64), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(1024, image.PixelWidth);
        Assert.Equal(512, image.PixelHeight);

        // Idempotent: a request the published bucket already covers is a completed task and no
        // decode, so a caller may re-preload on every state change without cost.
        var repeat = image.PreloadAsync(100, 50);
        Assert.True(repeat.IsCompletedSuccessfully);
        Assert.Equal(1, decoder.DecodeCalls);

        // A larger slot is an upgrade through the same ladder. The previous pixels stay drawable
        // until the new bucket lands (the snapshot is replaced, never cleared).
        await image.PreloadAsync(1000, 500).WaitAsync(ImagePipelineTestHarness.DecodeTimeout);
        Assert.Equal(2, decoder.DecodeCalls);
        Assert.Equal((1024, 512), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.True(image.TryGetPixelSnapshot(out _));
    }

    [Fact]
    public void PreloadAsyncRejectsNegativeSizesAndIsANoOpForAnEagerSource()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 16, NaturalHeight = 16 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var deferred = file.CreateDeferredImage();
        // The guard throws synchronously, before any task exists — an Action lambda, deliberately,
        // so the assertion sees the throw itself and not a faulted Task.
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = deferred.PreloadAsync(-1, 10); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = deferred.PreloadAsync(10, -1); });

        // An eager source already owns its pixels: nothing to wait for, nothing scheduled.
        var eager = BitmapImage.FromBytes([0x89, 0x50, 0x4E, 0x47]);
        var calls = decoder.DecodeCalls;
        Assert.True(eager.PreloadAsync(10, 10).IsCompletedSuccessfully);
        Assert.Equal(calls, decoder.DecodeCalls);
    }

    /// <summary>
    /// The four corner pixels of a publication, in BGRA order, as one 16-byte probe.
    /// </summary>
    private static byte[] SampleCorners(BitmapPixelSnapshot snapshot)
    {
        var probe = new byte[16];
        (int X, int Y)[] corners =
        [
            (0, 0),
            (snapshot.Width - 1, 0),
            (0, snapshot.Height - 1),
            (snapshot.Width - 1, snapshot.Height - 1),
        ];

        for (var i = 0; i < corners.Length; i++)
        {
            var offset = (corners[i].Y * snapshot.Stride) + (corners[i].X * 4);
            snapshot.Pixels.AsSpan(offset, 4).CopyTo(probe.AsSpan(i * 4));
        }

        return probe;
    }
}
