using System.Diagnostics;
using System.Reflection;
using Jalium.UI.Diagnostics;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Media.Native;
using Jalium.UI.Threading;

namespace Jalium.UI.Tests;

/// <summary>
/// Pins the observability and publication contracts of the image pipeline: a failure must be
/// visible in a <b>Release</b> build, a completion must reach the thread that can act on it, and a
/// published raster must never be readable as a buffer paired with someone else's dimensions.
/// </summary>
/// <remarks>
/// The reported symptom was a black image with no error anywhere. The reason nothing was logged is
/// that the only error output on the upload and restore paths was <c>Debug.WriteLine</c>, which is
/// <c>[Conditional("DEBUG")]</c> and therefore absent from the builds users run. Every test in this
/// class must therefore also pass under <c>dotnet test -c Release</c>.
/// </remarks>
[Collection("Application")]
public sealed class ImagePipelineFailureContractTests
{
    private sealed class DecoderFault : InvalidOperationException
    {
        internal DecoderFault()
            : base("injected decoder fault")
        {
        }
    }

    /// <summary>
    /// A decode failure must reach <see cref="ImageDiagnostics"/> and the awaiting caller. This is
    /// the channel that replaced the compiled-out <c>Debug.WriteLine</c>, so it is asserted through
    /// the public event and the always-live counters rather than through any build-conditional API.
    /// </summary>
    [Fact]
    public async Task DecodeFailureIsReportedThroughTheReleaseLiveDiagnosticsChannel()
    {
        var decoder = new RecordingImageDecoder { DecodeFault = static () => new DecoderFault() };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        var before = ImageDiagnostics.Snapshot().DecodesFailed;

        await Assert.ThrowsAsync<DecoderFault>(() =>
            image.RequestDecodeAsync(64, 64, cover: false)
                .WaitAsync(ImagePipelineTestHarness.DecodeTimeout));

        Assert.True(
            recorder.WaitForCount(
                ImageDiagnosticKind.DecodeFailed, 1, ImagePipelineTestHarness.DecodeTimeout),
            "a decode failure produced no ImageDiagnostics record");

        // Deliver the notification half as well, before counting. The worker writes the record and
        // the UI-thread drain latches the failure into the source, and until BOTH have run the
        // "exactly one record" assertion below is vacuous: nothing had yet reached the site that
        // could produce a second one. That is the site the pipeline pairs with
        // ReportDiagnosedLoadFailure precisely so one failure is not counted twice.
        Assert.True(
            ImagePipelineTestHarness.SpinUntil(
                () =>
                {
                    BitmapDecodeNotifier.FlushPendingForTests();
                    return image.LoadFailure is not null;
                },
                ImagePipelineTestHarness.DecodeTimeout),
            "the decode failure never reached the source's own failure chain");

        var failure = Assert.Single(
            recorder.Events, e => e.Kind == ImageDiagnosticKind.DecodeFailed);
        Assert.IsType<DecoderFault>(failure.Error);
        Assert.Equal(file.DiagnosticSource, failure.Source);
        Assert.False(string.IsNullOrEmpty(failure.Detail));

        // The always-live counter must move too: a support capture on a user's machine reads
        // Snapshot(), not the event.
        Assert.True(ImageDiagnostics.Snapshot().DecodesFailed > before);

        // A failed decode is terminal for this source version — it must not re-enqueue itself.
        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));
        Assert.Equal(1, decoder.DecodeCalls);
    }

    /// <summary>
    /// Structural guard against the original defect coming back by way of the attribute rather than
    /// the call site: no reporting entry point may be compiled out of a Release build.
    /// </summary>
    [Fact]
    public void NoImageDiagnosticsReportingEntryPointIsCompiledOutOfRelease()
    {
        var reporters = typeof(ImageDiagnostics)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(void))
            .ToArray();

        Assert.NotEmpty(reporters);
        foreach (var reporter in reporters)
        {
            Assert.Null(reporter.GetCustomAttribute<ConditionalAttribute>());
        }

        // And the counters really are readable without any tracing switch being on.
        _ = ImageDiagnostics.Snapshot();
    }

    /// <summary>
    /// A completion must be raised on the main dispatcher even when the bitmap was constructed on a
    /// thread-pool thread.
    /// </summary>
    /// <remarks>
    /// The decode scheduler dispatches through <c>ThreadPool.UnsafeQueueUserWorkItem</c>, so a
    /// bitmap constructed on a pooled thread can have its decode run on that same pooled thread.
    /// The old notifier tested <c>CheckAccess()</c> against the CONSTRUCTION thread's dispatcher,
    /// which then returned true and ran <c>OnImageLoaded</c> — and through it
    /// <c>InvalidateMeasure</c>/<c>InvalidateVisual</c> — off the UI thread. This test creates a
    /// dispatcher on the construction thread precisely so that mistake would be reproducible.
    /// </remarks>
    /// <remarks>
    /// Deliberately NOT an <c>async Task</c> test. <c>ProcessQueue</c> is thread-affine, and an
    /// awaited continuation resumes on a pool thread, so an async version of this test throws
    /// <see cref="InvalidOperationException"/> from its own pump before it can assert anything.
    /// Everything therefore stays on the thread that claimed the main-dispatcher slot.
    /// </remarks>
    [Fact]
    public void DecodeCompletionNotifiesTheMainDispatcherWhenConstructedOnAThreadPoolThread()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 64, NaturalHeight = 64 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        var previousMainDispatcher = ReadMainDispatcherSlot();
        try
        {
            Dispatcher.SetAsMainThread();
            BitmapDecodeNotifier.FlushPendingForTests();
            var dispatcher = Dispatcher.MainDispatcher;
            Assert.NotNull(dispatcher);
            Assert.Same(Thread.CurrentThread, dispatcher.Thread);

            // Blocking is the point: everything after this must stay on the thread that owns the
            // main dispatcher, because ProcessQueue below is thread-affine.
#pragma warning disable xUnit1031
            using var image = Task.Run(() =>
            {
                // Give the construction thread a dispatcher of its own — that is the thing the old
                // code marshalled to.
                _ = Dispatcher.CurrentDispatcher;
                return file.CreateDeferredImage();
            }).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

            var raisedOn = new Thread?[1];
            image.OnImageLoaded += (_, _) => Volatile.Write(ref raisedOn[0], Thread.CurrentThread);

            var decode = image.RequestDecodeAsync(64, 64, cover: false);

            // Nothing may have run yet: the completion is queued, not delivered inline.
            var delivered = ImagePipelineTestHarness.SpinUntil(
                () =>
                {
                    dispatcher.ProcessQueue();
                    return Volatile.Read(ref raisedOn[0]) is not null;
                },
                ImagePipelineTestHarness.DecodeTimeout);

            Assert.True(decode.IsCompletedSuccessfully, "the decode never completed");
            Assert.True(delivered, "the decode completion never reached the main dispatcher");
            Assert.Same(dispatcher.Thread, Volatile.Read(ref raisedOn[0]));
            Assert.Same(Thread.CurrentThread, Volatile.Read(ref raisedOn[0]));
        }
        finally
        {
            RestoreMainDispatcherSlot(previousMainDispatcher);
        }
    }

    /// <summary>
    /// A main-dispatcher slot owned by a thread that has already exited must not silently swallow
    /// every decode completion in the process.
    /// </summary>
    /// <remarks>
    /// <c>DispatcherCore.GetForCurrentThread</c> does <c>_mainDispatcher ??= dispatcher</c>, so the
    /// first thread to touch any dispatcher owns the slot until <c>Application</c>'s constructor
    /// replaces it — and a decode worker that merely constructed a <c>BitmapImage</c> can be that
    /// thread. The notifier used to post the drain to it and clear its "a drain is scheduled" flag
    /// only from inside that drain, so the flag latched at 1 forever: every image in the process
    /// published pixels, no <c>Image</c> was ever invalidated, and the whole page rendered blank
    /// with no counter, no event and no log line.
    /// </remarks>
    [Fact]
    public void DecodeNotificationsSurviveAMainDispatcherOwnedByAnExitedThread()
    {
        BitmapImage.SetDecoder(new RecordingImageDecoder());

        using var file = new TempImageFile();
        var previousMainDispatcher = ReadMainDispatcherSlot();
        var batches = 0;
        void OnBatchCompleted() => batches++;

        try
        {
            BitmapDecodeNotifier.FlushPendingForTests();

            var claimant = new Thread(static () => Dispatcher.SetAsMainThread())
            {
                IsBackground = true,
                Name = "main-dispatcher-slot-claimant",
            };
            claimant.Start();
            Assert.True(claimant.Join(TimeSpan.FromSeconds(10)), "the claimant thread never exited");
            Assert.False(claimant.IsAlive);
            Assert.Same(claimant, Dispatcher.MainDispatcher?.Thread);

            ImageSource.ContentChangedBatchCompleted += OnBatchCompleted;

            using var first = file.CreateDeferredImage();
            using var second = file.CreateDeferredImage();

            // Nothing in this process can run a queued callback, so the only correct behaviour is
            // to deliver on the calling thread — synchronously, with no pump anywhere.
            BitmapDecodeNotifier.Post(first, sourceVersion: 0, success: true, error: null);
            Assert.Equal(1, batches);

            // And the second post must still be delivered. This is the latch: the old flag was
            // cleared only by a drain that never ran, so every later post short-circuited.
            BitmapDecodeNotifier.Post(second, sourceVersion: 0, success: true, error: null);
            Assert.Equal(2, batches);
        }
        finally
        {
            ImageSource.ContentChangedBatchCompleted -= OnBatchCompleted;
            RestoreMainDispatcherSlot(previousMainDispatcher);
        }
    }

    /// <summary>
    /// A GPU upload failure must reach the source's <c>LoadFailed</c> chain — and therefore
    /// <c>Image.ImageFailed</c> — on the UI thread, once per failure episode.
    /// </summary>
    /// <remarks>
    /// <para>Reporting an upload failure to <see cref="ImageDiagnostics"/> alone gives a support
    /// engineer a counter and gives the application nothing: an app that handles
    /// <c>Image.ImageFailed</c> to swap in a fallback showed a black rectangle instead, because no
    /// upload site routed the failure into the chain a decode failure already used.</para>
    /// <para>Two properties matter beyond "it arrives". It must not be RAISED where it is
    /// discovered — <c>GetNativeBitmap</c> runs on the render thread whenever
    /// <c>JALIUM_RENDER_THREAD</c> is on, which is the default on Windows, and the chain reaches a
    /// routed-event raise and a dependency-property write. And it must be deduplicated, because the
    /// upload is retried on every frame and an undeduplicated report raises <c>ImageFailed</c>
    /// sixty times a second.</para>
    /// </remarks>
    [Fact]
    public void AnUploadFailureReachesTheSourceLoadFailedChainOncePerEpisodeOnTheUIThread()
    {
        BitmapImage.SetDecoder(new RecordingImageDecoder());

        using var file = new TempImageFile();
        var previousMainDispatcher = ReadMainDispatcherSlot();
        try
        {
            Dispatcher.SetAsMainThread();
            BitmapDecodeNotifier.FlushPendingForTests();
            var dispatcher = Dispatcher.MainDispatcher;
            Assert.NotNull(dispatcher);
            dispatcher!.ProcessQueue();

            using var image = file.CreateDeferredImage();
            var failures = new List<Exception>();
            Thread? raisedOn = null;
            image.LoadFailed += (_, ex) =>
            {
                failures.Add(ex);
                raisedOn = Thread.CurrentThread;
            };

            var error = new InvalidOperationException("gpu upload failed");

            // Three frames' worth of the same retried upload, all before the UI thread runs.
            BitmapDecodeNotifier.PostSourceFailure(image, error);
            BitmapDecodeNotifier.PostSourceFailure(image, error);
            BitmapDecodeNotifier.PostSourceFailure(image, error);

            Assert.Empty(failures);

            dispatcher.ProcessQueue();

            var raised = Assert.Single(failures);
            Assert.Same(error, raised);
            Assert.Same(Thread.CurrentThread, raisedOn);
            Assert.Same(error, image.LoadFailure);

            // Still latched: the next frame's identical failure must not re-raise.
            BitmapDecodeNotifier.PostSourceFailure(image, error);
            dispatcher.ProcessQueue();
            Assert.Single(failures);
        }
        finally
        {
            RestoreMainDispatcherSlot(previousMainDispatcher);
        }
    }

    /// <summary>
    /// An upload failure EPISODE must end when a retry succeeds, so a later one is reported too —
    /// and a queued announcement for an episode that already resolved must not resurrect it.
    /// </summary>
    /// <remarks>
    /// <para>The latch that deduplicates the per-frame retry is the same latch an element reads to
    /// decide whether the source is in trouble. Nothing used to release it on a successful upload,
    /// so the first transient GPU fault a source ever hit silenced the application channel for that
    /// source permanently: every later episode — a genuinely different fault, minutes apart — was
    /// deduplicated against a failure that had already healed. Releasing it on the successful
    /// branch of <c>GetNativeBitmap</c> is what makes "once per episode" mean episode.</para>
    /// <para>The last leg is the ordering hazard that release introduces. The failure is queued on
    /// the render thread and announced on the UI thread, so a retry can succeed in between. The
    /// drain must therefore ANNOUNCE only, never re-latch: re-latching from a stale queue entry
    /// would put the source back into a failed state the GPU has demonstrably recovered from, which
    /// is the permanent latch again by a longer route.</para>
    /// </remarks>
    [Fact]
    public void AnUploadFailureEpisodeEndsWhenTheRetrySucceedsAndTheNextOneIsReported()
    {
        BitmapImage.SetDecoder(new RecordingImageDecoder());

        using var file = new TempImageFile();
        var previousMainDispatcher = ReadMainDispatcherSlot();
        try
        {
            Dispatcher.SetAsMainThread();
            BitmapDecodeNotifier.FlushPendingForTests();
            var dispatcher = Dispatcher.MainDispatcher;
            Assert.NotNull(dispatcher);
            dispatcher!.ProcessQueue();

            using var image = file.CreateDeferredImage();
            var failures = new List<Exception>();
            image.LoadFailed += (_, ex) => failures.Add(ex);

            var first = new InvalidOperationException("vram exhausted");
            BitmapDecodeNotifier.PostSourceFailure(image, first);
            dispatcher.ProcessQueue();
            Assert.Same(first, Assert.Single(failures));

            // The next frame's upload succeeds. This is verbatim the call the successful branch of
            // GetNativeBitmap makes.
            image.ClearUploadFailure();
            Assert.Null(image.LoadFailure);

            var second = new InvalidOperationException("device removed");
            BitmapDecodeNotifier.PostSourceFailure(image, second);
            dispatcher.ProcessQueue();
            Assert.Equal(2, failures.Count);
            Assert.Same(second, failures[1]);

            // A retry that beats the UI thread to it: the episode is over before its announcement
            // is delivered, so nothing is announced and — the part that matters — nothing is
            // re-latched.
            image.ClearUploadFailure();
            var third = new InvalidOperationException("one bad frame");
            BitmapDecodeNotifier.PostSourceFailure(image, third);
            image.ClearUploadFailure();
            dispatcher.ProcessQueue();

            Assert.Equal(2, failures.Count);
            Assert.Null(image.LoadFailure);
        }
        finally
        {
            RestoreMainDispatcherSlot(previousMainDispatcher);
        }
    }

    /// <summary>
    /// A metadata (header-probe) job that keeps failing must reach a terminal state, exactly as a
    /// pixel decode does.
    /// </summary>
    /// <remarks>
    /// The metadata job is the pixel path's own defect on a second path: nothing about a probe that
    /// threw — or that returned no dimensions at all for a container the decoder cannot read a
    /// header from — used to be recorded, so the next caller re-enqueued, forever. It is worse than
    /// the pixel path was, because metadata jobs are drained AHEAD of every pixel job, so an
    /// unbounded stream of them starves pixel decoding process-wide, and because the encoded bytes
    /// were cached only on the success path, so every retry re-read the file.
    /// </remarks>
    [Fact]
    public void AFailingMetadataProbeStopsInsteadOfReReadingTheSourceForever()
    {
        var decoder = new RecordingImageDecoder { DimensionFault = static () => new DecoderFault() };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        // Layout passes, exactly as an intrinsic-size-driven caller issues them.
        var passes = 0;
        var driving = Stopwatch.StartNew();
        while (driving.Elapsed < TimeSpan.FromSeconds(2))
        {
            image.RequestMetadata();
            passes++;
            Thread.Yield();
        }

        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));

        Assert.True(passes > 100, $"only {passes} requests were issued — the drive loop proves nothing");
        Assert.True(decoder.DimensionCalls >= 1, "the metadata job never ran at all");
        Assert.True(
            decoder.DimensionCalls <= 3,
            $"{decoder.DimensionCalls} header probes for {passes} requests — the metadata path is unbounded");
        Assert.True(
            recorder.Count(ImageDiagnosticKind.Degraded) >= 1,
            "the metadata chain reached its terminal state silently");

        // Terminal really means terminal.
        var settled = decoder.DimensionCalls;
        image.RequestMetadata();
        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));
        Assert.Equal(settled, decoder.DimensionCalls);
    }

    /// <summary>
    /// An author's out-of-range crop rect must not cost the element that owns the image its
    /// raster or its repaint.
    /// </summary>
    /// <remarks>
    /// <para>Decode options are applied inside the decode now, against the NATURAL size. A rect
    /// the author got wrong is therefore discovered on the decode worker, and it is deterministic:
    /// failing the decode over it would spend the source's whole failure budget and leave the
    /// element permanently blank. The decoder reports it through the Release-live channel and
    /// publishes the untransformed raster instead — an uncropped picture is diagnosable, a blank
    /// one is not.</para>
    /// <para>This also pins the older half of the same invariant: <c>OnImageLoaded</c> used to be
    /// raised as a bare multicast delegate, which stops at the first exception, so any subscriber
    /// attached ahead of <c>Image.OnSourceAsyncLoaded</c> that threw meant a fully decoded, fully
    /// resident raster nothing ever called <c>InvalidateVisual</c> for.</para>
    /// </remarks>
    [Fact]
    public void AThrowingBuiltInLoadHandlerDoesNotStrandTheElementsOwnNotification()
    {
        BitmapImage.SetDecoder(new RecordingImageDecoder { NaturalWidth = 400, NaturalHeight = 300 });

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);

        // Constructed the way the XAML type converter does: BeginInit, properties, EndInit — which
        // is also the only supported window in which decode options are read.
        using var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = file.Uri;

        // Out of range against the natural size, so the decode's option transform throws.
        image.SourceRect = new Jalium.UI.Int32Rect(10, 10, 500, 500);
        image.EndInit();

        var elementNotifications = 0;
        image.OnImageLoaded += (_, _) => Interlocked.Increment(ref elementNotifications);

        _ = image.RequestDecodeAsync(64, 64, cover: false);

        Assert.True(
            ImagePipelineTestHarness.SpinUntil(
                () =>
                {
                    BitmapDecodeNotifier.FlushPendingForTests();
                    return Volatile.Read(ref elementNotifications) >= 1;
                },
                ImagePipelineTestHarness.DecodeTimeout),
            "the throwing built-in handler truncated the invocation list and stranded the element");

        // The raster really did publish: the element had something to draw all along.
        Assert.True(image.PixelWidth > 0 && image.PixelHeight > 0);

        // And the throw is not swallowed — it is reported through the Release-live channel.
        Assert.True(
            recorder.Count(ImageDiagnosticKind.DecodeFailed) >= 1,
            "the built-in handler's failure produced no diagnostic record");
    }

    /// <summary>
    /// A main dispatcher that is alive but BUSY must never have image notifications delivered for
    /// it on the notifier's watchdog thread.
    /// </summary>
    /// <remarks>
    /// <para>The watchdog exists for a dispatcher that can never run the queued drain — the slot is
    /// owned by an exited thread, or it is shutting down. It must not fire for one that has simply
    /// not got round to the operation yet, which is the normal state during any blocking click
    /// handler, in the whole window between <c>Application</c>'s constructor and
    /// <c>Application.Run</c>, and in every host that pumps on demand.</para>
    /// <para>Delivering there runs <c>OnImageLoaded</c> and through it
    /// <c>Image.OnSourceAsyncLoaded</c> — <c>InvalidateMeasure</c> plus <c>InvalidateVisual</c> —
    /// on a pooled timer thread, concurrently with whatever the UI thread is doing. Neither the
    /// measure/arrange validity flags, the layout manager's queues nor the host's dirty-element set
    /// is synchronized, so the price of an impatient watchdog is a corrupted layout tree or an
    /// <c>InvalidOperationException</c> thrown out of the UI thread's own enumeration — strictly
    /// worse than the late repaint it was trying to avoid.</para>
    /// </remarks>
    [Fact]
    public void ABusyMainDispatcherNeverGetsNotificationsDeliveredOnTheWatchdogThread()
    {
        BitmapImage.SetDecoder(new RecordingImageDecoder());

        using var file = new TempImageFile();
        var previousMainDispatcher = ReadMainDispatcherSlot();
        var deliveredOn = new Thread?[1];
        void OnBatchCompleted() => Volatile.Write(ref deliveredOn[0], Thread.CurrentThread);

        try
        {
            // Alive, not shutting down, and deliberately never pumped until the assertion below.
            Dispatcher.SetAsMainThread();
            BitmapDecodeNotifier.FlushPendingForTests();
            var dispatcher = Dispatcher.MainDispatcher;
            Assert.NotNull(dispatcher);
            Assert.Same(Thread.CurrentThread, dispatcher!.Thread);
            dispatcher.ProcessQueue();

            ImageSource.ContentChangedBatchCompleted += OnBatchCompleted;

            using var image = file.CreateDeferredImage();
            BitmapDecodeNotifier.Post(image, sourceVersion: 0, success: true, error: null);

            // Comfortably past the escalation the previous implementation performed at two
            // watchdog intervals. Sleeping rather than spinning keeps this thread — the UI thread —
            // exactly as unavailable as a blocking application handler would.
            Thread.Sleep(TimeSpan.FromMilliseconds(2600));

            Assert.Null(Volatile.Read(ref deliveredOn[0]));

            // And nothing is lost: the batch is still queued and is delivered by the dispatcher
            // itself, on its own thread, the moment it runs again.
            dispatcher.ProcessQueue();
            Assert.Same(Thread.CurrentThread, Volatile.Read(ref deliveredOn[0]));
        }
        finally
        {
            ImageSource.ContentChangedBatchCompleted -= OnBatchCompleted;
            RestoreMainDispatcherSlot(previousMainDispatcher);
        }
    }

    /// <summary>
    /// One throwing application handler must not cost every other image in the same coalesced
    /// batch its notification.
    /// </summary>
    /// <remarks>
    /// Coalescing a page of images into a single drain is exactly what turns one bad handler into
    /// a multi-image outage: the drain used to abort on the first exception, leaving every later
    /// item consumed-or-stranded with no diagnostic and no retry.
    /// </remarks>
    [Fact]
    public void AThrowingLoadHandlerDoesNotCostTheRestOfTheBatchItsNotification()
    {
        BitmapImage.SetDecoder(new RecordingImageDecoder { NaturalWidth = 32, NaturalHeight = 32 });

        using var file = new TempImageFile();
        var previousMainDispatcher = ReadMainDispatcherSlot();
        try
        {
            // A live but unpumped main dispatcher: the completions queue up so the drain below is
            // one batch containing both images.
            Dispatcher.SetAsMainThread();
            BitmapDecodeNotifier.FlushPendingForTests();

            using var poisoned = file.CreateDeferredImage();
            using var innocent = file.CreateDeferredImage();

            var innocentLoads = 0;
            poisoned.OnImageLoaded += static (_, _) => throw new InvalidOperationException("bad handler");
            innocent.OnImageLoaded += (_, _) => Interlocked.Increment(ref innocentLoads);

            var decodes = Task.WhenAll(
                poisoned.RequestDecodeAsync(32, 32, cover: false),
                innocent.RequestDecodeAsync(32, 32, cover: false));

            // Blocking is deliberate: an awaited continuation would resume on a pool thread and
            // could race the notifier's own delivery, which is the thing under test here.
#pragma warning disable xUnit1031
            decodes.WaitAsync(ImagePipelineTestHarness.DecodeTimeout).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

            BitmapDecodeNotifier.FlushPendingForTests();

            Assert.Equal(1, Volatile.Read(ref innocentLoads));
        }
        finally
        {
            RestoreMainDispatcherSlot(previousMainDispatcher);
        }
    }

    /// <summary>
    /// A source whose decoder always throws must reach a terminal state, not be re-decoded once
    /// per render frame for the life of the process.
    /// </summary>
    /// <remarks>
    /// A decode that throws publishes no snapshot, so nothing about the source looks saturated,
    /// and <c>Image.OnRender</c> calls <c>RequestBitmapDecode</c> unconditionally on every render
    /// pass. Two or three un-decodable images — a missing HEIF/AVIF codec on an N/LTSC SKU, a
    /// corrupt asset — would then hold the whole two-to-three-worker decode pool permanently and
    /// leave every other image in the process queued: the reported whole-page blackout, reached
    /// through the failure path.
    /// </remarks>
    [Fact]
    public async Task APermanentlyFailingDecodeStopsInsteadOfRedecodingOncePerRenderFrame()
    {
        var decoder = new RecordingImageDecoder { DecodeFault = static () => new DecoderFault() };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        await Assert.ThrowsAsync<DecoderFault>(() =>
            image.RequestDecodeAsync(64, 64, cover: false)
                .WaitAsync(ImagePipelineTestHarness.DecodeTimeout));

        // 120 render passes' worth of re-requests, exactly as Image.OnRender issues them, and all
        // of them inside the post-failure retry window. Not one may reach the decoder: this is the
        // per-frame flood the bound exists to stop.
        for (var frame = 0; frame < 120; frame++)
        {
            image.RequestDecode(64, 64, cover: false);
        }

        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
        Assert.Equal(1, decoder.DecodeCalls);

        // Now let the retry ladder run at full speed. The budget is small and CONSECUTIVE, so a
        // source that keeps failing still stops for good after a handful of attempts.
        image.SetFailureRetryDelayOverrideForTests(0d);
        ImagePipelineTestHarness.SpinUntil(
            () =>
            {
                image.RequestDecode(64, 64, cover: false);
                return recorder.Count(ImageDiagnosticKind.BucketSaturated) >= 1;
            },
            TimeSpan.FromSeconds(10));

        recorder.WaitUntilQuiet(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));

        Assert.True(
            decoder.DecodeCalls <= 6,
            $"{decoder.DecodeCalls} native decodes — the failure path is unbounded");
        Assert.True(
            recorder.Count(ImageDiagnosticKind.BucketSaturated) >= 1,
            "the terminal state was reached silently");

        // Terminal really means terminal: further requests cost nothing at all, and hand back a
        // completed task rather than parking the caller on a decode that will never run.
        var settled = decoder.DecodeCalls;
        var afterTerminal = image.RequestDecodeAsync(64, 64, cover: false);
        Assert.True(afterTerminal.IsCompletedSuccessfully);
        Assert.Equal(settled, decoder.DecodeCalls);
    }

    /// <summary>
    /// A decode that fails for a few frames and then succeeds must end up showing the image.
    /// </summary>
    /// <remarks>
    /// The failure bound is what makes an un-decodable source terminate, but a bound alone turns
    /// every TRANSIENT fault into a permanent one: <c>Image.MeasureOverride</c> and
    /// <c>Image.OnRender</c> both re-request on every pass, so a pure consecutive-attempt counter
    /// is spent inside about three frames — roughly 50ms. An on-access antivirus scan holding the
    /// asset, an SMB share hiccup or a momentarily exhausted handle table lasts longer than that,
    /// and would have left the element blank for the life of the process with the source's own
    /// bytes perfectly readable a moment later. The delay between attempts is what buys the
    /// difference.
    /// </remarks>
    [Fact]
    public async Task ATransientDecodeFailureRecoversInsteadOfBlankingTheImageForever()
    {
        var failuresLeft = 3;
        var decoder = new RecordingImageDecoder
        {
            NaturalWidth = 64,
            NaturalHeight = 64,
            DecodeFault = () =>
                Interlocked.Decrement(ref failuresLeft) >= 0 ? new DecoderFault() : (Exception?)null,
        };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        // Collapsed so the test does not spend the real ladder's seconds; the framework schedule is
        // an exponential backoff, and the property under test is that a retry happens at all.
        image.SetFailureRetryDelayOverrideForTests(0d);

        await Assert.ThrowsAsync<DecoderFault>(() =>
            image.RequestDecodeAsync(64, 64, cover: false)
                .WaitAsync(ImagePipelineTestHarness.DecodeTimeout));

        // Render passes, exactly as Image.OnRender issues them.
        Assert.True(
            ImagePipelineTestHarness.SpinUntil(
                () =>
                {
                    image.RequestDecode(64, 64, cover: false);
                    return image.PixelWidth > 0;
                },
                TimeSpan.FromSeconds(10)),
            $"the source never recovered after {decoder.DecodeCalls} attempts — a transient decode failure is permanent");

        Assert.Equal(64, image.PixelWidth);
        Assert.Equal(64, image.PixelHeight);
    }

    /// <summary>
    /// An unbounded-axis request must not be downgraded to a microscopic bucket by a later
    /// one-pixel value on the other axis.
    /// </summary>
    /// <remarks>
    /// A zero axis means "unconstrained", so the bucket policy scales by the other axis alone;
    /// with both axes present it scales by the smaller of the two. Accumulating the request
    /// axis-wise with <c>Math.Max</c> is therefore NOT monotone in the resolved bucket: a source
    /// measured at (1900, unbounded) — which is what <c>Image.MeasureOverride</c> emits inside a
    /// <c>StackPanel</c> or <c>ScrollViewer</c> — and then arranged one pixel tall for a frame, as
    /// a collapsed expander or an animating row is, accumulates to (1900, 1) and resolves to a
    /// handful of pixels. The wide axis is never re-satisfied, because an upgrade needs strictly
    /// greater area, so the image renders as a blur with no failure anywhere.
    /// </remarks>
    [Fact]
    public async Task AWidthOnlyRequestIsNotDowngradedByALaterOnePixelHeight()
    {
        var decoder = new RecordingImageDecoder { NaturalWidth = 2000, NaturalHeight = 1000 };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        await image.RequestDecodeAsync(1900, 0, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        // Read through the published BUCKET. PixelWidth reports the canonical 2000 whatever the
        // bucket does, so asserting the downgrade through it would pass against the very defect
        // this test exists to pin.
        Assert.Equal((2000, 1000), ImagePipelineTestHarness.PublishedBucket(image));

        // The collapsed frame. Nothing may shrink here, and nothing does today because a published
        // bucket only ever grows — but the accumulated request is now (1900, 1).
        image.RequestDecode(1900, 1, cover: false);

        // Reclaim and re-decode: the restore resolves the bucket from that accumulated request
        // with no published bucket to protect it. Resolving (1900, 1) alone yields a 4x2 raster.
        image.ReclaimIdleResources();
        await image.RequestDecodeAsync(1900, 1, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        Assert.Equal((2000, 1000), ImagePipelineTestHarness.PublishedBucket(image));
        Assert.Equal(2000, image.PixelWidth);
        Assert.Equal(1000, image.PixelHeight);
    }

    /// <summary>
    /// A burst of completions costs the host exactly one repaint signal, not one per image.
    /// </summary>
    [Fact]
    public void ABurstOfDecodeNotificationsProducesExactlyOneContentChangedSignal()
    {
        BitmapImage.SetDecoder(new RecordingImageDecoder());

        using var file = new TempImageFile();
        var previousMainDispatcher = ReadMainDispatcherSlot();
        var batches = 0;
        void OnBatchCompleted() => batches++;

        try
        {
            Dispatcher.SetAsMainThread();
            BitmapDecodeNotifier.FlushPendingForTests();
            var dispatcher = Dispatcher.MainDispatcher;
            Assert.NotNull(dispatcher);
            dispatcher.ProcessQueue();

            ImageSource.ContentChangedBatchCompleted += OnBatchCompleted;

            using var first = file.CreateDeferredImage();
            using var second = file.CreateDeferredImage();
            using var third = file.CreateDeferredImage();

            // A stale source version is deliberate: the drain must still coalesce, and this keeps
            // the test independent of any decode actually running.
            BitmapDecodeNotifier.Post(first, sourceVersion: 0, success: true, error: null);
            BitmapDecodeNotifier.Post(second, sourceVersion: 0, success: true, error: null);
            BitmapDecodeNotifier.Post(third, sourceVersion: 0, success: true, error: null);

            Assert.Equal(0, batches);

            dispatcher.ProcessQueue();
            Assert.Equal(1, batches);

            // Nothing is left over, so a second turn owes no repaint at all.
            dispatcher.ProcessQueue();
            Assert.Equal(1, batches);
        }
        finally
        {
            ImageSource.ContentChangedBatchCompleted -= OnBatchCompleted;
            RestoreMainDispatcherSlot(previousMainDispatcher);
        }
    }

    /// <summary>
    /// Pixels, width, height and stride are always mutually consistent, however a reader
    /// interleaves with a publishing decode worker.
    /// </summary>
    /// <remarks>
    /// They used to be four independent field writes under a lock, read with no lock at all from
    /// the render thread — which is on by default on Windows. A reader that interleaved could pair
    /// a 64x64 buffer with 512x512 dimensions and hand a 1 MiB dimension triple to a 16 KiB buffer.
    /// The only visible symptom of that upload is a black rectangle.
    /// </remarks>
    [Fact]
    public async Task SnapshotPublicationIsNeverTornUnderConcurrentReads()
    {
        var decoder = new RecordingImageDecoder
        {
            // Alternating geometry: a reader that mixes an old dimension with a new buffer has
            // something to get wrong.
            NaturalSizeForCall = static call => call % 2 == 0 ? (512, 512) : (64, 64),
        };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        var geometries = new HashSet<(int Width, int Height)>();
        var reads = 0L;
        var violations = 0;
        var stop = false;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                reads++;
                if (!image.TryGetPixelSnapshot(out var snapshot) || snapshot is null)
                {
                    continue;
                }

                if (snapshot.Width <= 0 ||
                    snapshot.Height <= 0 ||
                    snapshot.Stride < snapshot.Width * 4 ||
                    (long)snapshot.Stride * snapshot.Height > snapshot.Pixels.Length)
                {
                    Interlocked.Increment(ref violations);
                    continue;
                }

                lock (geometries)
                {
                    geometries.Add((snapshot.Width, snapshot.Height));
                }
            }
        });

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(250))
        {
            await image.RequestDecodeAsync(0, 0, cover: false)
                .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

            // Dropping the publication is the cheapest way to force the next publish; the reclaimer
            // does exactly this to off-screen images.
            image.ReclaimIdleResources();
        }

        Volatile.Write(ref stop, true);
        await reader.WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        Assert.Equal(0, Volatile.Read(ref violations));
        Assert.True(reads > 1_000, $"only {reads} reads observed — the race window was not exercised");
        Assert.True(decoder.DecodeCalls >= 4, $"only {decoder.DecodeCalls} publications occurred");
        lock (geometries)
        {
            Assert.True(
                geometries.Count >= 2,
                "the publisher never changed geometry, so a torn read could not have been detected");
        }
    }

    /// <summary>
    /// The geometry is validated where it is produced, not at every consumer. Publishing an
    /// inconsistent tuple is a framework bug and must throw rather than reach an upload.
    /// </summary>
    [Fact]
    public void PublishingAnInconsistentSnapshotThrowsInsteadOfHandingOutAShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => BitmapPixelSnapshot.Create(
            new byte[16], width: 4, height: 4, stride: 16,
            NativePixelFormat.Bgra8, canonicalWidth: 4, canonicalHeight: 4));

        Assert.Throws<ArgumentOutOfRangeException>(() => BitmapPixelSnapshot.Create(
            new byte[64], width: 4, height: 4, stride: 8,
            NativePixelFormat.Bgra8, canonicalWidth: 4, canonicalHeight: 4));

        var first = BitmapPixelSnapshot.Create(
            new byte[64], width: 4, height: 4, stride: 16,
            NativePixelFormat.Bgra8, canonicalWidth: 8, canonicalHeight: 8);
        var second = BitmapPixelSnapshot.Create(
            new byte[64], width: 4, height: 4, stride: 16,
            NativePixelFormat.Bgra8, canonicalWidth: 8, canonicalHeight: 8);

        Assert.Equal(8, first.CanonicalWidth);
        Assert.True(
            second.Generation > first.Generation,
            "generations must increase so a generation-keyed cache notices a replaced raster");
    }

    /// <summary>
    /// The decoder's pixel format is carried into the publication instead of being dropped on the
    /// floor, so an Android/Mali RGBA8 decode is not uploaded as red/blue-swapped BGRA8.
    /// </summary>
    [Fact]
    public async Task DecoderPixelFormatIsCarriedIntoThePublishedSnapshot()
    {
        var decoder = new RecordingImageDecoder
        {
            NaturalWidth = 8,
            NaturalHeight = 8,
            OutputFormat = NativePixelFormat.Rgba8,
            Fill = 0x00,
            FirstPixelBytes = [0xFF, 0x00, 0x00, 0xFF], // opaque red, RGBA channel order
        };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var image = file.CreateDeferredImage();

        await image.RequestDecodeAsync(0, 0, cover: false)
            .WaitAsync(ImagePipelineTestHarness.DecodeTimeout);

        Assert.True(image.TryGetPixelSnapshot(out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(NativePixelFormat.Rgba8, snapshot.Format);
        Assert.Equal(8, snapshot.Width);
        Assert.Equal(8, snapshot.CanonicalWidth);

        // Red must still be in the red channel of the declared format. A consumer that assumed
        // BGRA8 would read 0x00 here and 0xFF at index 2.
        Assert.Equal(0xFF, snapshot.Pixels[0]);
        Assert.Equal(0x00, snapshot.Pixels[2]);
    }

    /// <summary>
    /// A classified native decode failure keeps its classification all the way into the diagnostics
    /// record, so a support capture can name the cause rather than "an image failed".
    /// </summary>
    /// <remarks>
    /// <para>The record carries the exception INSTANCE, not a formatted string, which is what makes
    /// the channel useful for a failure class that did not exist when it was written: when the WIC
    /// layer starts distinguishing "this machine has no AVIF/HEIF codec installed" from "this file
    /// is corrupt", the distinction reaches a capture with no change here — the status and the
    /// message travel with the exception.</para>
    /// <para>That matters because the two failures are indistinguishable to the user and have
    /// opposite remedies: one is "install the Store Image Extension", the other is "the file is
    /// broken". Collapsing them into one generic code is what makes an image failure unactionable.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AClassifiedNativeDecodeFailureKeepsItsClassificationInTheRecord()
    {
        var fault = new NativeMediaException(
            NativeMediaStatus.UnsupportedFormat, "jalium_image_decode");
        var decoder = new RecordingImageDecoder { DecodeFault = () => fault };
        BitmapImage.SetDecoder(decoder);

        using var file = new TempImageFile();
        using var recorder = new ImageDiagnosticsRecorder(file.DiagnosticSource);
        using var image = file.CreateDeferredImage();

        await Assert.ThrowsAsync<NativeMediaException>(() =>
            image.RequestDecodeAsync(64, 64, cover: false)
                .WaitAsync(ImagePipelineTestHarness.DecodeTimeout));

        Assert.True(
            recorder.WaitForCount(
                ImageDiagnosticKind.DecodeFailed, 1, ImagePipelineTestHarness.DecodeTimeout),
            "a classified native failure produced no ImageDiagnostics record");

        var failure = recorder.Events.First(e => e.Kind == ImageDiagnosticKind.DecodeFailed);
        var reported = Assert.IsType<NativeMediaException>(failure.Error);
        Assert.Equal(NativeMediaStatus.UnsupportedFormat, reported.Status);
        Assert.Equal("jalium_image_decode", reported.Operation);

        // The failing operation and the status both reach the human-readable message, and the
        // record identifies WHICH image — a capture full of anonymous failures is not triage.
        Assert.Contains("jalium_image_decode", reported.Message, StringComparison.Ordinal);
        Assert.Contains("UnsupportedFormat", reported.Message, StringComparison.Ordinal);
        Assert.Equal(file.DiagnosticSource, failure.Source);
    }

    /// <summary>
    /// One throwing diagnostics subscriber must not strand the subscribers registered after it, and
    /// the fault must be counted once per FAILING HANDLER.
    /// </summary>
    /// <remarks>
    /// <para>A multicast delegate invoked as a single call stops at the first handler that throws.
    /// Attach order is whatever order the application and the framework happened to subscribe in,
    /// so a broken logger silently took every sink attached after it — on the one channel whose
    /// entire purpose is that image failures stop being silent. The counter mattered too: one
    /// increment per EVENT could not distinguish a single broken sink from a channel where every
    /// sink is dead, which is exactly the question an empty trace file poses.</para>
    /// <para>Two throwing handlers rather than one, and a survivor registered LAST, so the test
    /// fails against both the old shape (survivor never invoked) and against a per-event counter
    /// (one fault reported for two failures).</para>
    /// </remarks>
    [Fact]
    public void AThrowingDiagnosticsSubscriberNeverStrandsTheOnesBehindIt()
    {
        var source = $"<multicast-{Guid.NewGuid():N}>";
        var delivered = 0;
        var firstFaultCount = 0;

        void First(ImageDiagnosticEvent report)
        {
            if (!string.Equals(report.Source, source, StringComparison.Ordinal)) return;
            Interlocked.Increment(ref firstFaultCount);
            throw new InvalidOperationException("first sink is broken");
        }

        void Second(ImageDiagnosticEvent report)
        {
            if (!string.Equals(report.Source, source, StringComparison.Ordinal)) return;
            throw new InvalidOperationException("second sink is broken too");
        }

        void Survivor(ImageDiagnosticEvent report)
        {
            if (!string.Equals(report.Source, source, StringComparison.Ordinal)) return;
            Interlocked.Increment(ref delivered);
        }

        var before = ImageDiagnostics.Snapshot().SinkFaults;
        ImageDiagnostics.Reported += First;
        ImageDiagnostics.Reported += Second;
        ImageDiagnostics.Reported += Survivor;
        try
        {
            ImageDiagnostics.DecodeFailed(source, "multicast isolation", new InvalidOperationException());
        }
        finally
        {
            ImageDiagnostics.Reported -= First;
            ImageDiagnostics.Reported -= Second;
            ImageDiagnostics.Reported -= Survivor;
        }

        Assert.Equal(1, Volatile.Read(ref firstFaultCount));
        Assert.Equal(
            1,
            Volatile.Read(ref delivered));

        // Exactly two, not one: the counter has to name how many sinks are broken.
        Assert.Equal(before + 2, ImageDiagnostics.Snapshot().SinkFaults);
    }

    /// <summary>
    /// An absolute URI in a scheme the loader does not handle is reported rather than ignored.
    /// </summary>
    /// <remarks>
    /// <c>pack://</c>, <c>ms-appx://</c> and any custom scheme used to fall out of
    /// <c>LoadFromUri</c> in complete silence — no deferred source configured, no failure latched,
    /// nothing enqueued — so the element drew nothing, reported nothing, and produced no trace
    /// record for the life of the process. Indistinguishable from a working image whose content is
    /// transparent, and one of the three cases the triage guide had to warn produced no evidence at
    /// all.
    /// </remarks>
    [Theory]
    [InlineData("pack://application:,,,/Assets/logo.png")]
    [InlineData("ms-appx:///Assets/logo.png")]
    public void AnUnsupportedUriSchemeIsReportedRatherThanIgnored(string uri)
    {
        BitmapImage.SetDecoder(new RecordingImageDecoder());

        var absolute = new Uri(uri);
        using var recorder = new ImageDiagnosticsRecorder(absolute.ToString());
        using var image = new BitmapImage(absolute);

        var failure = Assert.IsType<NotSupportedException>(image.LoadFailure);
        Assert.Contains(absolute.Scheme, failure.Message, StringComparison.Ordinal);

        // And on the Release-live channel, which is what a support capture reads.
        var record = Assert.Single(
            recorder.Events, e => e.Kind == ImageDiagnosticKind.DecodeFailed);
        Assert.Same(failure, record.Error);
    }

    /// <summary>
    /// The single most common real-world image bug — an asset that was not copied to the output
    /// directory — reaches the diagnostics channel, not just <c>Image.ImageFailed</c>.
    /// </summary>
    /// <remarks>
    /// <para>Before the bridge in <c>ImageSource.ReportLoadFailure</c> this failure was announced
    /// only through <c>LoadFailed</c> -&gt; <c>Image.OnSourceLoadFailed</c> -&gt;
    /// <c>ImageFailed</c>, which is an <c>Image</c> ELEMENT's routed event. An application painting
    /// the same missing asset through an <see cref="ImageBrush"/>, a <c>DrawingImage</c> or a
    /// <c>Shape.Fill</c> has no <c>Image</c> element anywhere, so it got a blank rectangle with no
    /// counter, no EventSource record and nothing in a trace file.</para>
    /// <para>Exactly ONE record, which is the other half of the contract: the sites that already
    /// write a richer record — the decode worker, the SVG loader — must not now write a second one,
    /// or <c>DecodesFailed</c> double-counts and one failure reads as two.</para>
    /// </remarks>
    [Fact]
    public void AMissingRelativeAssetIsVisibleToABrushConsumerNotOnlyToAnImageElement()
    {
        BitmapImage.SetDecoder(new RecordingImageDecoder());

        var missing = $"Assets/missing-{Guid.NewGuid():N}.png";
        var relative = new Uri(missing, UriKind.Relative);
        using var recorder = new ImageDiagnosticsRecorder(relative.ToString());

        var before = ImageDiagnostics.Snapshot().DecodesFailed;
        using var image = new BitmapImage(relative);

        Assert.IsType<FileNotFoundException>(image.LoadFailure);
        Assert.True(ImageDiagnostics.Snapshot().DecodesFailed > before);

        var record = Assert.Single(
            recorder.Events, e => e.Kind == ImageDiagnosticKind.DecodeFailed);
        Assert.Same(image.LoadFailure, record.Error);
        Assert.False(string.IsNullOrEmpty(record.Detail));
    }

    /// <summary>
    /// A clone carries the ORIGINAL's channel order, not an assumed BGRA8.
    /// </summary>
    /// <remarks>
    /// <c>CopyBitmapStateFrom</c> read the raster's buffer, geometry and stride as separate fields
    /// and then hardcoded <see cref="NativePixelFormat.Bgra8"/> when republishing them, so
    /// <c>Clone()</c> / <c>GetAsFrozen()</c> of an RGBA8 source — a custom
    /// <c>INativeImageDecoder</c>, <c>FromDecodedImage(Rgba8)</c>, a media frame — relabelled the
    /// pixels rather than converting them. Red and blue come out swapped, and no later stage can
    /// detect it: the bytes are valid, only the label is wrong. Reading the publication as one
    /// tuple is what makes the label travel with the bytes it describes.
    /// </remarks>
    [Fact]
    public void CloningABitmapCarriesItsPixelFormatRatherThanAssumingBgra8()
    {
        // Pure red in RGBA8 byte order: 0xFF at byte 0, which in BGRA8 would read as pure blue.
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
            pixels[i + 3] = 0xFF;
        }

        using var original = BitmapImage.FromDecodedImage(
            new DecodedImage(pixels, 4, 4, 16, NativePixelFormat.Rgba8));

        Assert.True(original.TryGetPixelSnapshot(out var originalSnapshot));
        Assert.Equal(NativePixelFormat.Rgba8, originalSnapshot!.Format);

        var clone = (BitmapImage)original.Clone();

        Assert.True(clone.TryGetPixelSnapshot(out var cloneSnapshot));
        Assert.NotNull(cloneSnapshot);
        Assert.Equal(NativePixelFormat.Rgba8, cloneSnapshot!.Format);
        Assert.Equal(originalSnapshot.Width, cloneSnapshot.Width);
        Assert.Equal(originalSnapshot.Height, cloneSnapshot.Height);
        Assert.Equal(originalSnapshot.Stride, cloneSnapshot.Stride);
        Assert.Equal(originalSnapshot.CanonicalWidth, cloneSnapshot.CanonicalWidth);
        Assert.Equal(0xFF, cloneSnapshot.Pixels[0]);
    }

    private static object? ReadMainDispatcherSlot() => MainDispatcherField.GetValue(null);

    private static void RestoreMainDispatcherSlot(object? value) =>
        MainDispatcherField.SetValue(null, value);

    /// <summary>
    /// The process main-dispatcher slot. Tests that need a pumpable main dispatcher must claim it
    /// and put back whatever was there, because the first <c>GetForCurrentThread</c> anywhere in
    /// the process claims it permanently.
    /// </summary>
    private static readonly FieldInfo MainDispatcherField =
        (typeof(Dispatcher).Assembly.GetType("Jalium.UI.Threading.DispatcherCore")
            ?? throw new InvalidOperationException("DispatcherCore type not found."))
        .GetField("_mainDispatcher", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DispatcherCore._mainDispatcher field not found.");
}
