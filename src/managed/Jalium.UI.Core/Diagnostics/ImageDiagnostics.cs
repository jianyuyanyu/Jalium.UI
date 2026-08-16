using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Jalium.UI.Diagnostics;

/// <summary>Classifies an <see cref="ImageDiagnosticEvent"/>.</summary>
public enum ImageDiagnosticKind
{
    /// <summary>A decode was requested for a realized display size.</summary>
    DecodeRequested,

    /// <summary>Pixels were published.</summary>
    DecodeCompleted,

    /// <summary>Decoding or loading threw.</summary>
    DecodeFailed,

    /// <summary>A decode has been running longer than the watchdog interval.</summary>
    DecodeStalled,

    /// <summary>
    /// A decode chain was stopped because repeated decodes stopped producing a bigger raster.
    /// Seeing this at all means a bucket predicate is wrong; the resident pixels are still shown.
    /// </summary>
    BucketSaturated,

    /// <summary>A GPU upload of decoded pixels failed.</summary>
    UploadFailed,

    /// <summary>The pipeline fell back to a lower-fidelity path so something is still painted.</summary>
    Degraded,

    /// <summary>A visible failure placeholder was drawn in place of an image.</summary>
    PlaceholderShown,

    /// <summary>One-shot device/host capability line emitted on the first image event.</summary>
    CapabilitiesProbed,
}

/// <summary>
/// One reported image-pipeline event. Delivered to <see cref="ImageDiagnostics.Reported"/>
/// on whatever thread produced it.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Source">Stable identity of the image — URI, resource name, or type name.</param>
/// <param name="Detail">Free-form context. Never parsed; safe to change.</param>
/// <param name="Width">Pixel width the event refers to, or 0.</param>
/// <param name="Height">Pixel height the event refers to, or 0.</param>
/// <param name="Attempt">1-based decode attempt for this source version, or 0.</param>
/// <param name="ElapsedMs">Duration the event refers to, or 0.</param>
/// <param name="Error">The failure, when there is one.</param>
public readonly record struct ImageDiagnosticEvent(
    ImageDiagnosticKind Kind,
    string Source,
    string Detail,
    int Width,
    int Height,
    int Attempt,
    double ElapsedMs,
    Exception? Error);

/// <summary>Always-live image-pipeline counters, read with <see cref="ImageDiagnostics.Snapshot"/>.</summary>
/// <param name="SinkFaults">
/// Times a diagnostics sink itself failed — a subscriber that threw, an EventSource write that
/// failed, or a trace file that could not be opened. A reporting channel whose own breakage is
/// invisible is not a reporting channel, and the alternative (letting a sink exception escape into
/// a decode worker or the render path) is worse, so the fault is counted instead.
/// </param>
public readonly record struct ImageDiagnosticsSnapshot(
    long DecodesStarted,
    long DecodesCompleted,
    long DecodesFailed,
    long UpgradesScheduled,
    long BucketSaturatedCount,
    long StalledDecodeCount,
    long UploadFailureCount,
    long DegradedCount,
    long PlaceholdersShown,
    int QueueLength,
    int ActiveWorkers,
    double LongestRunningDecodeMs,
    long SinkFaults);

/// <summary>
/// The image pipeline's failure and progress channel.
/// </summary>
/// <remarks>
/// <para>Every counter and every <see cref="Reported"/> invocation is live in <b>Release</b>. That
/// is the entire point of this type: the upload and restore paths used to report through
/// <c>Debug.WriteLine</c>, which is <c>[Conditional("DEBUG")]</c> and therefore compiled
/// out of the builds users actually run — so an image that failed to upload produced a black
/// rectangle and not one line of evidence anywhere. Do not reintroduce a
/// <c>Debug.WriteLine</c> as the only output of an image failure path.</para>
/// <para>Text tracing is opt-in through <c>JALIUM_IMAGE_TRACE=1</c> and/or
/// <c>JALIUM_IMAGE_TRACE_FILE=&lt;path&gt;</c>; EventPipe/ETW consumers can enable the
/// <c>Jalium-UI-Image</c> EventSource instead. With all three off, a report costs a few
/// interlocked increments and a null check.</para>
/// </remarks>
public static class ImageDiagnostics
{
    private const string TraceEnvironmentVariable = "JALIUM_IMAGE_TRACE";
    private const string TraceFileEnvironmentVariable = "JALIUM_IMAGE_TRACE_FILE";

