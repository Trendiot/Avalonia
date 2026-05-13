using System;
using System.Linq;
using System.Threading;
using Avalonia.Vulkan.UnmanagedInterop;
// ReSharper disable FieldCanBeMadeReadOnly.Local
// ReSharper disable IdentifierTypo
// ReSharper disable StringLiteralTypo

namespace Avalonia.Vulkan.Interop;

internal class VulkanDisplay : IDisposable
{
    private IVulkanPlatformGraphicsContext _context;

    // Acquire-semaphore pool, round-robin per frame. A single shared
    // ImageAvailableSemaphore (the original design) violates
    // VUID-vkAcquireNextImageKHR-semaphore-01779 under MAILBOX on Windows:
    // vkAcquireNextImageKHR can return before the previous frame's submit
    // has consumed the wait on the same semaphore, leaving an "uncompleted
    // signal/wait operation pending". FIFO masks this because vsync paces
    // acquires behind GPU completion.
    //
    // Reuse is gated on the CB submitted in this slot completing on the GPU
    // — that submit's wait on the slot's acquire semaphore finishing is a
    // prerequisite of submit completion, so the semaphore is guaranteed
    // free of pending operations. We deliberately do NOT keep a parallel
    // _acquireFences[] array (an earlier attempt did this): overriding the
    // CB's intrinsic fence with a per-slot fence broke
    // VulkanCommandBufferPool.IsFinished, which checks the CB's own fence
    // for recycle eligibility, and produced VUID-vkResetCommandBuffer-00045
    // / VUID-vkBeginCommandBuffer-00049 / VUID-vkQueueSubmit-00071 cascades
    // when the pool handed back CBs that were still in flight.
    private VulkanSemaphore[] _acquireSemaphores = Array.Empty<VulkanSemaphore>();
    private VulkanCommandBuffer?[] _slotInFlightCommandBuffers = Array.Empty<VulkanCommandBuffer?>();
    private int _acquireSlot;

    // Render-finished semaphores, indexed by acquired swapchain image.
    // vkQueuePresentKHR consumes its wait semaphore at submission time, and
    // the same image cannot be re-acquired until that is processed, so a
    // per-image render-finished semaphore is always free of pending signals
    // when its image is re-acquired.
    private VulkanSemaphore[] _renderFinishedSemaphores = Array.Empty<VulkanSemaphore>();

    private uint _nextImage;
    private VulkanKhrSurface? _surface;
    private VkSurfaceFormatKHR _surfaceFormat;
    private VkSwapchainKHR _swapchain;
    private VkExtent2D _swapchainExtent;
    private readonly IVulkanKhrSurfacePlatformSurface _platformSurface;
    private readonly bool _isDynamicMode;
    private VkImage[] _swapchainImages = Array.Empty<VkImage>();
    private VkImageView[] _swapchainImageViews = Array.Empty<VkImageView>();
    public VulkanCommandBufferPool CommandBufferPool { get; private set; }
    public PixelSize Size { get; private set; }
    private bool _swapchainOutOfDate;

    private VulkanDisplay(IVulkanPlatformGraphicsContext context, VulkanKhrSurface surface, VkSwapchainKHR swapchain,
        VkExtent2D swapchainExtent, IVulkanKhrSurfacePlatformSurface platformSurface, bool isDynamicMode)
    {
        _context = context;
        _surface = surface;
        _swapchain = swapchain;
        _swapchainExtent = swapchainExtent;
        _platformSurface = platformSurface;
        _isDynamicMode = isDynamicMode;
        CommandBufferPool = new VulkanCommandBufferPool(_context);
        CreateSwapchainImages();
        EnsureSyncPrimitives();
    }

    internal VkSurfaceFormatKHR SurfaceFormat
    {
        get
        {
            if (_surfaceFormat.format == VkFormat.VK_FORMAT_UNDEFINED && _surface != null)
                _surfaceFormat = _surface.GetSurfaceFormat();
            return _surfaceFormat;
        }
    }

