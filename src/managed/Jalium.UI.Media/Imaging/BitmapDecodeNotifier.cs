using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Jalium.UI.Diagnostics;
using Jalium.UI.Threading;

namespace Jalium.UI.Media.Imaging;

/// <summary>
/// Delivers deferred-decode completions and image-pipeline failures to the UI thread, coalesced to
/// one drain per dispatcher turn.
/// </summary>
/// <remarks>
/// <para><b>The wrong dispatcher.</b> The original code called <c>Dispatcher.CheckAccess()</c> on
/// the dispatcher of the thread that CONSTRUCTED the <see cref="BitmapImage"/>. Because
/// <see cref="BitmapDecodeScheduler"/> runs jobs through
/// <see cref="ThreadPool.UnsafeQueueUserWorkItem{TState}(Action{TState}, TState, bool)"/>, a
/// bitmap constructed on a pooled thread can have its decode run on that same pooled thread —
/// <c>CheckAccess()</c> returns true and <c>OnImageLoaded</c>, and through it
/// <c>InvalidateMeasure</c>/<c>InvalidateVisual</c>, runs on a worker. Resolution here is
/// <see cref="Dispatcher.MainDispatcher"/> only.</para>
/// <para><b>The wrong priority.</b> Completions used to post at <c>Background</c> (4), which is
/// below <c>Render</c> (7) and is therefore starvable by a busy render loop — exactly the
/// condition an image-heavy page creates. <c>Normal</c> (9) is above the render chain without
/// being <c>Send</c> (10), which would hoist arbitrary application handlers above it.</para>
/// <para><b>One post per decode.</b> A burst of completions used to schedule a dispatcher
/// operation each, so a page of images produced a storm of individual layout invalidations. Posts
/// are queued and exactly one drain is scheduled per batch.</para>
/// <para><b>The coalescing flag must never latch.</b> The first version of this class cleared its
/// "a drain is scheduled" flag only inside <see cref="Drain"/> and treated
/// <c>MainDispatcher is null</c> as "this host has no pumping queue". Both are wrong.
/// <c>DispatcherCore.GetForCurrentThread</c> does <c>_mainDispatcher ??= dispatcher</c>, so the
/// FIRST thread in the process to touch any dispatcher owns the main-dispatcher slot until
/// <c>Application</c>'s constructor overwrites it — a short-lived worker that merely constructed a
/// <see cref="BitmapImage"/> can be that thread, and it may already have exited. Posting to it
/// queues an operation that never runs; with the flag cleared only by the drain, every later post
/// short-circuited and the notifier was dead for the whole process: every image published pixels,
/// no <c>Image</c> was ever invalidated, and the page rendered blank with no diagnostic at all.
/// This version therefore (a) refuses a dispatcher whose thread has exited or which is shutting
/// down, (b) re-targets whenever the main-dispatcher slot changes under it, (c) checks the posted
/// operation's status because <c>EnqueueOperation</c> ABORTS silently on a shut-down dispatcher
/// rather than throwing, and (d) watches the queued operation so an aborted one is re-posted.
/// Every one of those paths reports through <see cref="ImageDiagnostics.Degraded"/>, which is live
/// in Release.</para>
/// <para><b>What the watchdog may NOT do.</b> Delivery on any thread other than the main
/// dispatcher's is only ever correct when NO thread can ever run the queued callback — no main
/// dispatcher exists, its thread has exited, or it is shutting down. A main dispatcher that is
/// alive and merely BUSY (a blocking click handler, the window between <c>Application</c>'s
/// constructor and <c>Application.Run</c>, a headless host that pumps on demand) will run the
/// operation eventually, and delivering it on the timer thread instead runs
/// <c>Image.OnSourceAsyncLoaded</c> — i.e. <c>InvalidateMeasure</c> plus
/// <c>InvalidateVisual</c> — concurrently with a layout pass on the UI thread. None of the
/// structures those touch (<c>_isMeasureValid</c>, the layout manager's queues, the host's
/// dirty-element set) are synchronized, so that trades a blank image for a corrupted layout tree
/// or an <see cref="InvalidOperationException"/> thrown out of the UI thread's own enumeration.
/// An impatient watchdog must therefore report and wait, never deliver.</para>
/// </remarks>
internal static class BitmapDecodeNotifier
{
    /// <summary>Overrides <see cref="DefaultWatchdogMs"/>. Support/diagnostic escape hatch.</summary>
    private const string WatchdogEnvironmentVariable = "JALIUM_IMAGE_NOTIFY_WATCHDOG_MS";

