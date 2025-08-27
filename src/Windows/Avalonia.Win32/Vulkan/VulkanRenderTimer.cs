using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Logging;
using Avalonia.Rendering;
using Avalonia.Vulkan;

namespace Avalonia.Win32.Vulkan;

internal class VulkanRenderTimer : IRenderTimer
{
    private readonly object _syncLock = new();
    private Action? _waitForPresentFence;

    public event Action<TimeSpan>? Tick;
    public bool RunsInBackground => true;

    public VulkanRenderTimer()
    {
        Logger.TryGet(LogEventLevel.Debug, "VulkanDynamic")?.Log(this, "VulkanRenderTimer created with fence-based VSync");
        
        // Create a render loop thread that waits for presentation fences
        Thread thread = new(RenderLoop)
        {
            IsBackground = RunsInBackground,
            Name = "VulkanDynamicVSync",
        };
        thread.Start();
    }

    private void RenderLoop()
    {
        Logger.TryGet(LogEventLevel.Debug, "VulkanDynamic")?.Log(this, "VSync render loop started");
        Stopwatch sw = Stopwatch.StartNew();
        
        CancellationTokenSource cts = new();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            cts.Cancel();
        
        // Wait for Tick to be subscribed
        while(Tick == null)
            Thread.Sleep(1);
        
        // Bootstrap with initial tick to start rendering
        while (_waitForPresentFence == null)
        {
            Thread.Sleep(16);
            Tick?.Invoke(sw.Elapsed);
        }

        while (!cts.IsCancellationRequested)
        {
            if (_waitForPresentFence != null)
            {
                // Wait for the presentation fence to be signaled
                // This fence is signaled when GPU completes all presentation work
                lock (_syncLock)
                {
                    _waitForPresentFence();
                    _waitForPresentFence = null;
                }
            }
            else
            {
                //rest at 120hz support
                Thread.Sleep(8);
            }

            // Fire the render tick after presentation completes or rest
            Tick?.Invoke(sw.Elapsed);
        }
    }

    public void SetPresentFenceWaitAction(Action fenceWaitAction)
    {
        lock (_syncLock)
            _waitForPresentFence = fenceWaitAction;
        
        Logger.TryGet(LogEventLevel.Verbose, "VulkanDynamic")
            ?.Log(this, "Present fence wait action set for VSync synchronization");
    }
}
