using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Logging;
using Avalonia.Metadata;
using Avalonia.Threading;

namespace Avalonia.Rendering
{
    /// <summary>
    /// Provides factory methods for creating <see cref="IRenderLoop"/> instances.
    /// </summary>
    [PrivateApi]
    public static class RenderLoop
    {
        /// <summary>
        /// Creates an <see cref="IRenderLoop"/> from an <see cref="IRenderTimer"/>.
        /// </summary>
        public static IRenderLoop FromTimer(IRenderTimer timer) => new DefaultRenderLoop(timer);
    }

    /// <summary>
    /// Default implementation of the application render loop.
    /// </summary>
    /// <remarks>
    /// The render loop is responsible for advancing the animation timer and updating the scene
    /// graph for visible windows. It owns the sleep/wake state machine: setting
    /// <see cref="IRenderTimer.Tick"/> to a non-null callback to start the timer and to null to
    /// stop it, under a lock so that timer implementations never see concurrent changes.
    /// </remarks>
    internal class DefaultRenderLoop : IRenderLoop
    {
        private readonly List<IRenderLoopTask> _items = new List<IRenderLoopTask>();
        private readonly List<IRenderLoopTask> _itemsCopy = new List<IRenderLoopTask>();
        private Action<TimeSpan> _tick;
        private readonly IRenderTimer _timer;
        private readonly object _timerLock = new();
        private int _inTick;
        private volatile bool _hasItems;
        private bool _running;
        private bool _wakeupPending;

        // --- Tick-loop diagnostic ----------------------------------------------------------
        // Splits the time around each tick into:
        //   gap        : time from previous TimerTick exit -> this TimerTick entry
        //                (timer-side latency: VulkanRenderTimer wait + dispatch)
        //   pre        : TimerTick entry -> first task.Render() call (lock + items copy)
        //   render     : sum of all task.Render() durations on this tick
        //                (compositor + scene graph + Skia + native render target)
        //   post       : last task.Render() return -> TimerTick exit (cleanup + epilogue)
        //
        // Logs only the worst N ticks per 1s window so console I/O cannot perturb timings.
        private const double DiagJitterThresholdMs = 9.0;
        private const int    DiagWorstPerWindow    = 5;
        private long _diagPrevTickExitTs;
        private long _diagLastReportTs;
        private long _diagTickCount;
        private long _diagJitterCount;
        private readonly double[] _diagWorstInterval = new double[DiagWorstPerWindow];
        private readonly double[] _diagWorstGap     = new double[DiagWorstPerWindow];
        private readonly double[] _diagWorstPre     = new double[DiagWorstPerWindow];
        private readonly double[] _diagWorstRender  = new double[DiagWorstPerWindow];
        private readonly double[] _diagWorstPost    = new double[DiagWorstPerWindow];
        private readonly int   [] _diagWorstTaskCt  = new int   [DiagWorstPerWindow];
        private readonly long  [] _diagWorstTickNo  = new long  [DiagWorstPerWindow];
        
        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultRenderLoop"/> class.
        /// </summary>
        /// <param name="timer">The render timer.</param>
        public DefaultRenderLoop(IRenderTimer timer)
        {
            _timer = timer;
            _tick = TimerTick;
        }

        /// <inheritdoc/>
        public void Add(IRenderLoopTask i)
        {
            _ = i ?? throw new ArgumentNullException(nameof(i));
            Dispatcher.UIThread.VerifyAccess();

            bool shouldStart;
            lock (_items)
            {
                _items.Add(i);
                shouldStart = _items.Count == 1;
            }

            if (shouldStart)
            {
                _hasItems = true;
                Wakeup();
            }
        }

        /// <inheritdoc/>
        public void Remove(IRenderLoopTask i)
        {
            _ = i ?? throw new ArgumentNullException(nameof(i));
            Dispatcher.UIThread.VerifyAccess();

            bool shouldStop;
            lock (_items)
            {
                _items.Remove(i);
                shouldStop = _items.Count == 0;
            }

            if (shouldStop)
            {
                _hasItems = false;
                lock (_timerLock)
                {
                    if (_running)
                    {
                        _running = false;
                        _wakeupPending = false;
                        _timer.Tick = null;
                    }
                }
            }
        }

        /// <inheritdoc />
        public bool RunsInBackground => _timer.RunsInBackground;

        /// <inheritdoc />
        public void Wakeup()
        {
            lock (_timerLock)
            {
                if (_hasItems && !_running)
                {
                    _running = true;
                    _timer.Tick = _tick;
                }
                else
                {
                    _wakeupPending = true;
                }
            }
        }