    /// <summary>
    /// How long a queued drain may sit unexecuted before the notifier starts reporting that the UI
    /// thread is not running it.
    /// </summary>
    /// <remarks>
    /// A healthy UI thread runs a <c>Normal</c>-priority operation within a frame or two, so one
    /// second is several orders of magnitude of headroom. Nothing destructive happens at the
    /// expiry — the batch is only ever delivered by the dispatcher itself — so this is a
    /// diagnostics threshold, not a correctness one.
    /// </remarks>
    private const int DefaultWatchdogMs = 1000;

    private const int MinimumWatchdogMs = 50;

    /// <summary>
    /// How much the watchdog interval is stretched once a stall has already been reported.
    /// </summary>
    /// <remarks>
    /// The stalled state can last as long as the UI thread is blocked. Polling it at the full rate
    /// buys nothing — the only thing left to detect is the target dying, which turns the stall into
    /// the deliverable "nothing can ever run this" case — so back off rather than waking a timer
    /// thread every second for the duration of a modal operation.
    /// </remarks>
    private const int StalledPollMultiplier = 8;

    /// <summary>Diagnostics identity for events about the channel rather than about one image.</summary>
    private const string DiagnosticName = "<image decode notifier>";

    /// <summary>What one queued item asks the drain to do.</summary>
    private enum PendingKind
    {
        /// <summary>A deferred decode published pixels.</summary>
        DecodeSucceeded,

        /// <summary>A deferred decode failed.</summary>
        DecodeFailed,

        /// <summary>
        /// A failure discovered outside the decoder — today a GPU upload failure found on the
        /// render thread — that must reach the source's <c>LoadFailed</c> chain.
        /// </summary>
        SourceLoadFailed,

        /// <summary>
        /// A metadata probe found a multi-frame payload and built the internal animated
        /// substitute. The dispatcher-affine half of starting it — creating the frame timer —
        /// must run on the UI thread.
        /// </summary>
        AnimatedSubstituteReady,

        /// <summary>
        /// A metadata probe resolved the source's natural size. Delivered on the UI thread because
        /// the element that was waiting for an intrinsic size answers it with
        /// <c>InvalidateMeasure</c>.
        /// </summary>
        MetadataResolved,
    }

    private readonly record struct PendingNotification(
        ImageSource Target,
        long SourceVersion,
        PendingKind Kind,
        Exception? Error);

    /// <summary>What <see cref="OnWatchdogTick"/> decided to do, resolved under the gate.</summary>
    private enum WatchdogAction
    {
        /// <summary>The scheduled drain is still viable; leave it alone.</summary>
        None,

        /// <summary>The queued operation was aborted; post it again to the same dispatcher.</summary>
        Repost,

        /// <summary>The main-dispatcher slot changed; post to the new owner.</summary>
        Retarget,

        /// <summary>
        /// The operation is still queued on a dispatcher that has simply not run it yet. Report and
        /// keep waiting — see the class remarks for why this may never become inline delivery.
        /// </summary>
        ReportStall,

        /// <summary>Nothing will ever run the drain; deliver on the calling thread.</summary>
        DeliverHere,
    }

    private static readonly ConcurrentQueue<PendingNotification> s_pending = new();
    private static readonly object s_scheduleGate = new();
    private static readonly int s_watchdogMs = ResolveWatchdogMs();

    /// <summary>
    /// Polling period once a stall has been reported. Computed in <see cref="long"/> and capped so
    /// a support engineer who sets an enormous
    /// <c>JALIUM_IMAGE_NOTIFY_WATCHDOG_MS</c> cannot overflow the multiplication into a negative
    /// period, which <see cref="Timer.Change(int, int)"/> rejects.
    /// </summary>
    private static readonly int s_stalledPollMs =
        (int)Math.Min((long)s_watchdogMs * StalledPollMultiplier, 60_000L);

    // Every field below is guarded by s_scheduleGate.
    private static bool s_drainScheduled;
    private static Dispatcher? s_scheduledOn;
    private static DispatcherOperation? s_scheduledOperation;
    private static long s_scheduledAtTimestamp;
    private static bool s_stallReported;
    private static Timer? s_watchdog;
    private static string? s_lastInlineReason;

