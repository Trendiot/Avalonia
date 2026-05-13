using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using Avalonia.Rendering;
using Avalonia.Threading;

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
    /// propagates without restart. Defaults to the highest current refresh
    /// rate among monitors that host a visible window (refreshed once per
    /// second on the UI thread); null on non-Windows platforms unless the
    /// host wires its own. Override to inject a host-specific rate.
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
                    EnsureRefreshRateMonitorStarted();
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
                while(targetMs - (sw.Elapsed - _lastTickAt).TotalMilliseconds >= 0)
                    Thread.SpinWait(100);
            }
            
            _lastTickAt = sw.Elapsed;
            _tick?.Invoke(sw.Elapsed);
        }
    }

    // ---- Multi-monitor refresh rate tracking (Windows-only) -------------------
    //
    // Maintains a process-wide cache of the highest refresh rate among
    // monitors that currently host a visible Avalonia window. Refreshed once
    // per second on the UI thread (cheap: a handful of P/Invokes). The render
    // loop reads the cache via MaxRefreshRateHzProvider on the hot path with
    // zero allocations and zero lock contention.
    //
    // Non-Windows: provider stays null, no cap is applied (same as before).
    // Override MaxRefreshRateHzProvider to inject a host-specific rate.

    private const double FallbackRefreshHz = 60.0;
    private static long s_cachedHzBits = BitConverter.DoubleToInt64Bits(FallbackRefreshHz);
    private static int s_monitorStarted; // 0 = not started, 1 = started

    private static double GetCachedMaxRefreshHz()
        => BitConverter.Int64BitsToDouble(Interlocked.Read(ref s_cachedHzBits));

    private static void SetCachedMaxRefreshHz(double v)
        => Interlocked.Exchange(ref s_cachedHzBits, BitConverter.DoubleToInt64Bits(v));

    private static Func<double>? DefaultRefreshRateProvider()
        => OperatingSystem.IsWindows() ? GetCachedMaxRefreshHz : null;

    // Posts an immediate scan and a recurring 1 s DispatcherTimer onto the UI
    // thread. CompareExchange ensures we wire this up at most once even if
    // multiple VulkanRenderTimer instances exist. Wrapped in try/catch
    // because Dispatcher.UIThread may not be available (headless / test).
    private static void EnsureRefreshRateMonitorStarted()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Interlocked.CompareExchange(ref s_monitorStarted, 1, 0) != 0) return;

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                ScanRefreshRates();
                DispatcherTimer.Run(
                    () => { ScanRefreshRates(); return true; },
                    TimeSpan.FromSeconds(1),
                    DispatcherPriority.Background);
            }, DispatcherPriority.Background);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Warning, "VulkanDynamic")
                ?.Log(typeof(VulkanRenderTimer), $"Refresh-rate monitor not started: {e.Message}");
        }
    }

    // Walks the desktop lifetime's window list, maps each visible window to
    // its monitor, and stores the max current refresh rate. Falls back to the
    // primary monitor when no windows are available (early startup, hidden,
    // single-view lifetimes that don't expose a window collection).
    private static void ScanRefreshRates()
    {
        try
        {
            double max = 0;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var w in desktop.Windows)
                {
                    if (!w.IsVisible) continue;
                    var handle = w.TryGetPlatformHandle();
                    var hwnd = handle?.Handle ?? IntPtr.Zero;
                    if (hwnd == IntPtr.Zero) continue;
                    var hz = GetMonitorRefreshHzForWindow(hwnd);
                    if (hz > max) max = hz;
                }
            }
            if (max <= 0) max = GetWindowsPrimaryRefreshRateHz();
            if (max <= 0) max = FallbackRefreshHz;
            SetCachedMaxRefreshHz(max);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Verbose, "VulkanDynamic")
                ?.Log(typeof(VulkanRenderTimer), $"Refresh-rate scan failed: {e.Message}");
        }
    }

    private static unsafe double GetMonitorRefreshHzForWindow(IntPtr hwnd)
    {
        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero) return 0;

        var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        var miPtr = &mi;
        if (GetMonitorInfo(hMonitor, miPtr) == 0) return 0;

        var dm = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
        // miPtr->szDevice yields a char* (fixed buffer access through a struct
        // pointer is well-defined; using a `fixed` statement here would be a
        // CS0213 error since szDevice is already a fixed-size buffer).
        if (EnumDisplaySettings(miPtr->szDevice, ENUM_CURRENT_SETTINGS, &dm) == 0) return 0;

        return dm.dmDisplayFrequency;
    }

    // GDI's GetDeviceCaps(VREFRESH) returns the primary display's *current*
    // vertical refresh rate. Used as a fallback when no window is mappable
    // to a monitor (very early startup, no desktop lifetime, etc.).
    private static double GetWindowsPrimaryRefreshRateHz()
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return 0;
        try { return GetDeviceCaps(dc, VREFRESH); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private const int VREFRESH = 116;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int ENUM_CURRENT_SETTINGS = -1;

    // All P/Invoke signatures below use only blittable types. The Avalonia.Vulkan
    // assembly is built with [DisableRuntimeMarshalling], so bool returns and
    // [MarshalAs(ByValTStr)] strings would fail at runtime — Win32 BOOL maps to
    // int (0/nonzero), and the inline string buffers are exposed as fixed char
    // arrays accessed via unsafe pointers.

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hDC, int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern unsafe int GetMonitorInfo(IntPtr hMonitor, MONITORINFOEXW* lpmi);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW")]
    private static extern unsafe int EnumDisplaySettings(char* lpszDeviceName, int iModeNum, DEVMODEW* lpDevMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DEVMODEW
    {
        public fixed char dmDeviceName[32];
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        public fixed char dmFormName[32];
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

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
