using System.Collections.Concurrent;
using System.Diagnostics;
using Jalium.UI.Diagnostics;

namespace Jalium.UI.Media.Imaging;

/// <summary>What a queued scheduler job does.</summary>
internal enum BitmapDecodeJobKind
{
    /// <summary>
    /// Resolve encoded bytes and read intrinsic dimensions without decoding pixels. Drained
    /// ahead of every pixel job.
    /// </summary>
    Metadata,

    /// <summary>Full native decode plus display-bucket resample.</summary>
    Pixels,
}

/// <summary>
/// Process-wide, bounded scheduler for deferred bitmap decoding.
/// </summary>
/// <remarks>
/// <para>Image-heavy views frequently realize many sources in one layout pass. Dispatching one
/// ThreadPool work item per image lets native decoders saturate every core and starve input,
/// even though no more than a few results can become visible per frame. This scheduler keeps a
/// small number of decoding workers active and drains a FIFO of weak bitmap references.</para>
/// <para>The worker count has a FLOOR of two. It used to be
/// <c>Math.Clamp(ProcessorCount / 4, 1, 2)</c>, which gives exactly one worker on any machine
/// with seven or fewer logical cores — so a single image whose decode never terminated took the
/// only worker and every other image in the process stayed queued forever. That is what turned
/// one bad bucket predicate into a whole page rendering blank, and it is why the floor exists
/// even though the decode loop itself is now bounded.</para>
/// </remarks>
internal static class BitmapDecodeScheduler
{
    private const string WatchdogEnvironmentVariable = "JALIUM_IMAGE_DECODE_WATCHDOG_MS";
    private const int DefaultWatchdogMs = 5000;
    private const int MinimumWatchdogMs = 250;

    private readonly record struct WorkItem(WeakReference<BitmapImage> Source, long SourceVersion);

    private sealed record LiveWork(string Source, long StartedAtTimestamp);

    private static readonly ConcurrentQueue<WorkItem> s_pixelQueue = new();
    private static readonly ConcurrentQueue<WorkItem> s_metadataQueue = new();
    private static readonly ConcurrentDictionary<long, LiveWork> s_liveWork = new();
    private static readonly int s_maxWorkers = Math.Clamp(Environment.ProcessorCount / 4, 2, 3);
    private static readonly int s_watchdogMs = ResolveWatchdogMs();
    private static readonly object s_watchdogGate = new();

    private static Timer? s_watchdogTimer;
    private static bool s_watchdogArmed;
    private static long s_liveWorkIdSeed;
    private static int s_activeWorkers;
    private static int s_reliefWorkerActive;

    /// <summary>Number of jobs waiting to start.</summary>
    internal static int QueueLength => s_metadataQueue.Count + s_pixelQueue.Count;

    /// <summary>Number of workers currently running a job, including a relief worker.</summary>
    internal static int ActiveWorkers => Volatile.Read(ref s_activeWorkers);

    /// <summary>Steady-state worker cap. A stall watchdog may add exactly one worker above it.</summary>
    internal static int MaxWorkers => s_maxWorkers;

    internal static void Enqueue(BitmapImage source, long sourceVersion, BitmapDecodeJobKind kind)
    {
        var item = new WorkItem(new WeakReference<BitmapImage>(source), sourceVersion);
        if (kind == BitmapDecodeJobKind.Metadata)
        {
            s_metadataQueue.Enqueue(item);
        }
        else
        {
            s_pixelQueue.Enqueue(item);
        }

        PublishSchedulerState();
        TryStartWorkers();
    }

    private static void TryStartWorkers()
    {
        while (!s_metadataQueue.IsEmpty || !s_pixelQueue.IsEmpty)
        {
            var active = Volatile.Read(ref s_activeWorkers);
            if (active >= s_maxWorkers)
                return;

            if (Interlocked.CompareExchange(ref s_activeWorkers, active + 1, active) != active)
                continue;

            ArmWatchdog();
            ThreadPool.UnsafeQueueUserWorkItem(
                static isRelief => RunWorker(isRelief),
                state: false,
                preferLocal: false);
        }
    }