    /// <summary>
    /// Set while a thread is inside <see cref="DrainCore"/>. Interlocked rather than a lock: the
    /// drain runs arbitrary application handlers, and holding a monitor across those invites a
    /// deadlock against any handler that waits on another thread.
    /// </summary>
    private static int s_drainActive;

    /// <summary>
    /// Queues one decode completion. Safe to call from any thread, including a decode worker.
    /// </summary>
    internal static void Post(BitmapImage source, long sourceVersion, bool success, Exception? error)
    {
        ArgumentNullException.ThrowIfNull(source);

        s_pending.Enqueue(new PendingNotification(
            source,
            sourceVersion,
            success ? PendingKind.DecodeSucceeded : PendingKind.DecodeFailed,
            error));
        EnsureDrainScheduled();
    }

    /// <summary>
    /// Queues the UI-thread half of publishing a multi-frame source's animated substitute.
    /// </summary>
    /// <remarks>
    /// The substitute's frames are decoded on the metadata worker, but its frame timer is a
    /// <c>DispatcherTimer</c> and must be created on a thread that actually pumps — see
    /// <c>BitmapImage.PublishAnimatedSubstitute</c> for what creating one on a pooled worker costs.
    /// It rides this channel rather than a bare <c>InvokeAsync</c> so it is coalesced into the same
    /// batch as the decode completions and reaches the same dispatcher, whatever the notifier's
    /// re-targeting and watchdog logic has decided that is.
    /// </remarks>
    internal static void PostAnimatedSubstitute(BitmapImage source, long sourceVersion)
    {
        ArgumentNullException.ThrowIfNull(source);

        s_pending.Enqueue(new PendingNotification(
            source, sourceVersion, PendingKind.AnimatedSubstituteReady, null));
        EnsureDrainScheduled();
    }

    /// <summary>
    /// Queues the announcement that a header probe resolved a source's natural size.
    /// </summary>
    /// <remarks>
    /// Rides this channel rather than a bare <c>InvokeAsync</c> for the same reasons a decode
    /// completion does: it must reach whichever dispatcher this class has decided can actually run
    /// a callback, it must be coalesced into one drain per batch so a page of images owes one
    /// layout pass rather than one per image, and it must not run <c>InvalidateMeasure</c> on the
    /// decode worker that produced it.
    /// </remarks>
    internal static void PostMetadata(BitmapImage source, long sourceVersion)
    {
        ArgumentNullException.ThrowIfNull(source);

        s_pending.Enqueue(new PendingNotification(
            source, sourceVersion, PendingKind.MetadataResolved, null));
        EnsureDrainScheduled();
    }

    /// <summary>
    /// Queues a GPU upload failure discovered by the render backend so it reaches the source's
    /// <c>LoadFailed</c> chain — and through it <c>Image.ImageFailed</c> — on the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>The caller is the render backend, which on Windows runs on a dedicated render thread
    /// by default. Raising <c>LoadFailed</c> there would run application handlers, a routed-event
    /// raise and a dependency-property write off the UI thread, which is precisely the hazard this
    /// class exists to prevent — so the report rides the same coalesced UI-thread channel as a
    /// decode completion.</para>
    /// <para>Upload class specifically, not "some failure": the classification travels with the
    /// latch and decides whether an element may stop painting the source. An upload failure must
    /// NOT stop it — the retry on the next frame is the only thing that ever clears one — so
    /// anything routed through here is telling every reader "keep drawing, keep retrying". A
    /// failure that genuinely means "there are no pixels" belongs on
    /// <c>ImageSource.ReportLoadFailure</c> instead.</para>
    /// <para>Deduplicated by the latch itself, which is claimed here on the discovering thread
    /// rather than when the queued item is delivered. A GPU upload is retried on every frame, so
    /// dozens of identical reports can pile up before the UI thread next runs; claiming at post
    /// time collapses them into one queue entry instead of one per frame. A successful retry
    /// releases the latch (<c>RenderTargetDrawingContext.GetNativeBitmap</c>), so a genuinely new
    /// failure episode is reported again.</para>
    /// </remarks>
    internal static void PostSourceFailure(ImageSource source, Exception error)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(error);

        if (!source.TryLatchUploadFailure(error))
        {
            return;
        }

