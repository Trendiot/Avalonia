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
            _tick?.Invoke(sw.Elapsed);
        }
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
