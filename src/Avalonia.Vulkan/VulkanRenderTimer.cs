using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Logging;
using Avalonia.Rendering;

namespace Avalonia.Vulkan;

/// <summary>
/// <see cref="IRenderTimer"/> implementation for Vulkan.
/// </summary>
public class VulkanRenderTimer : IRenderTimer
{
    private readonly object _syncLock = new();
    private readonly AutoResetEvent _wakeEvent = new(false);
    private volatile Action<TimeSpan>? _tick;
    private bool _threadStarted;
    private Action? _waitForPresentFence;
    private TimeSpan _lastTickAt;

    /// <summary>
    /// Optional callback returning the current display refresh rate (Hz).
    /// Re-evaluated each iteration so a live monitor refresh-rate change
    /// propagates without restart. Defaults to a Windows GDI query of the
    /// primary display's current rate; null on other platforms unless the
    /// host wires its own.
    /// </summary>
    public Func<double>? MaxRefreshRateHzProvider { get; set; } = DefaultRefreshRateProvider();

    /// <summary>
    /// Multiplier applied to the refresh rate to derive the render-rate cap.
    /// Default 3.0 paces rendering to at most 3× the display refresh, which
    /// preserves the MAILBOX present-mode latency benefit (≤ 1/(3·Hz) input
    /// staleness at vsync) while bounding wasted GPU work to roughly 2
    /// throw-away frames per displayed frame. Set ≤ 0 to disable capping.
    /// </summary>
    public double RenderRateMultiplier { get; set; } = 3.0;

    /// <summary>
    /// Raised when the render timer ticks to signal a new frame should be drawn.
    /// </summary>
    /// <remarks>
    /// This event can be raised on any thread; it is the responsibility of the subscriber to
    /// switch execution to the right thread.
    /// </remarks>
    public Action<TimeSpan>? Tick
    {
        get => _tick;
        set
        {
            _tick = value;
            if (value != null)
            {
                if (!_threadStarted)
                {
                    _threadStarted = true;
                    Logger.TryGet(LogEventLevel.Debug, "VulkanDynamic")?.Log(this, "VulkanRenderTimer starting VSync thread");
                    new Thread(RenderLoop)
                    {
                        IsBackground = true,
                        Name = "VulkanDynamicVSync"
                    }.Start();
                }
                else
                {
                    _wakeEvent.Set();
                }
            }
        }
    }

    /// <summary>
    /// Indicates if the timer ticks on a non-UI thread
    /// </summary>
    public bool RunsInBackground => true;

    private void RenderLoop()
    {
        Logger.TryGet(LogEventLevel.Debug, "VulkanDynamic")?.Log(this, "VSync render loop started");
        Stopwatch sw = Stopwatch.StartNew();

        CancellationTokenSource cts = new();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            cts.Cancel();

        // Bootstrap with initial tick to start rendering
        while (_waitForPresentFence == null)
        {
            if (_tick == null) 
            { 
                _wakeEvent.WaitOne(16); 
                continue;
            }
            _tick?.Invoke(sw.Elapsed);
        }

        while (!cts.IsCancellationRequested)
        {
            if (_tick == null) { _wakeEvent.WaitOne(); continue; }

            if (_waitForPresentFence != null)
            {
                try
                {
                    lock (_syncLock)
                    {
                        _waitForPresentFence();
                        _waitForPresentFence = null;
                    }
                }
                catch (VulkanException e)
                {
                    Logger.TryGet(LogEventLevel.Verbose, "VulkanDynamic RenderLoop")
                        ?.Log(this, $"{e.Message}");
                    if (!e.Message.Equals("vkWaitForFences returned VK_TIMEOUT", StringComparison.OrdinalIgnoreCase))
                        throw;
                    lock (_syncLock)
                        _waitForPresentFence = null;
                }
            }
            else
            {
                // No fence pending: a tick ran but produced no present (the
                // common case when ServerCompositionTarget.Render early-returns
                // because nothing was dirty this iteration — empty ticks are
                // normal in this loop, which pumps a tick every iteration).
                //
                // The 1 ms timeout is intentional. With a 16 ms timeout, runs
                // of consecutive empty ticks at high refresh rate cap throughput
                // at roughly 60–115 fps because each empty tick burns up to a
                // full vsync interval. With 1 ms the empty-tick cost is
                // negligible and the loop is paced by the present fence wait
                // (~6 ms at 165 Hz). CPU cost of polling at 1 ms is also
                // negligible — the wait event still wakes us immediately on
                // SetPresentFenceWaitAction in the steady-state path.
                _wakeEvent.WaitOne(1);
            }

            // Cap render rate at RenderRateMultiplier × current display refresh.
            // Re-querying every iteration picks up live changes to the monitor
            // refresh rate without a restart. Without this cap, MAILBOX +
            // sub-millisecond GPU frames produce 1000+ fps of throw-away work
            // (the display engine drops all but the latest queued frame at
            // every vsync). The default 3× multiplier preserves the MAILBOX
            // input-latency benefit while eliminating ~80% of the waste.
            //
            // Thread.Sleep (not _wakeEvent.WaitOne) so per-frame fence-update
            // signals don't truncate the cap: SetPresentFenceWaitAction sets
            // _wakeEvent every frame and would otherwise wake us instantly.
            var rateHz = MaxRefreshRateHzProvider?.Invoke() ?? 0;
            if (rateHz > 0 && RenderRateMultiplier > 0)
            {
                var targetMs = 1000.0 / (rateHz * RenderRateMultiplier);
                var deficitMs = targetMs - (sw.Elapsed - _lastTickAt).TotalMilliseconds;
                if (deficitMs >= 1)
                    Thread.Sleep((int)deficitMs);
            }
            _lastTickAt = sw.Elapsed;

            _tick?.Invoke(sw.Elapsed);
        }
    }

    private static Func<double>? DefaultRefreshRateProvider()
        => OperatingSystem.IsWindows() ? GetWindowsPrimaryRefreshRateHz : null;

    // GDI's GetDeviceCaps(VREFRESH) returns the primary display's *current*
    // vertical refresh rate; it tracks user-initiated refresh-rate changes
    // without requiring the process to restart.
    //
    // Multi-monitor caveat: this returns only the primary monitor's rate. A
    // window on a different-rate secondary monitor will still be capped to
    // the primary's rate × multiplier. Hosts that need per-window precision
    // can override MaxRefreshRateHzProvider; multi-monitor enhancement (e.g.
    // mirror DxgiConnection.GetAllMonitorFrequencies, take max) is a follow-up.
    private static double GetWindowsPrimaryRefreshRateHz()
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return 0;
        try { return GetDeviceCaps(dc, VREFRESH); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private const int VREFRESH = 116;
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hDC, int nIndex);

    /// <summary>
    /// Updates the fence for Vsync Operations.
    /// </summary>
    /// <param name="fenceWaitAction"></param>
    public void SetPresentFenceWaitAction(Action fenceWaitAction)
    {
        lock (_syncLock)
            _waitForPresentFence = fenceWaitAction;
        // Wake the render loop in case it's currently sitting in the no-fence
        // WaitOne branch. See that branch for why the timeout there is short.
        _wakeEvent.Set();
        Logger.TryGet(LogEventLevel.Verbose, "VulkanDynamic")
            ?.Log(this, "Present fence wait action set for VSync synchronization");
    }
}