    private static readonly object s_gate = new();
    private static readonly bool s_debugEnabled = IsEnabledValue(
        Environment.GetEnvironmentVariable(TraceEnvironmentVariable));
    private static readonly string? s_traceFilePath = NormalizeTraceFilePath(
        Environment.GetEnvironmentVariable(TraceFileEnvironmentVariable));

    private static StreamWriter? s_traceFileWriter;
    private static bool s_traceFileWriterInitialized;

    private static long s_decodesStarted;
    private static long s_decodesCompleted;
    private static long s_decodesFailed;
    private static long s_upgradesScheduled;
    private static long s_bucketSaturated;
    private static long s_stalledDecodes;
    private static long s_uploadFailures;
    private static long s_degraded;
    private static long s_placeholdersShown;
    private static long s_sinkFaults;

    private static int s_queueLength;
    private static int s_activeWorkers;
    private static long s_longestRunningDecodeMicroseconds;
    private static int s_capabilitiesReported;

    /// <summary>
    /// Raised for every image-pipeline event, in Release as well as Debug. Handlers run
    /// synchronously on the reporting thread — which is frequently a decode worker — so they must
    /// not block, and must not throw (an exception from a handler is swallowed here so that
    /// diagnostics can never change rendering behaviour).
    /// </summary>
    public static event Action<ImageDiagnosticEvent>? Reported;

    /// <summary>
    /// Gets whether text or EventSource tracing is enabled. Counters and
    /// <see cref="Reported"/> are live regardless of this value.
    /// </summary>
    public static bool IsEnabled =>
        s_debugEnabled ||
        s_traceFilePath != null ||
        ImageEventSource.Log.IsEnabled();

    /// <summary>Reads the always-live counters.</summary>
    public static ImageDiagnosticsSnapshot Snapshot() => new(
        Interlocked.Read(ref s_decodesStarted),
        Interlocked.Read(ref s_decodesCompleted),
        Interlocked.Read(ref s_decodesFailed),
        Interlocked.Read(ref s_upgradesScheduled),
        Interlocked.Read(ref s_bucketSaturated),
        Interlocked.Read(ref s_stalledDecodes),
        Interlocked.Read(ref s_uploadFailures),
        Interlocked.Read(ref s_degraded),
        Interlocked.Read(ref s_placeholdersShown),
        Volatile.Read(ref s_queueLength),
        Volatile.Read(ref s_activeWorkers),
        Interlocked.Read(ref s_longestRunningDecodeMicroseconds) / 1000d,
        Interlocked.Read(ref s_sinkFaults));

    /// <summary>Resets every counter. Test-only; never call this from framework code.</summary>
    internal static void ResetCountersForTests()
    {
        Interlocked.Exchange(ref s_decodesStarted, 0);
        Interlocked.Exchange(ref s_decodesCompleted, 0);
        Interlocked.Exchange(ref s_decodesFailed, 0);
        Interlocked.Exchange(ref s_upgradesScheduled, 0);
        Interlocked.Exchange(ref s_bucketSaturated, 0);
        Interlocked.Exchange(ref s_stalledDecodes, 0);
        Interlocked.Exchange(ref s_uploadFailures, 0);
        Interlocked.Exchange(ref s_degraded, 0);
        Interlocked.Exchange(ref s_placeholdersShown, 0);
        Interlocked.Exchange(ref s_sinkFaults, 0);
        Interlocked.Exchange(ref s_longestRunningDecodeMicroseconds, 0);
    }