        s_pending.Enqueue(new PendingNotification(
            source, 0L, PendingKind.SourceLoadFailed, error));
        EnsureDrainScheduled();
    }

    /// <summary>
    /// Delivers everything queued right now on the calling thread and releases the coalescing
    /// state. Test-only; never call this from framework code.
    /// </summary>
    /// <remarks>
    /// A unit-test host has a main dispatcher — the first <c>Dispatcher.GetForCurrentThread()</c>
    /// anywhere in the process claims the slot — but usually nothing pumps it. A test that wants
    /// to observe delivery synchronously calls this instead of pumping, because the watchdog
    /// deliberately never delivers on anything but the dispatcher itself.
    /// </remarks>
    internal static void FlushPendingForTests() => Drain();

    /// <summary>
    /// Makes sure exactly one drain is scheduled against the dispatcher that owns the process
    /// right now, or delivers inline when no dispatcher can ever run it.
    /// </summary>
    private static void EnsureDrainScheduled()
    {
        var target = ResolveDeliveryTarget();
        lock (s_scheduleGate)
        {
            if (target is null)
            {
                // Nothing in this process will ever run a queued callback: there is no main
                // dispatcher, its thread has exited, or it is shutting down. Draining on the
                // calling thread is the only way the completion is observed at all, and there is
                // no UI thread left to race. Note this is a MEASURED condition, not the old
                // "MainDispatcher happens to be null" guess — merely constructing a BitmapImage on
                // any thread makes that slot non-null.
                ReleaseScheduleLocked();
            }
            else
            {
                if (s_drainScheduled && ReferenceEquals(s_scheduledOn, target))
                {
                    // A drain is already queued on the right dispatcher and has not started
                    // dequeuing yet, or is running and will be re-scheduled by whichever post
                    // loses the next race. Either way this item is in the queue already.
                    return;
                }

                s_drainScheduled = true;
                s_scheduledOn = target;
                s_scheduledOperation = null;
                s_scheduledAtTimestamp = Stopwatch.GetTimestamp();
                s_stallReported = false;
                ArmWatchdogLocked(s_watchdogMs);
            }
        }

        if (target is null)
        {
            DeliverHere("no pumping main dispatcher exists", 0d);
            return;
        }

        PostDrainTo(target);
    }

    /// <summary>
    /// Queues the drain and verifies it was actually accepted.
    /// </summary>
    /// <remarks>
    /// <c>DispatcherCore.EnqueueOperation</c> aborts the operation and returns normally when the
    /// dispatcher has begun shutting down — it does not throw — so a status check is the only way
    /// to notice that the completion was dropped between resolving the target and posting to it.
    /// The accepted operation is retained so the watchdog can tell "aborted, nobody will run this"
    /// apart from "still queued behind a busy UI thread"; only the first is actionable.
    /// </remarks>
    private static void PostDrainTo(Dispatcher target)
    {
        try
        {
            var operation = target.InvokeAsync(Drain, DispatcherPriority.Normal);
            if (operation.Status != DispatcherOperationStatus.Aborted)
            {
                // The dispatcher is accepting work again, so a later relapse into inline delivery
                // is news and gets reported rather than deduplicated away.
                lock (s_scheduleGate)
                {
                    s_lastInlineReason = null;
                    if (s_drainScheduled && ReferenceEquals(s_scheduledOn, target))
                    {
                        s_scheduledOperation = operation;
                    }
                }

                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The dispatcher shut down between the check above and the post.
        }

        lock (s_scheduleGate)
        {
            ReleaseScheduleLocked();
        }

        DeliverHere("the main dispatcher rejected the completion drain", 0d);
    }

    /// <summary>Reports the degradation and delivers the batch on the calling thread.</summary>
    /// <remarks>
    /// Reachable only when <see cref="ResolveDeliveryTarget"/> found no dispatcher that could ever
    /// run the drain, so there is no UI thread whose layout pass this could race.
    /// Deduplicated by reason: a headless or unit-test host delivers inline for every single batch
    /// it will ever produce, and one Degraded record per image would drown the very capture this
    /// channel exists to make readable; a change of reason, or a relapse after the dispatcher
    /// started accepting work again, is what is actually worth a line.
    /// </remarks>
    private static void DeliverHere(string reason, double elapsedMs)
    {
        bool report;
        lock (s_scheduleGate)
        {
            report = !string.Equals(s_lastInlineReason, reason, StringComparison.Ordinal);
            s_lastInlineReason = reason;
        }

        if (report)
        {
            ImageDiagnostics.Degraded(
                DiagnosticName,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"image notifications delivered off the UI thread: {reason} (waited {elapsedMs:F0}ms)"),
                0,
                0);
        }

        Drain();
    }

    private static void Drain()
    {
        lock (s_scheduleGate)
        {
            // Released BEFORE dequeuing, so a post that races this drain schedules another one
            // rather than being silently dropped by a drain that has already passed its own queue
            // check.
            ReleaseScheduleLocked();
        }

        // At most one thread may be running notification handlers at a time. Without this, the
        // headless path (every decode worker delivers inline) could raise OnImageLoaded for two
        // images concurrently, and application handlers are not written for that.
        while (!s_pending.IsEmpty)
        {
            if (Interlocked.CompareExchange(ref s_drainActive, 1, 0) != 0)
            {
                // Another thread owns delivery. It re-checks the queue after releasing, so the
                // items enqueued by this caller are delivered by that drain rather than dropped.
                return;
            }

            try
            {
                DrainCore();
            }
            finally
            {
                Volatile.Write(ref s_drainActive, 0);
            }
        }
    }

    private static void DrainCore()
    {
        var delivered = false;
        try
        {
            while (s_pending.TryDequeue(out var pending))
            {
                delivered = true;
                try
                {
                    Deliver(pending);
                }
                catch (Exception ex)
                {
                    // Per item, deliberately. Coalescing a page of images into one drain is what
                    // would otherwise let ONE throwing application handler consume or strand every
                    // other notification in the same batch — unrelated images that then never get
                    // InvalidateMeasure/InvalidateVisual and stay blank. Reported through the
                    // Release-live channel; never swallowed.
                    ImageDiagnostics.DecodeFailed(
                        DescribeTarget(pending.Target), "decode notification handler", ex);
                }
            }
        }
        finally
        {
            if (delivered)
            {
                // One batch-completed signal per drain, so a host owes one repaint for a page of
                // images instead of one per image. In a finally because a throwing application
                // handler must not cost the images that were already delivered their repaint.
                try
                {
                    Media.ImageSource.RaiseContentChangedBatchCompleted();
                }
                catch (Exception ex)
                {
                    ImageDiagnostics.DecodeFailed(
                        DiagnosticName, "image content batch handler", ex);
                }
            }
        }
    }

    private static void Deliver(in PendingNotification pending)
    {
        switch (pending.Kind)
        {
            case PendingKind.DecodeSucceeded:
            case PendingKind.DecodeFailed:
                ((BitmapImage)pending.Target).RaiseDecodeNotification(
                    pending.SourceVersion,
                    pending.Kind == PendingKind.DecodeSucceeded,
                    pending.Error);
                break;

            case PendingKind.AnimatedSubstituteReady:
                ((BitmapImage)pending.Target).RaiseAnimatedSubstituteReady(pending.SourceVersion);
                break;

            case PendingKind.MetadataResolved:
                ((BitmapImage)pending.Target).RaiseMetadataResolved(pending.SourceVersion);
                break;

            case PendingKind.SourceLoadFailed:
                // ANNOUNCE only. The failure was latched on the thread that discovered it, so this
                // must not re-latch: a retry that succeeded between the post and this drain has
                // already released the latch, and re-latching from a stale queue entry would put
                // the source back into a failed state the GPU has demonstrably recovered from —
                // one queued frame turning into a permanent one. RaiseLatchedLoadFailure raises
                // only while this error is still the live one, so a recovered episode goes quiet
                // instead of handing the application a fallback for a frame that painted.
                if (pending.Error is { } error)
                {
                    pending.Target.RaiseLatchedLoadFailure(error);
                }

                break;
        }
    }

    private static string DescribeTarget(ImageSource target) =>
        target is BitmapImage bitmap ? bitmap.DiagnosticSourceName : target.GetType().Name;

    private static void OnWatchdogTick()
    {
        WatchdogAction action;
        Dispatcher? target;
        double elapsedMs;

        lock (s_scheduleGate)
        {
            if (!s_drainScheduled)
            {
                DisarmWatchdogLocked();
                return;
            }

            elapsedMs = Stopwatch.GetElapsedTime(s_scheduledAtTimestamp).TotalMilliseconds;
            if (elapsedMs < s_watchdogMs)
                return;

            target = ResolveDeliveryTarget();
            if (target is null)
            {
                action = WatchdogAction.DeliverHere;
            }
            else if (!ReferenceEquals(target, s_scheduledOn))
            {
                // Application's constructor (or a restartable host) replaced the main-dispatcher
                // slot after the decode already posted to whatever thread claimed it first. The
                // operation queued on the old owner will never run; re-post to the new one.
                action = WatchdogAction.Retarget;
            }
            else if (s_scheduledOperation is null ||
                     s_scheduledOperation.Status == DispatcherOperationStatus.Aborted)
            {
                // Aborted is the only state that genuinely loses the batch: the dispatcher took
                // the operation and then dropped it. Re-posting is safe and idempotent, because a
                // drain with an empty queue does nothing at all.
                action = WatchdogAction.Repost;
            }
            else if (s_scheduledOperation.Status == DispatcherOperationStatus.Pending)
            {
                // Still queued on a live dispatcher that is simply busy. It WILL run. Delivering
                // here instead would run InvalidateMeasure/InvalidateVisual on this timer thread,
                // concurrently with whatever the UI thread is doing — see the class remarks.
                action = s_stallReported ? WatchdogAction.None : WatchdogAction.ReportStall;
                s_stallReported = true;
            }
            else
            {
                // Executing or Completed: the drain is running, or ran and a later post re-armed
                // the schedule. Nothing to do.
                action = WatchdogAction.None;
            }

            if (action == WatchdogAction.DeliverHere)
            {
                ReleaseScheduleLocked();
            }
            else if (action is WatchdogAction.Repost or WatchdogAction.Retarget)
            {
                s_scheduledOn = target;
                s_scheduledOperation = null;
                s_scheduledAtTimestamp = Stopwatch.GetTimestamp();
                s_stallReported = false;
                ArmWatchdogLocked(s_watchdogMs);
            }
            else
            {
                // Stalled: keep the schedule exactly as it is and stop polling at the full rate.
                ArmWatchdogLocked(s_stalledPollMs);
            }
        }

        switch (action)
        {
            case WatchdogAction.Repost:
            case WatchdogAction.Retarget:
                ImageDiagnostics.Degraded(
                    DiagnosticName,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the queued image completion drain was dropped after {elapsedMs:F0}ms; re-posting"),
                    0,
                    0);
                PostDrainTo(target!);
                break;

            case WatchdogAction.ReportStall:
                // Not a delivery decision — a visibility one. The batch stays queued and is
                // delivered by the dispatcher itself whenever the UI thread runs again.
                ImageDiagnostics.Degraded(
                    DiagnosticName,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the main dispatcher has not run the queued image completion drain in {elapsedMs:F0}ms; images stay unrefreshed until it does"),
                    0,
                    0);
                break;

            case WatchdogAction.DeliverHere:
                DeliverHere("the main dispatcher became unusable", elapsedMs);
                break;
        }
    }

    /// <summary>
    /// The dispatcher that can actually run a queued callback, or <see langword="null"/> when
    /// none can.
    /// </summary>
    private static Dispatcher? ResolveDeliveryTarget()
    {
        var dispatcher = Dispatcher.MainDispatcher;
        if (dispatcher is null)
            return null;

        // A dispatcher whose thread has already exited can never run the callback, and it is the
        // easiest way in the world to end up owning the main-dispatcher slot: the first thread to
        // touch any dispatcher claims it, and a decode worker that constructs a BitmapImage does
        // exactly that.
        if (!dispatcher.Thread.IsAlive)
            return null;

        // Shutdown started is the right test, not shutdown finished: EnqueueOperation aborts as
        // soon as _isShutdown is set, whether or not the queue has drained.
        if (dispatcher.HasShutdownStarted)
            return null;

        return dispatcher;
    }

    private static void ReleaseScheduleLocked()
    {
        Debug.Assert(Monitor.IsEntered(s_scheduleGate));

        s_drainScheduled = false;
        s_scheduledOn = null;
        s_scheduledOperation = null;
        s_stallReported = false;
        DisarmWatchdogLocked();
    }

    private static void ArmWatchdogLocked(int periodMs)
    {
        Debug.Assert(Monitor.IsEntered(s_scheduleGate));

        s_watchdog ??= new Timer(
            static _ => OnWatchdogTick(),
            state: null,
            dueTime: Timeout.Infinite,
            period: Timeout.Infinite);
        s_watchdog.Change(periodMs, periodMs);
    }

    private static void DisarmWatchdogLocked()
    {
        Debug.Assert(Monitor.IsEntered(s_scheduleGate));

        s_watchdog?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private static int ResolveWatchdogMs()
    {
        var configured = Environment.GetEnvironmentVariable(WatchdogEnvironmentVariable);
        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= MinimumWatchdogMs)
        {
            return parsed;
        }

        return DefaultWatchdogMs;
    }
}
