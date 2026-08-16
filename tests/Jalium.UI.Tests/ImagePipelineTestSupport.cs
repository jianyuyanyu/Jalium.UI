using System.Collections.Concurrent;
using System.Diagnostics;
using Jalium.UI.Diagnostics;
using Jalium.UI.Media.Imaging;

namespace Jalium.UI.Tests;

/// <summary>
/// Shared fixtures for the image-pipeline regression suite.
/// </summary>
/// <remarks>
/// The defect these tests exist to pin is an <b>unbounded</b> decode chain: a request that the
/// bucket policy can never satisfy (a 24x24 icon asked for at 48px on a 200% display, or an
/// 8000x6000 photo asked for at 5120x3840) used to re-enqueue itself at the same source version
/// forever, and on a machine with few enough cores to run a single decode worker that took the
/// whole process's images with it. A test that merely waited for pixels and asserted they arrived
/// would PASS against that bug. So every assertion in this suite is phrased as an exact count of
/// native decoder entries, which is why the counters live in the decoder rather than in the test.
/// </remarks>
internal sealed class RecordingImageDecoder : INativeImageDecoder
{
    private readonly ConcurrentQueue<(int Width, int Height)> _decodedSizes = new();
    private int _decodeCalls;
    private int _dimensionCalls;
    private int _activeDecodes;
    private int _maxConcurrentDecodes;
    private int _completionSequence;
    private int _metadataCompletionOrder;

    /// <summary>Natural pixel width every decode reports, unless <see cref="NaturalSizeForCall"/> overrides it.</summary>
    internal int NaturalWidth { get; set; } = 1024;

    /// <summary>Natural pixel height every decode reports, unless <see cref="NaturalSizeForCall"/> overrides it.</summary>
    internal int NaturalHeight { get; set; } = 512;

    /// <summary>Pixel format the decoder claims. Carried into the published snapshot.</summary>
    internal NativePixelFormat OutputFormat { get; set; } = NativePixelFormat.Bgra8;

    /// <summary>Byte written into every channel of every pixel.</summary>
    internal byte Fill { get; set; } = 0x40;

    /// <summary>
    /// When set, every pixel encodes its own NATURAL coordinate instead of <see cref="Fill"/>:
    /// B = x, G = y, R = 0x80, A = 0xFF.
    /// </summary>
    /// <remarks>
    /// A uniform fill cannot distinguish "cropped at (10,10)" from "cropped at (0,0)", nor a crop
    /// taken against the natural image from one taken against the display bucket — which is exactly
    /// the defect the decode-option tests exist to pin. Coordinates above 255 wrap, so a probe must
    /// stay inside the first 256 rows/columns to read an exact value.
    /// </remarks>
    internal bool PositionEncodedPixels { get; set; }

    /// <summary>
    /// Overwrites the first pixel after the fill. Lets a test encode a channel-order probe — a
    /// pure red pixel is 0xFF at byte 0 in RGBA8 and at byte 2 in BGRA8 — so a swizzle is visible.
    /// </summary>
    internal byte[]? FirstPixelBytes { get; set; }

    /// <summary>
    /// When set, consulted on every <c>Decode</c>; a non-null result is thrown instead of pixels.
    /// Returning <see langword="null"/> lets that call succeed, which is how a TRANSIENT fault —
    /// a file lock that clears, a share that comes back — is expressed.
    /// </summary>
    internal Func<Exception?>? DecodeFault { get; set; }

    /// <summary>When set, every <c>Decode</c> blocks on it before producing pixels.</summary>
    internal ManualResetEventSlim? DecodeGate { get; set; }

    /// <summary>
    /// When set, every <c>Decode</c> takes one permit before producing pixels.
    /// </summary>
    /// <remarks>
    /// A <see cref="ManualResetEventSlim"/> can only release ALL waiters, so a test that opens it
    /// lets every queued decode run to completion in a burst and can only observe the resulting
    /// race. Permits release an exact number of decodes, which is what turns "did the scheduler
    /// drain the metadata job first" from a timing measurement into a deterministic one.
    /// </remarks>
    internal SemaphoreSlim? DecodePermits { get; set; }

    /// <summary>
    /// When set, every decode body runs under <c>lock</c> on this object, so at most one decode is
    /// ever in progress.
    /// </summary>
    /// <remarks>
    /// Models the machine the bug was reported from — seven or fewer logical cores, where the old
    /// <c>Math.Clamp(ProcessorCount / 4, 1, 2)</c> gave the decode pool exactly ONE worker — without
    /// reaching into the scheduler's static worker count, which is resolved once per process and
    /// cannot be varied per test. Serializing at the decoder is strictly stronger than serializing
    /// at the pool: it holds however many workers the host actually runs.
    /// </remarks>
    internal object? DecodeSerializer { get; set; }

    /// <summary>
    /// When set, consulted on every <c>TryReadDimensions</c>; a non-null result is thrown. Models a
    /// container whose header this decoder cannot read — a missing OS codec, a truncated file.
    /// </summary>
    internal Func<Exception?>? DimensionFault { get; set; }

