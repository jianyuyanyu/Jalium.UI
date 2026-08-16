using System.Diagnostics;
using Jalium.UI;
using Jalium.UI.Media;

namespace Jalium.UI.Threading;

/// <summary>
/// A timer that is integrated into the <see cref="Dispatcher"/> queue which is
/// processed at a specified interval of time and at a specified priority.
///
/// Optimization: when the interval matches the frame interval (8ms or 16ms),
/// the timer piggybacks on <see cref="CompositionTarget.Rendering"/> instead
/// of creating its own System.Threading.Timer. This eliminates timer
/// proliferation — all frame-rate timers share a single backing timer.
/// Piggybacked ticks are throttled to the nominal interval, so the uncapped
/// (1ms) frame loop cannot amplify e.g. a 16ms timer to full render rate.
/// </summary>
public sealed class DispatcherTimer
{
    private readonly Dispatcher _dispatcher;
    private Timer? _timer;
    private TimeSpan _interval;
    private bool _isEnabled;
    private object? _tag;
    private bool _useCompositionTarget; // True when piggybacking on frame timer
    private long _nextDueTimestamp;     // Stopwatch ticks; piggyback throttle deadline
    private int _tickPending;           // 0/1 gate: at most one queued tick may be in flight

    /// <summary>
    /// Occurs when the timer interval has elapsed.
    /// </summary>
    public event EventHandler? Tick;

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatcherTimer"/> class.
    /// </summary>
    public DispatcherTimer()
        : this(DispatcherPriority.Background)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatcherTimer"/> class
    /// which processes timer events at the specified priority.
    /// </summary>
    /// <param name="priority">The priority at which to invoke the timer.</param>
    public DispatcherTimer(DispatcherPriority priority)
        : this(priority, Dispatcher.CurrentDispatcher)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatcherTimer"/> class
    /// which processes timer events at the specified priority on the specified dispatcher.
    /// </summary>
    /// <param name="priority">The priority at which to invoke the timer.</param>
    /// <param name="dispatcher">The dispatcher to associate with the timer.</param>
    public DispatcherTimer(DispatcherPriority priority, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Dispatcher.ValidatePriority(priority, nameof(priority));
        Priority = priority;
        _interval = TimeSpan.Zero;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatcherTimer"/> class
    /// which uses the specified time interval, priority, event handler, and dispatcher.
    /// </summary>
    /// <param name="interval">The period of time between ticks.</param>
    /// <param name="priority">The priority at which to invoke the timer.</param>
    /// <param name="callback">The event handler to call when the Tick event occurs.</param>
    /// <param name="dispatcher">The dispatcher to associate with the timer.</param>
    public DispatcherTimer(TimeSpan interval, DispatcherPriority priority, EventHandler callback, Dispatcher dispatcher)
        : this(priority, dispatcher)
    {
        Interval = interval;

        if (callback != null)
        {
            Tick += callback;
        }
    }

    /// <summary>
    /// Gets the <see cref="Dispatcher"/> associated with this <see cref="DispatcherTimer"/>.
    /// </summary>
    public Dispatcher Dispatcher => _dispatcher;

    /// <summary>
    /// Gets or sets a value that indicates whether the timer is running.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;

                if (_isEnabled)
                {
                    StartTimer();
                }
                else
                {
                    StopTimer();
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets the period of time between timer ticks.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is less than 0 or greater than <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            if (value.TotalMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Interval cannot be negative.");
            }

            if (value.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Interval is too large.");
            }

            bool wasRunning = _isEnabled;

            if (wasRunning)
            {
                StopTimer();
            }

            _interval = value;

            if (wasRunning)
            {
                StartTimer();
            }
        }
    }

    /// <summary>
    /// Gets or sets the priority at which timer events are dispatched.
    /// </summary>
    public DispatcherPriority Priority { get; set; }

    /// <summary>
    /// Gets or sets a user-defined data object.
    /// </summary>
    public object? Tag
    {
        get => _tag;
        set => _tag = value;
    }

    /// <summary>
    /// Starts the <see cref="DispatcherTimer"/>.
    /// </summary>
    public void Start()
    {
        IsEnabled = true;
    }

    /// <summary>
    /// Stops the <see cref="DispatcherTimer"/>.
    /// </summary>
    public void Stop()
    {
        IsEnabled = false;
    }

    /// <summary>
    /// Determines if this timer's interval is short enough to piggyback on
    /// CompositionTarget.Rendering instead of creating a dedicated timer.
    /// Any interval at or below one display refresh period (e.g. 16ms at 60Hz)
    /// is merged into the centralized frame timer. This eliminates timer
    /// proliferation for frame-rate timers (animations, spring physics, etc.).
    /// </summary>
    private bool ShouldUseCompositionTarget()
    {
        int intervalMs = (int)_interval.TotalMilliseconds;
        int frameMs = 1000 / Math.Max(CompositionTarget.RefreshRate, 30);
        return intervalMs <= frameMs + 2;
    }