    /// <summary>
    /// Drains jobs until the queues are empty.
    /// </summary>
    /// <remarks>
    /// Runs as a <see cref="ThreadPool.UnsafeQueueUserWorkItem{TState}(Action{TState}, TState, bool)"/>
    /// callback, where an escaping exception terminates the process. Nothing may leave this method
    /// — not from a job, and not from the bookkeeping around one.
    /// </remarks>
    private static void RunWorker(bool isRelief)
    {
        try
        {
            while (TryDequeue(out var work, out var kind))
            {
                if (!work.Source.TryGetTarget(out var source))
                    continue;

                var workId = Interlocked.Increment(ref s_liveWorkIdSeed);
                s_liveWork[workId] = new LiveWork(
                    source.DiagnosticSourceName,
                    Stopwatch.GetTimestamp());
                PublishSchedulerState();
                try
                {
                    if (kind == BitmapDecodeJobKind.Metadata)
                    {
                        source.ProcessDeferredMetadata(work.SourceVersion);
                    }
                    else
                    {
                        source.ProcessDeferredDecode(work.SourceVersion);
                    }
                }
                catch (Exception ex)
                {
                    // This loop runs inside ThreadPool.UnsafeQueueUserWorkItem, where an escaping
                    // exception terminates the PROCESS. ProcessDeferredDecode handles its own
                    // failures, so anything caught here came from outside its try/catch — an
                    // application handler running inline inside a decode notification, or an
                    // allocation failure — and crashing a user's application from a decode worker
                    // is a strictly worse outcome than the blank image it would replace.
                    //
                    // Not a swallow: AbandonDecodeAfterWorkerFault reports through the
                    // Release-live ImageDiagnostics channel and, just as importantly, clears the
                    // source's queued/running flag so it is not left permanently un-decodable.
                    source.AbandonDecodeAfterWorkerFault(work.SourceVersion, ex);
                }
                finally
                {
                    s_liveWork.TryRemove(workId, out _);
                }
            }
        }
        catch (Exception ex)
        {
            // Belt and braces for the loop scaffolding itself (dequeue, weak-reference resolution,
            // the live-work table). The per-job catch above already covers the common case; this
            // one exists so that NO path out of this method is an unhandled exception on a pooled
            // thread. The worker exits and the finally below restarts one if work is left.
            ImageDiagnostics.DecodeFailed("<image decode scheduler>", "decode worker loop", ex);
        }
        finally
        {
            Interlocked.Decrement(ref s_activeWorkers);
            if (isRelief)
            {
                Volatile.Write(ref s_reliefWorkerActive, 0);
            }

            PublishSchedulerState();
            if (!s_metadataQueue.IsEmpty || !s_pixelQueue.IsEmpty)
            {
                TryStartWorkers();
            }
            else
            {
                DisarmWatchdogIfIdle();
            }
        }
    }

    /// <summary>
    /// Metadata jobs are drained to empty before any pixel job is touched: a metadata job reads a
    /// header and returns in microseconds, while a burst of pixel decodes can hold every worker
    /// for hundreds of milliseconds, and intrinsic sizes are needed by the very first layout pass.
    /// </summary>
    private static bool TryDequeue(out WorkItem work, out BitmapDecodeJobKind kind)
    {
        if (s_metadataQueue.TryDequeue(out work))
        {
            kind = BitmapDecodeJobKind.Metadata;
            return true;
        }

        if (s_pixelQueue.TryDequeue(out work))
        {
            kind = BitmapDecodeJobKind.Pixels;
            return true;
        }

        kind = BitmapDecodeJobKind.Pixels;
        return false;
    }

    private static void ArmWatchdog()
    {
        lock (s_watchdogGate)
        {
            if (s_watchdogArmed)
                return;

            s_watchdogTimer ??= new Timer(
                static _ => OnWatchdogTick(),
                state: null,
                dueTime: Timeout.Infinite,
                period: Timeout.Infinite);
            s_watchdogTimer.Change(s_watchdogMs, s_watchdogMs);
            s_watchdogArmed = true;
        }
    }

    private static void DisarmWatchdogIfIdle()
    {
        lock (s_watchdogGate)
        {
            if (!s_watchdogArmed || Volatile.Read(ref s_activeWorkers) > 0)
                return;

            s_watchdogTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            s_watchdogArmed = false;
        }
    }

    /// <summary>
    /// Reports any job that has been running longer than the watchdog interval and, when the pool
    /// is fully occupied by such jobs, adds exactly one worker above the cap.
    /// </summary>
    /// <remarks>
    /// The relief worker is a liveness valve, not a throughput knob: a decoder that blocks on a
    /// network share or a wedged native codec must not be able to hold the whole queue hostage.
    /// It is capped at one so a genuinely slow machine cannot cascade into unbounded parallel
    /// native decodes.
    /// </remarks>
    private static void OnWatchdogTick()
    {
        var now = Stopwatch.GetTimestamp();
        var stalled = 0;
        var queueLength = QueueLength;
        var activeWorkers = ActiveWorkers;

        foreach (var entry in s_liveWork)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(entry.Value.StartedAtTimestamp, now).TotalMilliseconds;
            if (elapsedMs < s_watchdogMs)
                continue;

            stalled++;
            ImageDiagnostics.DecodeStalled(entry.Value.Source, elapsedMs, queueLength, activeWorkers);
        }

        PublishSchedulerState();

        if (stalled == 0 || queueLength == 0)
            return;

        if (activeWorkers < s_maxWorkers)
            return;

        if (Interlocked.CompareExchange(ref s_reliefWorkerActive, 1, 0) != 0)
            return;

        Interlocked.Increment(ref s_activeWorkers);
        ThreadPool.UnsafeQueueUserWorkItem(
            static isRelief => RunWorker(isRelief),
            state: true,
            preferLocal: false);
    }

    private static void PublishSchedulerState()
    {
        double longestMs = 0;
        if (!s_liveWork.IsEmpty)
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var entry in s_liveWork)
            {
                var elapsedMs = Stopwatch
                    .GetElapsedTime(entry.Value.StartedAtTimestamp, now)
                    .TotalMilliseconds;
                if (elapsedMs > longestMs)
                {
                    longestMs = elapsedMs;
                }
            }
        }

        ImageDiagnostics.UpdateSchedulerState(QueueLength, ActiveWorkers, longestMs);
    }

    private static int ResolveWatchdogMs()
    {
        var configured = Environment.GetEnvironmentVariable(WatchdogEnvironmentVariable);
        if (int.TryParse(configured, out var parsed) && parsed >= MinimumWatchdogMs)
        {
            return parsed;
        }

        return DefaultWatchdogMs;
    }
}