    /// <summary>Per-call natural size, 1-based call index. Used to vary published geometry.</summary>
    internal Func<int, (int Width, int Height)>? NaturalSizeForCall { get; set; }

    /// <summary>Number of times a full pixel decode was entered.</summary>
    internal int DecodeCalls => Volatile.Read(ref _decodeCalls);

    /// <summary>Number of times the metadata-only dimension probe was entered.</summary>
    internal int DimensionCalls => Volatile.Read(ref _dimensionCalls);

    /// <summary>Peak number of decodes running at the same instant.</summary>
    internal int MaxConcurrentDecodes => Volatile.Read(ref _maxConcurrentDecodes);

    /// <summary>Number of decodes currently blocked inside the decoder.</summary>
    internal int ActiveDecodes => Volatile.Read(ref _activeDecodes);

    /// <summary>
    /// 1-based position of the metadata probe in the global completion order, or 0 when it has
    /// not completed. Pins the scheduler's "metadata jobs are drained before pixel jobs" rule.
    /// </summary>
    internal int MetadataCompletionOrder => Volatile.Read(ref _metadataCompletionOrder);

    /// <summary>Natural sizes handed to the pipeline, in completion order.</summary>
    internal IReadOnlyCollection<(int Width, int Height)> DecodedSizes => _decodedSizes;

    public DecodedImage Decode(
        ReadOnlySpan<byte> data,
        NativePixelFormat requestedFormat = NativePixelFormat.Bgra8)
    {
        _ = requestedFormat;

        if (DecodeSerializer is { } serializer)
        {
            lock (serializer)
            {
                return DecodeCore();
            }
        }

        return DecodeCore();
    }

    private DecodedImage DecodeCore()
    {
        var call = Interlocked.Increment(ref _decodeCalls);
        var concurrent = Interlocked.Increment(ref _activeDecodes);
        UpdateMaximum(ref _maxConcurrentDecodes, concurrent);
        try
        {
            DecodeGate?.Wait();
            DecodePermits?.Wait();

            if (DecodeFault?.Invoke() is { } fault)
            {
                throw fault;
            }

            var (width, height) = NaturalSizeForCall?.Invoke(call) ?? (NaturalWidth, NaturalHeight);
            var stride = width * 4;
            var buffer = new byte[stride * height];
            if (PositionEncodedPixels)
            {
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var offset = (y * stride) + (x * 4);
                        buffer[offset] = (byte)x;
                        buffer[offset + 1] = (byte)y;
                        buffer[offset + 2] = 0x80;
                        buffer[offset + 3] = 0xFF;
                    }
                }
            }
            else
            {
                Array.Fill(buffer, Fill);
                if (FirstPixelBytes is { Length: 4 } probe)
                {
                    probe.CopyTo(buffer, 0);
                }
            }

            _decodedSizes.Enqueue((width, height));
            return new DecodedImage(buffer, width, height, stride, OutputFormat);
        }
        finally
        {
            Interlocked.Decrement(ref _activeDecodes);
            Interlocked.Increment(ref _completionSequence);
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
        Interlocked.Increment(ref _dimensionCalls);
        if (DimensionFault?.Invoke() is { } fault)
        {
            throw fault;
        }

        width = NaturalWidth;
        height = NaturalHeight;
        Interlocked.CompareExchange(
            ref _metadataCompletionOrder,
            Interlocked.Increment(ref _completionSequence),
            0);
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

/// <summary>
/// A real on-disk file backing a deferred <see cref="BitmapImage"/>.
/// </summary>
/// <remarks>
/// <c>ConfigureDeferredFileSource</c> calls <c>File.Exists</c> before it will defer anything, so
/// the deferred path cannot be exercised from an in-memory byte array. The contents are irrelevant
/// — <see cref="RecordingImageDecoder"/> ignores them — but the file must exist for the lifetime
/// of the test, because the loader reads it on a decode worker.
/// </remarks>
internal sealed class TempImageFile : IDisposable
{
    internal TempImageFile()
    {
        Path = System.IO.Path.GetTempFileName();
        File.WriteAllBytes(Path, [0x89, 0x50, 0x4E, 0x47]);
        Uri = new Uri(Path);
    }

    internal string Path { get; }

    internal Uri Uri { get; }

    /// <summary>The identity <c>BitmapImage</c> reports to <see cref="ImageDiagnostics"/>.</summary>
    internal string DiagnosticSource => Uri.ToString();

    internal BitmapImage CreateDeferredImage() => new(Uri);

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // A decode worker may still hold the handle open when a test tears down early.
            // Leaving a temp file behind must never fail a test.
        }
    }
}