    private static unsafe VkSwapchainKHR CreateSwapchain(IVulkanPlatformGraphicsContext context,
        VulkanKhrSurface surface, out VkExtent2D swapchainExtent, VulkanDisplay? oldDisplay = null)
    {
        while (!surface.CanSurfacePresent())
            Thread.Sleep(16);
        context.InstanceApi.GetPhysicalDeviceSurfaceCapabilitiesKHR(context.PhysicalDeviceHandle,
                surface.Handle, out var capabilities)
            .ThrowOnError("vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        uint presentModesCount = 0;
        context.InstanceApi.GetPhysicalDeviceSurfacePresentModesKHR(context.PhysicalDeviceHandle,
                surface.Handle, ref presentModesCount, null)
            .ThrowOnError("vkGetPhysicalDeviceSurfacePresentModesKHR");

        var modes = new VkPresentModeKHR[(int)presentModesCount];
        fixed (VkPresentModeKHR* pModes = modes)
            context.InstanceApi.GetPhysicalDeviceSurfacePresentModesKHR(context.PhysicalDeviceHandle,
                    surface.Handle, ref presentModesCount, pModes)
                .ThrowOnError("vkGetPhysicalDeviceSurfacePresentModesKHR");
        
        var imageCount = capabilities.minImageCount + 1;
        if (capabilities.maxImageCount > 0 && imageCount > capabilities.maxImageCount)
            imageCount = capabilities.maxImageCount;
        
        var surfaceFormat = surface.GetSurfaceFormat();

        bool supportsIdentityTransform = capabilities.supportedTransforms.HasAllFlags(
            VkSurfaceTransformFlagsKHR.VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR);

        bool isRotated =
            capabilities.currentTransform.HasAllFlags(VkSurfaceTransformFlagsKHR.VK_SURFACE_TRANSFORM_ROTATE_90_BIT_KHR)
            || capabilities.currentTransform.HasAllFlags(VkSurfaceTransformFlagsKHR
                .VK_SURFACE_TRANSFORM_ROTATE_270_BIT_KHR);

        if (capabilities.currentExtent.width != uint.MaxValue) 
            swapchainExtent = capabilities.currentExtent;
        else
        {
            var surfaceSize = surface.Size;

            var width = Math.Max(capabilities.minImageExtent.width,
                Math.Min(capabilities.maxImageExtent.width, (uint)surfaceSize.Width));
            var height = Math.Max(capabilities.minImageExtent.height,
                Math.Min(capabilities.maxImageExtent.height, (uint)surfaceSize.Height));

            swapchainExtent = new VkExtent2D
            {
                width = width,
                height = height
            };
        }
        // Present mode selection with priority for VSync modes
        VkPresentModeKHR presentMode;
        if (modes.Contains(VkPresentModeKHR.VK_PRESENT_MODE_MAILBOX_KHR))
        { 
            // Best: Triple buffering with VSync - low latency, no tearing
            presentMode = VkPresentModeKHR.VK_PRESENT_MODE_MAILBOX_KHR;
        }
        else if (modes.Contains(VkPresentModeKHR.VK_PRESENT_MODE_IMMEDIATE_KHR))
        {
            // Fallback: Immediate mode (allows tearing) - only if nothing else available
            presentMode = VkPresentModeKHR.VK_PRESENT_MODE_IMMEDIATE_KHR;
        }
        else if (modes.Contains(VkPresentModeKHR.VK_PRESENT_MODE_FIFO_RELAXED_KHR))
        {
            // Good: Adaptive VSync - tears only when frame rate drops
            presentMode = VkPresentModeKHR.VK_PRESENT_MODE_FIFO_RELAXED_KHR;
        }
        else if (modes.Contains(VkPresentModeKHR.VK_PRESENT_MODE_FIFO_KHR))
        {
            // Standard: Traditional VSync - guaranteed to be available
            presentMode = VkPresentModeKHR.VK_PRESENT_MODE_FIFO_KHR;
        }
        else
            throw new InvalidOperationException("No suitable present mode found");
       

        var swapchainCreateInfo = new VkSwapchainCreateInfoKHR
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR,
            surface = surface.Handle,
            minImageCount = imageCount,
            imageFormat = surfaceFormat.format,
            imageColorSpace = surfaceFormat.colorSpace,
            imageExtent = swapchainExtent,
            imageUsage = VkImageUsageFlags.VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT |
                         VkImageUsageFlags.VK_IMAGE_USAGE_TRANSFER_DST_BIT,
            imageSharingMode = VkSharingMode.VK_SHARING_MODE_EXCLUSIVE,
            imageArrayLayers = 1,
            preTransform = supportsIdentityTransform && isRotated
                ? VkSurfaceTransformFlagsKHR.VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR
                : capabilities.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR,
            presentMode = presentMode,
            clipped = 1,
            oldSwapchain = oldDisplay?._swapchain ?? default
        };
        context.DeviceApi.CreateSwapchainKHR(context.DeviceHandle, ref swapchainCreateInfo, IntPtr.Zero,
            out var swapchain).ThrowOnError("vkCreateSwapchainKHR");
        oldDisplay?.DestroySwapchain();
        return swapchain;
    }

