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
    public double RenderRateMultiplier { get; set; } = 1.0;

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
            // Thread.SpinWait (not _wakeEvent.WaitOne) so per-frame fence-update
            // signals don't truncate the cap: SetPresentFenceWaitAction sets
            // _wakeEvent every frame and would otherwise wake us instantly.
            //
            //Use SpinWait as windows sleeps threads at ~16ms
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

    private double _cachedRefreshRate = 0;
    
    // ---- Multi-monitor refresh rate tracking -----------------------------------
    //
    // Maintains a process-wide cache of the highest refresh rate among
    // monitors that currently host a visible Avalonia window. Refreshed once
    // per second on the UI thread (cheap: a handful of P/Invokes). The render
    // loop reads the cache via MaxRefreshRateHzProvider on the hot path with
    // zero allocations and zero lock contention.
    //
    // Windows: walks visible HWNDs, MonitorFromWindow + EnumDisplaySettings.
    // Linux/X11: walks visible XIDs, XTranslateCoordinates + XRR CRTC walk.
    // Other platforms: provider stays null, no cap is applied (same as before).
    // Override MaxRefreshRateHzProvider to inject a host-specific rate.

    private const double FallbackRefreshHz = 60.0;
    private static long s_cachedHzBits = BitConverter.DoubleToInt64Bits(FallbackRefreshHz);
    private static int s_monitorStarted; // 0 = not started, 1 = started

    private static double GetCachedMaxRefreshHz()
        => BitConverter.Int64BitsToDouble(Interlocked.Read(ref s_cachedHzBits));

    private static void SetCachedMaxRefreshHz(double v)
        => Interlocked.Exchange(ref s_cachedHzBits, BitConverter.DoubleToInt64Bits(v));

    private static Func<double>? DefaultRefreshRateProvider()
    {
        if (OperatingSystem.IsWindows()) return GetCachedMaxRefreshHz;
        if (OperatingSystem.IsLinux()) return GetCachedMaxRefreshHz;
        return null;
    }

    // Posts an immediate scan and a recurring 1 s DispatcherTimer onto the UI
    // thread. CompareExchange ensures we wire this up at most once even if
    // multiple VulkanRenderTimer instances exist. Wrapped in try/catch
    // because Dispatcher.UIThread may not be available (headless / test).
    private static void EnsureRefreshRateMonitorStarted()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        if (Interlocked.CompareExchange(ref s_monitorStarted, 1, 0) != 0) return;

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                Action scan = OperatingSystem.IsWindows()
                    ? ScanRefreshRatesWindows
                    : ScanRefreshRatesX11;
                scan();
                DispatcherTimer.Run(
                    () => { scan(); return true; },
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

    // ---- Windows ----------------------------------------------------------------
    //
    // Walks the desktop lifetime's window list, maps each visible window to
    // its monitor, and stores the max current refresh rate. Falls back to the
    // primary monitor when no windows are available (early startup, hidden,
    // single-view lifetimes that don't expose a window collection).
    private static void ScanRefreshRatesWindows()
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

    // ---- Linux/X11 -------------------------------------------------------------
    //
    // Returns the MINIMUM refresh rate across active CRTCs — *not* the max,
    // because X11 multi-monitor compositing is asymmetric to Windows DWM:
    //
    //   Windows DWM (esp. Win 11): per-output vsync. A window on the 60 Hz
    //   panel scans out at 60 while a window on the 165 Hz panel scans out at
    //   165 concurrently. Capping per-window at MAX-of-windowed-monitors is
    //   correct.
    //
    //   X11: one logical screen, one compositor render thread tied to a
    //   single vsync source. Mutter / KWin / picom / Xfwm-compositing all
    //   pace every output at one rate (usually the lowest active, sometimes
    //   the primary). Even Avalonia windows on the 165 Hz panel of a 60+165
    //   setup get composited at the slower rate. Per-output vsync is a
    //   Wayland-era property, not X11.
    //
    //   Empirically confirmed on a Titan RTX with a 60 Hz + 165 Hz pair:
    //   pulling the 165 Hz panel as primary still paced the 165 Hz output at
    //   60. So capping render at 3× MAX (495 fps) would produce ~8× more
    //   frames than the compositor can ever consume. 3× MIN matches the
    //   compositor's actual pace.
    //
    // We don't walk windows on X11 (which CRTC the window is on doesn't
    // matter — the compositor paces globally). Just iterate active CRTCs
    // and take the min. Disabled CRTCs (Mode == 0) are skipped so a parked
    // headless output doesn't lock the cap at 0.
    //
    // Display handling: we open our own Xlib connection via XOpenDisplay(null)
    // — Avalonia.X11 owns its Display but it's not exposed to Avalonia.Vulkan
    // and Xlib connections aren't safe to share across threads without
    // XInitThreads anyway. The connection is opened lazily on first scan and
    // kept for the life of the process (one open socket; the OS reclaims on
    // exit). All XR* calls happen on the UI thread under the same
    // DispatcherTimer the Windows path uses.
    //
    // Library availability: libXrandr.so.2 isn't guaranteed on every Linux
    // (Wayland-only sessions, headless containers). The first failed call
    // throws DllNotFoundException, which we catch once and latch into
    // s_x11Probed so we never retry the lookup on the hot path.

    private static IntPtr s_x11Display;
    private static bool s_x11Probed;        // set after first XOpenDisplay attempt
    private static bool s_x11Available;     // true if XOpenDisplay + libXrandr both work

    private static void ScanRefreshRatesX11()
    {
        if (!TryEnsureX11()) return;
        try
        {
            var dpy = s_x11Display;
            IntPtr resources = XRRGetScreenResourcesCurrent(dpy, XDefaultRootWindow(dpy));
            if (resources == IntPtr.Zero) return;
            double rate;
            try { rate = GetX11MinActiveCrtcHz(dpy, resources); }
            finally { XRRFreeScreenResources(resources); }
            if (rate <= 0) rate = FallbackRefreshHz;
            SetCachedMaxRefreshHz(rate);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Verbose, "VulkanDynamic")
                ?.Log(typeof(VulkanRenderTimer), $"X11 refresh-rate scan failed: {e.Message}");
        }
    }

    // Iterate every CRTC on the screen, ignore disabled ones (Mode == 0),
    // compute refresh = dotClock / (hTotal × vTotal) for each, return the
    // minimum. Returns 0 if no active CRTC is found so the caller falls back
    // to FallbackRefreshHz (60).
    private static unsafe double GetX11MinActiveCrtcHz(IntPtr dpy, IntPtr resources)
    {
        var res = (XRRScreenResources*)resources;
        var crtcs = res->Crtcs;
        int nCrtc = res->NCrtc;
        double min = 0;
        for (int i = 0; i < nCrtc; i++)
        {
            IntPtr info = XRRGetCrtcInfo(dpy, resources, crtcs[i]);
            if (info == IntPtr.Zero) continue;
            try
            {
                var ci = (XRRCrtcInfo*)info;
                if (ci->Mode == IntPtr.Zero) continue; // disabled CRTC
                double hz = ComputeRefreshHz(res, ci->Mode);
                if (hz <= 0) continue;
                if (min == 0 || hz < min) min = hz;
            }
            finally
            {
                XRRFreeCrtcInfo(info);
            }
        }
        return min;
    }

    // Finds modeId in resources.Modes[] and returns dotClock / (hTotal * vTotal).
    // Mode IDs are unique within a screen so the linear scan is bounded by
    // mode count, which is small (~handful to a few dozen) on any real config.
    private static unsafe double ComputeRefreshHz(XRRScreenResources* res, IntPtr modeId)
    {
        var modes = res->Modes;
        int n = res->NMode;
        for (int i = 0; i < n; i++)
        {
            if (modes[i].Id != modeId) continue;
            ulong dotClock = (ulong)modes[i].DotClock.ToInt64();
            uint hTotal = modes[i].HTotal;
            uint vTotal = modes[i].VTotal;
            if (dotClock == 0 || hTotal == 0 || vTotal == 0) return 0;
            return dotClock / (double)(hTotal * (ulong)vTotal);
        }
        return 0;
    }

    private static bool TryEnsureX11()
    {
        if (s_x11Probed) return s_x11Available;
        s_x11Probed = true;
        try
        {
            s_x11Display = XOpenDisplay(IntPtr.Zero);
            if (s_x11Display == IntPtr.Zero) return false;
            // Touch a libXrandr entry point so DllNotFoundException latches
            // here, before any per-frame call path can hit it. The actual
            // result is discarded — we just want to know the symbol resolves.
            _ = XRRGetScreenResourcesCurrent(s_x11Display, XDefaultRootWindow(s_x11Display));
            s_x11Available = true;
            return true;
        }
        catch (DllNotFoundException)
        {
            // libX11.so.6 or libXrandr.so.2 not present (Wayland-only host,
            // minimal container). Stay silent, leave the cache at fallback.
            return false;
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Verbose, "VulkanDynamic")
                ?.Log(typeof(VulkanRenderTimer), $"X11 probe failed: {e.Message}");
            return false;
        }
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libXrandr.so.2")] private static extern IntPtr XRRGetScreenResourcesCurrent(IntPtr display, IntPtr window);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeScreenResources(IntPtr resources);
    [DllImport("libXrandr.so.2")] private static extern IntPtr XRRGetCrtcInfo(IntPtr display, IntPtr resources, IntPtr crtc);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeCrtcInfo(IntPtr info);

    // XRRScreenResources, XRRCrtcInfo, XRRModeInfo — field layouts mirror the
    // C headers in libXrandr's randr.h / Xrandr.h. Only the fields we read
    // are commented; unread trailing fields are still declared so the C#
    // struct *size* matches the C struct size — required because we index
    // Crtcs[] / Modes[] / Outputs[] arrays of these structs. Each `Time`,
    // `RRCrtc`, `RROutput`, `RRMode`, `XRRModeFlags` field is a Linux
    // `unsigned long` (XID or time), which is 4 bytes on 32-bit and 8 on
    // 64-bit — IntPtr matches that on both. C# Sequential layout reproduces
    // the C compiler's natural alignment padding.

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct XRRScreenResources
    {
        public IntPtr Timestamp;
        public IntPtr ConfigTimestamp;
        public int NCrtc;
        public IntPtr* Crtcs;     // RRCrtc* — indexed by ScanRefreshRatesX11
        public int NOutput;
        public IntPtr* Outputs;   // unused; declared for size
        public int NMode;
        public XRRModeInfo* Modes; // indexed by ComputeRefreshHz
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct XRRCrtcInfo
    {
        public IntPtr Timestamp;
        public int X;
        public int Y;
        public uint Width;
        public uint Height;
        public IntPtr Mode;       // RRMode — 0 when CRTC is disabled
        public ushort Rotation;
        public int NOutput;       // unused; declared for size
        public IntPtr* Outputs;
        public ushort Rotations;
        public int NPossible;
        public IntPtr* Possible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct XRRModeInfo
    {
        public IntPtr Id;
        public uint Width;
        public uint Height;
        public IntPtr DotClock;   // unsigned long, in Hz of pixel clock
        public uint HSyncStart;
        public uint HSyncEnd;
        public uint HTotal;       // horizontal total (pixels per line)
        public uint HSkew;
        public uint VSyncStart;
        public uint VSyncEnd;
        public uint VTotal;       // vertical total (lines per frame)
        public byte* Name;        // unused; declared for size
        public uint NameLength;
        public IntPtr ModeFlags;
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