        private void TimerTick(TimeSpan time)
        {
            if (Interlocked.CompareExchange(ref _inTick, 1, 0) == 0)
            {
                long diagTickEnterTs = Stopwatch.GetTimestamp();
                long diagFirstRenderEnterTs = 0;
                long diagLastRenderExitTs = 0;
                int  diagTaskCount = 0;
                try
                {
                    // Consume any pending wakeup — this tick will process its work.
                    // Only wakeups arriving during task execution will keep the timer running.
                    // Also drop late ticks that arrive after the timer was stopped.
                    lock (_timerLock)
                    {
                        if (!_running)
                            return;
                        _wakeupPending = false;
                    }

                    lock (_items)
                    {
                        _itemsCopy.Clear();
                        _itemsCopy.AddRange(_items);
                    }

                    var wantsNextTick = false;
                    diagTaskCount = _itemsCopy.Count;
                    diagFirstRenderEnterTs = Stopwatch.GetTimestamp();
                    for (int i = 0; i < _itemsCopy.Count; i++)
                    {
                        wantsNextTick |= _itemsCopy[i].Render();
                    }
                    diagLastRenderExitTs = Stopwatch.GetTimestamp();

                    _itemsCopy.Clear();

                    if (!wantsNextTick)
                    {
                        lock (_timerLock)
                        {
                            if (!_running)
                            {
                                // Already stopped by Remove()
                            }
                            else if (_wakeupPending)
                            {
                                _wakeupPending = false;
                            }
                            else
                            {
                                _running = false;
                                _timer.Tick = null;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this, "Exception in render loop: {Error}", ex);
                }
                finally
                {
                    long diagTickExitTs = Stopwatch.GetTimestamp();
                    DiagReportTick(diagTickEnterTs, diagFirstRenderEnterTs, diagLastRenderExitTs,
                        diagTickExitTs, diagTaskCount);
                    Interlocked.Exchange(ref _inTick, 0);
                }
            }
        }

        private void DiagReportTick(long tickEnterTs, long firstRenderEnterTs,
            long lastRenderExitTs, long tickExitTs, int taskCount)
        {
            double tickToMs = 1000.0 / Stopwatch.Frequency;
            _diagTickCount++;

            if (_diagPrevTickExitTs == 0)
            {
                _diagPrevTickExitTs = tickExitTs;
                _diagLastReportTs = tickExitTs;
                return;
            }

            double intervalMs = (tickExitTs - _diagPrevTickExitTs) * tickToMs;
            double gapMs      = (tickEnterTs - _diagPrevTickExitTs) * tickToMs;
            // pre = tick entry up to first task.Render() call (lock acquisitions, items copy).
            // render = the actual task work. post = cleanup. If no tasks ran (early return path),
            // pre/render/post stay 0 and the entire interval is just gap + tick overhead.
            double preMs    = firstRenderEnterTs > 0 ? (firstRenderEnterTs - tickEnterTs) * tickToMs : 0;
            double renderMs = (firstRenderEnterTs > 0 && lastRenderExitTs > 0)
                ? (lastRenderExitTs - firstRenderEnterTs) * tickToMs
                : 0;
            double postMs   = lastRenderExitTs > 0 ? (tickExitTs - lastRenderExitTs) * tickToMs : 0;
            _diagPrevTickExitTs = tickExitTs;

            if (intervalMs > DiagJitterThresholdMs)
            {
                _diagJitterCount++;
                int worstIdx = 0;
                for (int i = 1; i < DiagWorstPerWindow; i++)
                    if (_diagWorstInterval[i] < _diagWorstInterval[worstIdx]) worstIdx = i;
                if (intervalMs > _diagWorstInterval[worstIdx])
                {
                    _diagWorstInterval[worstIdx] = intervalMs;
                    _diagWorstGap     [worstIdx] = gapMs;
                    _diagWorstPre     [worstIdx] = preMs;
                    _diagWorstRender  [worstIdx] = renderMs;
                    _diagWorstPost    [worstIdx] = postMs;
                    _diagWorstTaskCt  [worstIdx] = taskCount;
                    _diagWorstTickNo  [worstIdx] = _diagTickCount;
                }
            }

            double sinceReportMs = (tickExitTs - _diagLastReportTs) * tickToMs;
            if (sinceReportMs >= 1000.0)
            {
                Console.Error.WriteLine(
                    $"[RL] 1s: ticks={_diagTickCount} jitter={_diagJitterCount} "
                    + $"avgFps={(_diagTickCount * 1000.0 / sinceReportMs):F1}");
                for (int i = 0; i < DiagWorstPerWindow; i++)
                {
                    if (_diagWorstInterval[i] <= 0) continue;
                    Console.Error.WriteLine(
                        $"  worst#{i} t={_diagWorstTickNo[i]} int={_diagWorstInterval[i]:F2}ms "
                        + $"gap={_diagWorstGap[i]:F2} pre={_diagWorstPre[i]:F2} "
                        + $"render={_diagWorstRender[i]:F2} post={_diagWorstPost[i]:F2} "
                        + $"tasks={_diagWorstTaskCt[i]}");
                    _diagWorstInterval[i] = 0; _diagWorstGap[i] = 0; _diagWorstPre[i] = 0;
                    _diagWorstRender[i] = 0; _diagWorstPost[i] = 0;
                    _diagWorstTaskCt[i] = 0; _diagWorstTickNo[i] = 0;
                }
                _diagTickCount = 0; _diagJitterCount = 0; _diagLastReportTs = tickExitTs;
            }
        }
    }
}