    /// <summary>Records that a native decode is about to run.</summary>
    public static void DecodeRequested(string source, int width, int height, int attempt)
    {
        Interlocked.Increment(ref s_decodesStarted);
        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.DecodeRequested, Normalize(source), string.Empty,
            width, height, attempt, 0d, null));
    }

    /// <summary>Records that pixels were published at <paramref name="width"/>x<paramref name="height"/>.</summary>
    public static void DecodeCompleted(string source, int width, int height, int attempt, double elapsedMs)
    {
        Interlocked.Increment(ref s_decodesCompleted);
        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.DecodeCompleted, Normalize(source), string.Empty,
            width, height, attempt, elapsedMs, null));
    }

    /// <summary>Records that another decode was scheduled because a larger bucket is reachable.</summary>
    public static void UpgradeScheduled(string source, int width, int height, int attempt)
    {
        Interlocked.Increment(ref s_upgradesScheduled);
        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.DecodeRequested, Normalize(source), "upgrade",
            width, height, attempt, 0d, null));
    }

    /// <summary>Records a decode or load failure.</summary>
    public static void DecodeFailed(string source, string detail, Exception? error)
    {
        Interlocked.Increment(ref s_decodesFailed);
        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.DecodeFailed, Normalize(source), detail ?? string.Empty,
            0, 0, 0, 0d, error));
    }

    /// <summary>
    /// Records that a decode chain reached its terminal state through the hard attempt bound —
    /// either because repeated decodes stopped producing a bigger raster, or because repeated
    /// decodes failed outright. Whatever pixels are resident stay on screen.
    /// </summary>
    /// <param name="source">Stable identity of the image.</param>
    /// <param name="publishedWidth">Width of the raster left resident, or 0 when none was published.</param>
    /// <param name="publishedHeight">Height of the raster left resident, or 0 when none was published.</param>
    /// <param name="attempts">Consecutive unproductive attempts that tripped the bound.</param>
    /// <param name="detail">
    /// Why the chain stopped. Defaults to the bucket-predicate case, which is always a framework
    /// bug — a correct predicate stops the chain by itself.
    /// </param>
    public static void BucketSaturated(
        string source,
        int publishedWidth,
        int publishedHeight,
        int attempts,
        string? detail = null)
    {
        Interlocked.Increment(ref s_bucketSaturated);
        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.BucketSaturated, Normalize(source),
            detail ?? "decode chain stopped by the unproductive-attempt bound",
            publishedWidth, publishedHeight, attempts, 0d, null));
    }

    /// <summary>Records that a decode has exceeded the scheduler watchdog interval.</summary>
    public static void DecodeStalled(string source, double elapsedMs, int queueLength, int activeWorkers)
    {
        Interlocked.Increment(ref s_stalledDecodes);
        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.DecodeStalled, Normalize(source),
            string.Create(CultureInfo.InvariantCulture, $"queue={queueLength} workers={activeWorkers}"),
            0, 0, 0, elapsedMs, null));
    }

    /// <summary>Records a GPU upload failure.</summary>
    public static void UploadFailed(string source, string stage, int width, int height, Exception? error)
    {
        Interlocked.Increment(ref s_uploadFailures);
        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.UploadFailed, Normalize(source), stage ?? string.Empty,
            width, height, 0, 0d, error));
    }

    /// <summary>Records a successful fallback to a lower-fidelity path.</summary>
    public static void Degraded(string source, string detail, int width, int height)
    {
        Interlocked.Increment(ref s_degraded);
        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.Degraded, Normalize(source), detail ?? string.Empty,
            width, height, 0, 0d, null));
    }

    /// <summary>Records that a visible failure placeholder replaced an image.</summary>
    public static void PlaceholderShown(string source, string detail)
    {
        Interlocked.Increment(ref s_placeholdersShown);
        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.PlaceholderShown, Normalize(source), detail ?? string.Empty,
            0, 0, 0, 0d, null));
    }

    /// <summary>Records the one-shot host/device capability line used to triage a suspect machine.</summary>
    public static void CapabilitiesProbed(string detail)
    {
        // Whoever reports a capability line satisfies the one-shot: a backend that probes the real
        // adapter must not be followed by this type's generic process line.
        Interlocked.Exchange(ref s_capabilitiesReported, 1);
        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.CapabilitiesProbed, "process", detail ?? string.Empty,
            0, 0, 0, 0d, null));
    }

    /// <summary>
    /// Emits the process capability line exactly once, ahead of the first real image record.
    /// </summary>
    /// <remarks>
    /// A support capture that opens with "decode failed" and nothing else cannot be triaged: the
    /// first question is always which machine produced it. Emitting the line lazily — from the
    /// first event rather than from a module initializer — keeps it free for a process that never
    /// touches an image, and guarantees it is the first line of any trace file rather than a line
    /// the reader has to go looking for.
    /// </remarks>
    private static void EnsureCapabilitiesReported()
    {
        if (Interlocked.CompareExchange(ref s_capabilitiesReported, 1, 0) != 0)
        {
            return;
        }

        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif

        Report(new ImageDiagnosticEvent(
            ImageDiagnosticKind.CapabilitiesProbed,
            "process",
            string.Create(
                CultureInfo.InvariantCulture,
                $"configuration={configuration} processors={Environment.ProcessorCount} " +
                $"arch={RuntimeInformation.ProcessArchitecture} os=\"{RuntimeInformation.OSDescription}\" " +
                $"runtime=\"{RuntimeInformation.FrameworkDescription}\" " +
                $"trace={(s_debugEnabled ? 1 : 0)} traceFile={(s_traceFilePath != null ? 1 : 0)}"),
            0,
            0,
            0,
            0d,
            null));
    }

    /// <summary>
    /// Publishes the decode scheduler's live state so <see cref="Snapshot"/> can report it without
    /// the diagnostics layer taking a dependency on the media assembly's scheduler internals.
    /// </summary>
    internal static void UpdateSchedulerState(int queueLength, int activeWorkers, double longestRunningMs)
    {
        Volatile.Write(ref s_queueLength, queueLength);
        Volatile.Write(ref s_activeWorkers, activeWorkers);
        Interlocked.Exchange(
            ref s_longestRunningDecodeMicroseconds,
            (long)Math.Clamp(longestRunningMs * 1000d, 0d, long.MaxValue));
    }

    private static void Report(in ImageDiagnosticEvent report)
    {
        if (report.Kind != ImageDiagnosticKind.CapabilitiesProbed)
        {
            EnsureCapabilitiesReported();
        }

        var handler = Reported;
        if (handler != null)
        {
            // One try/catch PER SUBSCRIBER, not one around the multicast invoke. A multicast
            // delegate stops at the first handler that throws, so a single broken sink used to
            // strand every sink registered after it — and since attach order is whatever order the
            // application and the framework happened to subscribe in, an app that attaches a
            // logger before its own telemetry sink loses the telemetry sink and nothing says so.
            // Silently losing the channel that exists to make silent failures visible is the worst
            // possible place for that bug, which is why the same walk already guards
            // BitmapImage.OnImageLoaded and CompositionTarget.Rendering.
            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action<ImageDiagnosticEvent>)subscriber)(report);
                }
                catch
                {
                    // A diagnostics subscriber must never be able to break the render path — this
                    // runs on decode workers and inside the render loop. The counters above already
                    // recorded the event, and the fault itself is counted rather than vanishing, so
                    // a broken sink is still visible to the next Snapshot(). Counted once per
                    // FAILING HANDLER rather than once per event: the per-event form could not
                    // distinguish one broken sink from a channel where every sink is dead, which is
                    // exactly the question a support capture with an empty trace file has to answer.
                    Interlocked.Increment(ref s_sinkFaults);
                }
            }
        }

        try
        {
            ImageEventSource.Log.ImageEvent(
                report.Kind.ToString(),
                report.Source,
                report.Detail,
                report.Width,
                report.Height,
                report.Attempt,
                report.ElapsedMs,
                report.Error?.GetType().FullName ?? string.Empty,
                report.Error?.Message ?? string.Empty);

            if (s_debugEnabled || s_traceFilePath != null)
            {
                WriteTextRecord(report);
            }
        }
        catch
        {
            // Tracing must never change application behaviour, but a trace sink that is silently
            // dropping every record would make a support capture actively misleading.
            Interlocked.Increment(ref s_sinkFaults);
        }
    }

    private static void WriteTextRecord(in ImageDiagnosticEvent report)
    {
        lock (s_gate)
        {
            if (s_debugEnabled)
            {
                using var debugWriter = new StringWriter(CultureInfo.InvariantCulture);
                WriteTextRecordCore(debugWriter, report);

                // Trace, NOT Debug. Debug.WriteLine is [Conditional("DEBUG")], so in the Release
                // build of this assembly — the one users actually run — the call would be compiled
                // away and JALIUM_IMAGE_TRACE=1 would silently produce nothing. That is the exact
                // defect this whole type exists to remove, and it would be at its worst here: a
                // support capture that comes back empty reads as "the image path is fine".
                // TRACE is defined by the SDK in Debug AND Release, and the default listener
                // forwards to OutputDebugString, so a debugger or DebugView sees these records in
                // a shipped build.
                Trace.WriteLine(debugWriter.ToString().TrimEnd('\r', '\n'));
            }

            var fileWriter = GetTraceFileWriter();
            if (fileWriter != null)
            {
                WriteTextRecordCore(fileWriter, report);
                fileWriter.Flush();
            }
        }
    }

    private static void WriteTextRecordCore(TextWriter writer, in ImageDiagnosticEvent report)
    {
        writer.Write("[Jalium.Image] kind=");
        writer.Write(report.Kind.ToString());
        writer.Write(" source=");
        WriteQuoted(writer, report.Source);
        writer.Write(" size=");
        writer.Write(report.Width.ToString(CultureInfo.InvariantCulture));
        writer.Write('x');
        writer.Write(report.Height.ToString(CultureInfo.InvariantCulture));
        writer.Write(" attempt=");
        writer.Write(report.Attempt.ToString(CultureInfo.InvariantCulture));
        writer.Write(" elapsed_ms=");
        writer.Write(report.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture));
        writer.Write(" thread_id=");
        writer.Write(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
        writer.Write(" detail=");
        WriteQuoted(writer, report.Detail);
        if (report.Error != null)
        {
            writer.Write(" error=");
            WriteQuoted(writer, report.Error.GetType().FullName ?? "Exception");
            writer.Write(" message=");
            WriteQuoted(writer, report.Error.Message);
        }

        writer.WriteLine();
    }

    private static StreamWriter? GetTraceFileWriter()
    {
        if (s_traceFileWriterInitialized)
        {
            return s_traceFileWriter;
        }

        s_traceFileWriterInitialized = true;
        if (s_traceFilePath == null)
        {
            return null;
        }

        try
        {
            string? directory = Path.GetDirectoryName(s_traceFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            s_traceFileWriter = new StreamWriter(new FileStream(
                s_traceFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite));
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            return s_traceFileWriter;
        }
        catch
        {
            // An unwritable trace path must not take the application down. The counters and the
            // Reported event remain live, so the channel is still observable — and the failure to
            // open the file is counted, so "the trace file is empty" is distinguishable from "the
            // trace file could never be created".
            Interlocked.Increment(ref s_sinkFaults);
            return null;
        }
    }

    private static void WriteQuoted(TextWriter writer, string value)
    {
        writer.Write('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\': writer.Write("\\\\"); break;
                case '"': writer.Write("\\\""); break;
                case '\r': writer.Write("\\r"); break;
                case '\n': writer.Write("\\n"); break;
                case '\t': writer.Write("\\t"); break;
                default: writer.Write(character); break;
            }
        }
        writer.Write('"');
    }

    private static string Normalize(string? source) =>
        string.IsNullOrEmpty(source) ? "<unknown>" : source;

    private static bool IsEnabledValue(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeTraceFilePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void OnProcessExit(object? sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        lock (s_gate)
        {
            try
            {
                s_traceFileWriter?.Flush();
            }
            catch
            {
                // Process-exit diagnostics must never change shutdown behaviour.
            }
        }
    }
}

[EventSource(Name = "Jalium-UI-Image")]
internal sealed class ImageEventSource : EventSource
{
    internal static readonly ImageEventSource Log = new();

    private ImageEventSource()
    {
    }

    [Event(1, Level = EventLevel.Informational)]
    public void ImageEvent(
        string kind,
        string source,
        string detail,
        int width,
        int height,
        int attempt,
        double elapsedMs,
        string errorType,
        string errorMessage)
    {
        if (IsEnabled())
        {
            WriteEvent(1, kind, source, detail, width, height, attempt, elapsedMs,
                errorType, errorMessage);
        }
    }
}
