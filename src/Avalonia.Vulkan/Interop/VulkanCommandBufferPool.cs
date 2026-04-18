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
        if (_autoFree)
            FreeFinishedCommandBuffers();

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
        for (int i = 0; i < count; i++)
        {
            var cb = _commandBuffers.Dequeue();
            if (cb.IsFinished)
            {
                cb.Reset();
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

        return new VulkanCommandBuffer(this, bufferHandle, _context);
    }
    
    public void AddSubmittedCommandBuffer(VulkanCommandBuffer buffer) => _commandBuffers.Enqueue(buffer);
}