    private void DestroySwapchain()
    {
        if(_swapchain.Handle != 0)
            _context.DeviceApi.DestroySwapchainKHR(_context.DeviceHandle, _swapchain, IntPtr.Zero);
        _swapchain = default;
    }

    internal static VulkanDisplay CreateDisplay(IVulkanPlatformGraphicsContext context, IVulkanKhrSurfacePlatformSurface surface, bool isDynamicMode = false)
    {
        var khrSurface = new VulkanKhrSurface(context, surface);
        var swapchain = CreateSwapchain(context, khrSurface, out var extent);
        return new VulkanDisplay(context, khrSurface, swapchain, extent, surface, isDynamicMode);
    }

    private void DestroyCurrentImageViews()
    {
        if (_swapchainImageViews.Length <= 0)
            return;
        foreach (var imageView in _swapchainImageViews)
            _context.DeviceApi.DestroyImageView(_context.DeviceHandle, imageView, IntPtr.Zero);

        _swapchainImageViews = Array.Empty<VkImageView>();

    }

    private unsafe void CreateSwapchainImages()
    {
        DestroyCurrentImageViews();
        Size = new PixelSize((int)_swapchainExtent.width, (int)_swapchainExtent.height);
        uint imageCount = 0;
        _context.DeviceApi.GetSwapchainImagesKHR(_context.DeviceHandle, _swapchain, ref imageCount, null)
            .ThrowOnError("vkGetSwapchainImagesKHR");
        _swapchainImages = new VkImage[imageCount];
        fixed (VkImage* pImages = _swapchainImages)
            _context.DeviceApi.GetSwapchainImagesKHR(_context.DeviceHandle, _swapchain, ref imageCount,
                pImages).ThrowOnError("vkGetSwapchainImagesKHR");
        _swapchainImageViews = new VkImageView[imageCount];
        for (var c = 0; c < imageCount; c++)
            _swapchainImageViews[c] = CreateSwapchainImageView(_swapchainImages[c], SurfaceFormat.format);
    }

    // (Re)builds the acquire-semaphore pool + render-finished semaphore array
    // to match the current swapchain. Acquire pool size is imageCount + 1 so
    // there is always at least one slot whose tracked CB has long since
    // finished, keeping the steady-state reuse wait non-blocking while
    // strictly preventing 01779.
    private void EnsureSyncPrimitives()
    {
        int imageCount = _swapchainImages.Length;

        if (_renderFinishedSemaphores.Length != imageCount)
        {
            DestroyRenderFinishedSemaphores();
            _renderFinishedSemaphores = new VulkanSemaphore[imageCount];
            for (int i = 0; i < imageCount; i++)
                _renderFinishedSemaphores[i] = new VulkanSemaphore(_context);
        }

        int targetAcquirePoolSize = imageCount + 1;
        if (_acquireSemaphores.Length != targetAcquirePoolSize)
        {
            DestroyAcquirePool();
            _acquireSemaphores = new VulkanSemaphore[targetAcquirePoolSize];
            _slotInFlightCommandBuffers = new VulkanCommandBuffer?[targetAcquirePoolSize];
            for (int i = 0; i < targetAcquirePoolSize; i++)
                _acquireSemaphores[i] = new VulkanSemaphore(_context);
            _acquireSlot = 0;
        }
    }

    private void DestroyRenderFinishedSemaphores()
    {
        for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
            _renderFinishedSemaphores[i]?.Dispose();
        _renderFinishedSemaphores = Array.Empty<VulkanSemaphore>();
    }

    private void DestroyAcquirePool()
    {
        for (int i = 0; i < _acquireSemaphores.Length; i++)
            _acquireSemaphores[i]?.Dispose();
        _acquireSemaphores = Array.Empty<VulkanSemaphore>();

        // CBs themselves are owned by the pool; we only drop our slot
        // tracking refs here.
        _slotInFlightCommandBuffers = Array.Empty<VulkanCommandBuffer?>();
    }