    private void StartTimer()
    {
        if (_timer != null || _useCompositionTarget)
        {
            return;
        }

        if (ShouldUseCompositionTarget())
        {
            // Piggyback on the centralized frame timer.
            // All frame-rate DispatcherTimers share a single System.Threading.Timer.
            // First tick is due one interval from now, matching the dedicated
            // timer's "first interval elapses before the first tick" semantics.
            _useCompositionTarget = true;
            _nextDueTimestamp = Stopwatch.GetTimestamp() + IntervalToStopwatchTicks(_interval);
            CompositionTarget.Rendering += OnCompositionTargetRendering;
            CompositionTarget.Subscribe();
            return;
        }

        // Non-frame-rate interval: use a dedicated timer (e.g., caret blink at 500ms)
        int intervalMs = Math.Max(1, (int)_interval.TotalMilliseconds);
        _timer = new Timer(
            OnTimerCallback,
            null,
            intervalMs,
            intervalMs);
    }

    private void StopTimer()
    {
        if (_useCompositionTarget)
        {
            _useCompositionTarget = false;
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            CompositionTarget.Unsubscribe();
            return;
        }

        if (_timer == null)
        {
            return;
        }

        _timer.Dispose();
        _timer = null;

        // Reopen the gate so a restarted timer is not blocked by a tick that was
        // still queued (or still running) when the timer was stopped.
        ExitTickGate(ref _tickPending);
    }

    /// <summary>
    /// Called by CompositionTarget.Rendering on the UI thread.
    /// Already on UI thread — raise tick directly, throttled to the nominal
    /// interval (the frame loop is uncapped and can run far above 60Hz).
    /// </summary>
    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        if (!_isEnabled) return;

        long now = Stopwatch.GetTimestamp();
        if (!ShouldFireOnFrame(now, IntervalToStopwatchTicks(_interval), ref _nextDueTimestamp)) return;

        RaiseTick();
    }

    /// <summary>
    /// Piggyback throttle: fires only once <paramref name="now"/> reaches the
    /// due timestamp, then re-arms one interval from NOW rather than from the
    /// previous due time, so a starved frame loop never builds up a backlog of
    /// catch-up ticks. A zero/near-frame interval (≤ the ≥1ms frame period) is
    /// due on every frame, leaving FrameInterval-based timers unaffected.
    /// </summary>
    internal static bool ShouldFireOnFrame(long now, long intervalTicks, ref long nextDueTimestamp)
    {
        if (now < nextDueTimestamp)
        {
            return false;
        }

        nextDueTimestamp = now + intervalTicks;
        return true;
    }

    private static long IntervalToStopwatchTicks(TimeSpan interval)
        => (long)(interval.TotalSeconds * Stopwatch.Frequency);

    private void OnTimerCallback(object? state)
    {
        if (!_isEnabled)
        {
            return;
        }

        // Dispatch the tick event to the associated dispatcher's thread
        try
        {
            if (_dispatcher.CheckAccess())
            {
                // Already on the dispatcher thread
                RaiseTick();
                return;
            }

            // At most one tick may be in flight. The backing System.Threading.Timer is
            // periodic and keeps firing on a thread-pool thread whether or not the UI
            // thread has processed anything; queueing every callback lets a starved
            // dispatcher (startup, a long layout pass, a blocking operation) accumulate
            // a backlog that then drains as a burst of ticks inside a single frame.
            //
            // That burst is observable, not theoretical: a staggered reveal driven by a
            // 55ms timer collapses into one frame, and periodic work (caret blink,
            // debounce, polling) fires several times back to back the moment the thread
            // frees up. WPF's DispatcherTimer never does this — it re-arms around the
            // dispatcher actually processing the tick, so a starved dispatcher yields
            // FEWER ticks rather than a catch-up burst.
            //
            // The piggyback path already guards this (see ShouldFireOnFrame, which re-arms
            // from "now" instead of the previous due time). This is the same rule for the
            // dedicated-timer path: drop callbacks that arrive while a tick is still
            // queued or still running, and let the next one be scheduled from a clean state.
            if (!TryEnterTickGate(ref _tickPending))
            {
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(RaiseQueuedTick);
            }
            catch
            {
                // Never leave the gate latched shut when the tick could not be queued,
                // otherwise the timer goes permanently silent after one failed dispatch.
                ExitTickGate(ref _tickPending);
                throw;
            }
        }
        catch
        {
            // Ignore exceptions if the dispatcher is shutting down
        }
    }

    /// <summary>
    /// Runs a queued tick and reopens the gate only after the handler returns, so a
    /// long-running handler cannot have further ticks pile up behind it either.
    /// </summary>
    private void RaiseQueuedTick()
    {
        try
        {
            RaiseTick();
        }
        finally
        {
            ExitTickGate(ref _tickPending);
        }
    }

    /// <summary>
    /// Claims the single in-flight tick slot. Returns <see langword="false"/> when a tick
    /// is already queued or running, in which case this timer callback must be dropped
    /// rather than queued behind it — dropping keeps the cadence, queueing creates a burst.
    /// </summary>
    internal static bool TryEnterTickGate(ref int tickPending)
        => Interlocked.CompareExchange(ref tickPending, 1, 0) == 0;

    /// <summary>
    /// Releases the in-flight tick slot. Must run even when the tick handler threw,
    /// otherwise the timer goes permanently silent.
    /// </summary>
    internal static void ExitTickGate(ref int tickPending)
        => Volatile.Write(ref tickPending, 0);

    private void RaiseTick()
    {
        if (!_isEnabled)
        {
            return;
        }

        try
        {
            Tick?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Exception silently handled to keep timer running
        }
    }
}
