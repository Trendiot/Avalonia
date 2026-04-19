using System;
using System.Diagnostics;
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
                // Block until SetPresentFenceWaitAction signals a new fence, with a safety
                // timeout so an idle UI (no renders being produced) still probes periodically.
                // Using a fixed Sleep here would drift relative to vsync and cause periodic
                // single-frame skips at high refresh rates.
                // *** TEMPORARY DIAGNOSTIC: 1ms timeout instead of 16ms to test whether
                // the safety-timeout path is contributing to PhotonPlot's 32ms outliers
                // (which match exactly 2 x 16ms). If outliers shift to ~2ms, this is the
                // cause. Revert to 16 (or remove timeout) once verified.
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
        // Wake the render loop immediately so it picks up the new fence without
        // waiting on the safety timeout in the no-fence branch.
        _wakeEvent.Set();
        Logger.TryGet(LogEventLevel.Verbose, "VulkanDynamic")
            ?.Log(this, "Present fence wait action set for VSync synchronization");
    }
}