    private VkImageView CreateSwapchainImageView(VkImage swapchainImage, VkFormat format)
    {
        var imageViewCreateInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
            subresourceRange =
            {
                aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                levelCount = 1,
                layerCount = 1
            },
            format = format,
            image = swapchainImage,
            viewType = VkImageViewType.VK_IMAGE_VIEW_TYPE_2D,
        };
        _context.DeviceApi.CreateImageView(_context.DeviceHandle, ref imageViewCreateInfo,
            IntPtr.Zero, out var imageView).ThrowOnError("vkCreateImageView");
        return imageView;
    }
    
    private void RecreateSwapchain()
    {
        if (_surface == null)
        {
            RecreateSurface();
            return;
        }
        _context.DeviceApi.DeviceWaitIdle(_context.DeviceHandle);
        _swapchain = CreateSwapchain(_context, _surface, out var extent, this);
        _swapchainExtent = extent;
        CreateSwapchainImages();
        EnsureSyncPrimitives();
    }

    private void RecreateSurface()
    {
        _surface?.Dispose();
        _surface = null;
        _surface = new VulkanKhrSurface(_context, _platformSurface);
        DestroySwapchain();
        RecreateSwapchain();
    }
    
    public bool EnsureSwapchainAvailable()
    {
        // Check if swapchain was marked as out of date from a previous presentation
        if (_swapchainOutOfDate)
        {
            RecreateSwapchain();
            _swapchainOutOfDate = false;
            return true;
        }
        
        // Check if surface size has changed
        if (Size != _surface?.Size)
        {
            RecreateSwapchain();
            return true;
        }
        return false;
    }

    public VulkanCommandBuffer StartPresentation()
    {
        _nextImage = 0;
        while (true)
        {
            // Pick the next acquire slot and wait until the CB previously
            // submitted on this slot has fully completed on the GPU. The
            // CB's fence signaling implies its wait on this slot's acquire
            // semaphore was processed, so the semaphore is free of pending
            // operations. First use per slot has no tracked CB and skips
            // the wait. Steady state: imageCount + 1 slots means by the
            // time we wrap back, the tracked CB has long since finished
            // and the wait is non-blocking.
            _acquireSlot = (_acquireSlot + 1) % _acquireSemaphores.Length;
            _slotInFlightCommandBuffers[_acquireSlot]?.WaitForCompletion();

            var acquireResult = _context.DeviceApi.AcquireNextImageKHR(
                _context.DeviceHandle,
                _swapchain,
                ulong.MaxValue,
                _acquireSemaphores[_acquireSlot].Handle,
                default, out _nextImage);
            if (acquireResult is VkResult.VK_ERROR_OUT_OF_DATE_KHR or VkResult.VK_SUBOPTIMAL_KHR)
                RecreateSwapchain();
            else if (acquireResult is VkResult.VK_ERROR_SURFACE_LOST_KHR)
                RecreateSurface();
            else
            {
                acquireResult.ThrowOnError("vkAcquireNextImageKHR");
                break;
            }
        }

        var commandBuffer = CommandBufferPool.CreateCommandBuffer();
        commandBuffer.BeginRecording();
        VulkanMemoryHelper.TransitionLayout(_context, commandBuffer,
            _swapchainImages[_nextImage], VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED,
            VkAccessFlags.VK_ACCESS_NONE, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT, 1);
        return commandBuffer;
    }

    internal unsafe void BlitImageToCurrentImage(VulkanCommandBuffer commandBuffer, VulkanImage image)
    {
        VulkanMemoryHelper.TransitionLayout(_context, commandBuffer,
            image.Handle, image.CurrentLayout, VkAccessFlags.VK_ACCESS_NONE,
            VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
            VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
            image.MipLevels);

        var srcBlitRegion = new VkImageBlit
        {
            srcOffsets2 =
            {
                x = image.Size.Width,
                y = image.Size.Height,
                z = 1
            },
            dstOffsets2 =
            {
                x = Size.Width,
                y = Size.Height,
                z = 1
            },
            srcSubresource =
            {
                aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                layerCount = 1
            },
            dstSubresource =
            {
                aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                layerCount = 1
            }
        };
        
        _context.DeviceApi.CmdBlitImage(commandBuffer.Handle, image.Handle,
            VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
            _swapchainImages[_nextImage],
            VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            1, &srcBlitRegion, VkFilter.VK_FILTER_LINEAR);

        VulkanMemoryHelper.TransitionLayout(_context, commandBuffer,
            image.Handle, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
            VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
            image.CurrentLayout, VkAccessFlags.VK_ACCESS_NONE, image.MipLevels);
    }

    internal unsafe void EndPresentation(VulkanCommandBuffer commandBuffer)
    {
        VulkanMemoryHelper.TransitionLayout(_context, commandBuffer,
            _swapchainImages[_nextImage],
            VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            VkAccessFlags.VK_ACCESS_NONE,
            VkImageLayout.VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
            VkAccessFlags.VK_ACCESS_NONE,
            1);

        // Submit waits on the slot's acquire semaphore and signals this
        // image's render-finished semaphore. We pass NO fence override so
        // VulkanCommandBuffer.Submit attaches the CB's intrinsic _fence —
        // that keeps VulkanCommandBufferPool.IsFinished accurate (it reads
        // _fence.IsSignaled to decide if a CB is reusable) AND gives us a
        // single fence we can wait on for slot-reuse gating below.
        int submittedSlot = _acquireSlot;
        commandBuffer.Submit(
            new[] { _acquireSemaphores[submittedSlot] },
            new[] { VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT },
            new[] { _renderFinishedSemaphores[_nextImage] });
        _slotInFlightCommandBuffers[submittedSlot] = commandBuffer;

        var semaphore = _renderFinishedSemaphores[_nextImage].Handle;
        var swapchain = _swapchain;
        var nextImage = _nextImage;

        VkResult result;
        var presentInfo = new VkPresentInfoKHR
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
            waitSemaphoreCount = 1,
            pWaitSemaphores = &semaphore,
            swapchainCount = 1,
            pSwapchains = &swapchain,
            pImageIndices = &nextImage,
            pResults = &result
        };

        var presentResult = _context.DeviceApi.vkQueuePresentKHR(_context.MainQueueHandle, ref presentInfo);

        // Handle VK_ERROR_OUT_OF_DATE_KHR by recreating the swapchain
        // This can happen if the window is resized between acquire and present
        if (presentResult == VkResult.VK_ERROR_OUT_OF_DATE_KHR)
        {
            // The swapchain is no longer valid. We need to recreate it.
            // The current frame cannot be presented, so we just recreate and return.
            // The next BeginDraw/EndPresentation cycle will use the new swapchain.
            _context.DeviceApi.DeviceWaitIdle(_context.DeviceHandle);
            RecreateSwapchain();
            // Mark that the swapchain was recreated so the next BeginDraw knows to recreate the image
            _swapchainOutOfDate = true;
            return;
        }

        // Handle VK_SUBOPTIMAL_KHR - presentation succeeded but surface is suboptimal
        if (presentResult != VkResult.VK_SUCCESS && presentResult != VkResult.VK_SUBOPTIMAL_KHR)
        {
            presentResult.ThrowOnError("vkQueuePresentKHR");
        }

        // Also check the result array for errors
        if (result != VkResult.VK_SUCCESS && result != VkResult.VK_SUBOPTIMAL_KHR)
        {
            result.ThrowOnError("vkQueuePresentKHR");
        }

        // VulkanDynamic vsync: hand the just-submitted CB's completion to
        // the render timer so it waits for this frame's GPU work before
        // pacing the next tick. Capture the CB by reference; even if the
        // pool later recycles it for a NEW submit, waiting on its fence
        // gives a strictly stronger guarantee than this frame's completion
        // (the pool only recycles CBs whose previous work already finished).
        if (_isDynamicMode && _context is VulkanContext vulkanContext)
        {
            var waitCb = commandBuffer;
            vulkanContext.SetPresentFence(() => {
                waitCb.WaitForCompletion(100_000_000); // 100ms timeout
            });
        }
    }
    
    public void Dispose()
    {
        _context.DeviceApi.DeviceWaitIdle(_context.DeviceHandle);
        DestroyAcquirePool();
        DestroyRenderFinishedSemaphores();
        DestroyCurrentImageViews();
        DestroySwapchain();
        CommandBufferPool?.Dispose();
        CommandBufferPool = null!;
        _surface?.Dispose();
        _surface = null!;
    }

}
