using System;
using System.Collections.Generic;
using Avalonia.Vulkan.UnmanagedInterop;

namespace Avalonia.Vulkan.Interop;

internal class VulkanCommandBufferPool : IDisposable
{
    private readonly IVulkanPlatformGraphicsContext _context;
    private readonly bool _autoFree;
    private readonly Queue<VulkanCommandBuffer> _commandBuffers = new();
    private VkCommandPool _handle;
    public VkCommandPool Handle => _handle;

    // [POOL] Pool growth tracking. The hypothesis under test: this pool's queue grows
    // during pressure events (resize storms, many ImportedImage/Semaphore creations) and
    // never shrinks, so each subsequent CreateCommandBuffer pays an O(N) scan cost that
    // accumulates as O(N) vkGetFenceStatus calls per snapshot. Reports: pool size, peak
    // size, allocations (slow path), recycles (fast path), avg scan-depth-to-recycle.
    // One report per second per pool instance, tagged with a unique pool id so the
    // process-wide shared external-objects pool is distinguishable from per-display pools.
    private static int _poolIdCounter;
    private readonly int _poolId = System.Threading.Interlocked.Increment(ref _poolIdCounter);
    private long _poolPeakSize;
    private long _poolAllocs;            // slow path (vkAllocateCommandBuffers + vkCreateFence)
    private long _poolRecycles;          // fast path (vkResetCommandBuffer of a finished CB)
    private long _poolScanDepthSum;      // sum of i for recycle hits; / recycles = avg
    private long _poolLastReportTs;

    public VulkanCommandBufferPool(IVulkanPlatformGraphicsContext context, bool autoFree = false)
    {
        _context = context;
        _autoFree = autoFree;
        var createInfo = new VkCommandPoolCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
            flags = VkCommandPoolCreateFlags.VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
            queueFamilyIndex = context.GraphicsQueueFamilyIndex
        };
        _context.DeviceApi.CreateCommandPool(_context.DeviceHandle, ref createInfo, IntPtr.Zero, out _handle)
            .ThrowOnError("vkCreateCommandPool");
    }

    public void FreeUsedCommandBuffers()
    {
        while (_commandBuffers.Count > 0)
            _commandBuffers.Dequeue().Dispose();
    }
    
    public void FreeFinishedCommandBuffers()
    {
        while (_commandBuffers.Count > 0)
        {
            var next = _commandBuffers.Peek();
            if(!next.IsFinished)
                return;
            _commandBuffers.Dequeue();
            next.Dispose();
        }
    }

    public void Dispose()
    {
        FreeUsedCommandBuffers();
        
        if (_handle.Handle != 0)
            _context.DeviceApi.DestroyCommandPool(_context.DeviceHandle, _handle, IntPtr.Zero);
        _handle = default;
    }

    public unsafe VulkanCommandBuffer CreateCommandBuffer()
    {
        // NOTE: do NOT call FreeFinishedCommandBuffers here even in _autoFree mode.
        // FreeFinishedCommandBuffers DISPOSES finished CBs (vkFreeCommandBuffers +
        // vkDestroyFence) — which would run before the recycle scan below, draining
        // the queue and forcing a fresh vkAllocateCommandBuffers + vkCreateFence on
        // every call. That hits Mesa's driver-side allocator periodically (10-30ms
        // stalls). The recycle scan handles bounding the queue size by reusing
        // finished CBs, so the autoFree contract is satisfied without disposal churn.

        // Recycle a finished command buffer if any is available. Allocating a fresh
        // command buffer + fence per frame (vkAllocateCommandBuffers + vkCreateFence)
        // periodically stalls 10-22ms on Mesa as the driver maintains its internal
        // freelists. Reusing avoids both calls in steady state. The pool was created
        // with VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT so per-CB reset is legal.
        //
        // We must scan the WHOLE queue rather than just the head: when CBs are submitted
        // with wait-semaphore dependencies (e.g. SubmitSemaphore in
        // VulkanExternalObjectsFeature for SnapshotWithSemaphores), their fences only
        // signal AFTER the user-supplied semaphore signals. If those semaphores are
        // periodically late, the head can be unfinished while later CBs are done. Only
        // checking head would force allocation in that case, defeating the recycle.
        int count = _commandBuffers.Count;
        if (count > _poolPeakSize) _poolPeakSize = count;
        for (int i = 0; i < count; i++)
        {
            var cb = _commandBuffers.Dequeue();
            if (cb.IsFinished)
            {
                cb.Reset();
                _poolRecycles++;
                _poolScanDepthSum += i;
                PoolMaybeReport();
                return cb;
            }
            // Not finished yet; re-enqueue so we keep checking on subsequent calls.
            _commandBuffers.Enqueue(cb);
        }

        var commandBufferAllocateInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
            commandPool = _handle,
            commandBufferCount = 1,
            level = VkCommandBufferLevel.VK_COMMAND_BUFFER_LEVEL_PRIMARY
        };
        VkCommandBuffer bufferHandle = default;
        _context.DeviceApi.AllocateCommandBuffers(_context.DeviceHandle, ref commandBufferAllocateInfo,
            &bufferHandle).ThrowOnError("vkAllocateCommandBuffers");

        _poolAllocs++;
        PoolMaybeReport();
        return new VulkanCommandBuffer(this, bufferHandle, _context);
    }

    private void PoolMaybeReport()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_poolLastReportTs == 0) { _poolLastReportTs = now; return; }
        double tickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if ((now - _poolLastReportTs) * tickToMs < 1000.0) return;
        long total = _poolRecycles + _poolAllocs;
        double avgScan = _poolRecycles > 0 ? (double)_poolScanDepthSum / _poolRecycles : 0;
        System.Console.Error.WriteLine(
            $"[POOL#{_poolId}] size={_commandBuffers.Count} peak={_poolPeakSize} "
            + $"calls={total} recycles={_poolRecycles} allocs={_poolAllocs} avgScanToHit={avgScan:F1}");
        _poolRecycles = _poolAllocs = _poolScanDepthSum = 0;
        _poolLastReportTs = now;
    }

    public void AddSubmittedCommandBuffer(VulkanCommandBuffer buffer) => _commandBuffers.Enqueue(buffer);
}