/// <summary>
/// Captures <see cref="ImageDiagnostics.Reported"/> events for one image and turns them into
/// awaitable checkpoints.
/// </summary>
/// <remarks>
/// <para>This is the suite's deterministic hook for background decode work. The task returned by
/// <c>RequestDecodeAsync</c> completes on the FIRST publish by design, so it says nothing about an
/// upgrade chain that is still running; waiting on a sleep instead would make the very bug under
/// test — an unbounded chain — invisible on a fast machine. Waiting for the Nth
/// <see cref="ImageDiagnosticKind.DecodeCompleted"/> record, or for the terminal
/// <see cref="ImageDiagnosticKind.BucketSaturated"/>, observes the chain itself.</para>
/// <para><see cref="ImageDiagnostics.Reported"/> is process-wide and fires on whichever thread
/// produced the event — normally a decode worker — so every record is filtered by source identity
/// and stored in a concurrent queue.</para>
/// </remarks>
internal sealed class ImageDiagnosticsRecorder : IDisposable
{
    private readonly string _source;
    private readonly ConcurrentQueue<ImageDiagnosticEvent> _events = new();
    private readonly Action<ImageDiagnosticEvent> _handler;

    internal ImageDiagnosticsRecorder(string source)
    {
        _source = source;
        _handler = OnReported;
        ImageDiagnostics.Reported += _handler;
    }

    internal IReadOnlyCollection<ImageDiagnosticEvent> Events => _events;

    internal int Count(ImageDiagnosticKind kind) => _events.Count(e => e.Kind == kind);

    internal int CompletedDecodes => Count(ImageDiagnosticKind.DecodeCompleted);

    /// <summary>Blocks until <paramref name="count"/> events of <paramref name="kind"/> arrive.</summary>
    /// <returns><see langword="true"/> when the count was reached before the deadline.</returns>
    internal bool WaitForCount(ImageDiagnosticKind kind, int count, TimeSpan timeout) =>
        ImagePipelineTestHarness.SpinUntil(() => Count(kind) >= count, timeout);

    /// <summary>
    /// Blocks until no new event has arrived for <paramref name="quietPeriod"/>, so an assertion
    /// on a total count cannot pass simply because the next decode had not started yet.
    /// </summary>
    internal void WaitUntilQuiet(TimeSpan quietPeriod, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        var lastCount = -1;
        var stableSince = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            var current = _events.Count;
            if (current != lastCount)
            {
                lastCount = current;
                stableSince.Restart();
            }
            else if (stableSince.Elapsed >= quietPeriod)
            {
                return;
            }

            Thread.Sleep(1);
        }
    }

    public void Dispose() => ImageDiagnostics.Reported -= _handler;

    private void OnReported(ImageDiagnosticEvent report)
    {
        if (string.Equals(report.Source, _source, StringComparison.Ordinal))
        {
            _events.Enqueue(report);
        }
    }
}

internal static class ImagePipelineTestHarness
{
    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or the timeout expires. The timeout is a
    /// failure guard only — every assertion in this suite is made on a count, never on the fact
    /// that a wait returned.
    /// </summary>
    internal static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return condition();
    }

    /// <summary>The standard failure-guard deadline for a background decode in this suite.</summary>
    internal static TimeSpan DecodeTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// The pixel size of the raster CURRENTLY RESIDENT — the display bucket the last decode
    /// published — or <c>(0, 0)</c> when nothing is resident.
    /// </summary>
    /// <remarks>
    /// Every assertion about the bucket ladder must go through this rather than through
    /// <c>PixelWidth</c>/<c>PixelHeight</c>. Those report the CANONICAL size and are invariant
    /// across the ladder by design — the display bucket is a function of the DPI and of the layout
    /// slot, and mirroring it into the intrinsic size fed layout its own output. So an assertion
    /// phrased on <c>PixelWidth</c> now passes whatever the bucket does, which for a suite whose
    /// entire purpose is to pin bucket behaviour is a false green.
    /// </remarks>
    internal static (int Width, int Height) PublishedBucket(BitmapImage image) =>
        image.TryGetPixelSnapshot(out var snapshot) && snapshot is not null
            ? (snapshot.Width, snapshot.Height)
            : (0, 0);

    /// <summary>
    /// Waits out a decode WITHOUT leaving the calling thread, for a test that goes on to touch
    /// dependency-property or <c>Freezable</c> state.
    /// </summary>
    /// <remarks>
    /// An <c>await</c> resumes on a pool thread, and every dependency-property write after that
    /// point fails its thread-affinity check — <c>InvalidOperationException: the calling thread
    /// cannot access this object</c> — which is a test-harness artefact and not the behaviour under
    /// test. Blocking here cannot deadlock: the decode runs on a <c>BitmapDecodeScheduler</c>
    /// worker and its completion source is created with
    /// <c>TaskCreationOptions.RunContinuationsAsynchronously</c>, so nothing it needs is queued
    /// behind this thread.
    /// </remarks>
    internal static void AwaitDecodeOnThisThread(Task decode) =>
        decode.WaitAsync(DecodeTimeout).GetAwaiter().GetResult();
}
