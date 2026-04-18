using System;
using System.Diagnostics;
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
    private VulkanSemaphorePair _semaphorePair;
    private uint _nextImage;
    private VulkanKhrSurface? _surface;
    private VkSurfaceFormatKHR _surfaceFormat;
    private VkSwapchainKHR _swapchain;
    private VkExtent2D _swapchainExtent;
    private readonly IVulkanKhrSurfacePlatformSurface _platformSurface;
    private VkImage[] _swapchainImages = Array.Empty<VkImage>();
    private VkImageView[] _swapchainImageViews = Array.Empty<VkImageView>();
    public VulkanCommandBufferPool CommandBufferPool { get; private set; }
    public PixelSize Size { get; private set; }
    private VulkanFence? _presentFence;
    private bool _swapchainOutOfDate;

    // --- GPU-timeline jitter diagnostic ---------------------------------------------------
    // Each frame writes two GPU timestamps (top-of-pipe before blit, bottom-of-pipe after
    // blit) into a ring of VkQueryPool slots. Each frame also records CPU timestamps
    // around vkQueueSubmit and vkQueuePresentKHR. After the next frame's GPU work has had
    // time to complete (we read back DiagRingDepth-1 frames behind), we resolve the GPU
    // timestamps and combine with the CPU samples for that same frame to produce a
    // per-frame breakdown:
    //   gpu_work    = (gpu_end_tick - gpu_start_tick) * timestampPeriod
    //                 -> the actual time the GPU spent executing OUR command buffer.
    //                    A periodic increase here proves cross-process GPU contention.
    //   submit_cpu  = how long vkQueueSubmit blocked on the CPU
    //                 -> non-zero means driver-side queue contention.
    //   present_cpu = how long vkQueuePresentKHR blocked on the CPU
    //                 -> non-zero under FIFO_KHR means WSI back-pressure surfaced here
    //                    (rare; usually back-pressure surfaces in vkAcquireNextImageKHR).
    //   wall_total  = wall-clock interval between successive present_returned timestamps
    //                 -> the visible frame interval.
    // The "missing" time (wall_total - gpu_work - submit_cpu - present_cpu) is everything
    // upstream (Avalonia compositor, Skia draw, render scheduler).
    private const int    DiagRingDepth        = 8;          // frames of history kept
    private const double DiagJitterThresholdMs = 9.0;
    private const int    DiagWorstPerWindow    = 5;
    private VkQueryPool _diagQueryPool;
    private float       _diagTimestampPeriodNs;             // ns per GPU tick (from physical device limits)
    private uint        _diagTimestampValidBits;            // 0 == no valid bits => disabled
    private long        _diagFrameCount;          // monotonic, never reset; used as ring index
    private long        _diagFramesInWindow;      // reset each 1s report window
    private long        _diagJitterCount;
    private long        _diagLastReportTs;
    private long        _diagPrevReportFrameTs;
    // Per-slot CPU samples captured at submit/present time, indexed by frameNum % DiagRingDepth.
    private readonly long[]   _diagCpuSubmitCallTs    = new long[DiagRingDepth];
    private readonly long[]   _diagCpuSubmitDoneTs    = new long[DiagRingDepth];
    private readonly long[]   _diagCpuPresentCallTs   = new long[DiagRingDepth];
    private readonly long[]   _diagCpuPresentDoneTs   = new long[DiagRingDepth];
    private readonly long[]   _diagFrameNos           = new long[DiagRingDepth];
    private readonly bool[]   _diagSlotPopulated      = new bool[DiagRingDepth];
    // Worst-N tracker for the 1s window; kept short to bound console I/O.
    private readonly double[] _diagWorstWall          = new double[DiagWorstPerWindow];
    private readonly double[] _diagWorstGpu           = new double[DiagWorstPerWindow];
    private readonly double[] _diagWorstSubmitCpu     = new double[DiagWorstPerWindow];
    private readonly double[] _diagWorstPresentCpu    = new double[DiagWorstPerWindow];
    private readonly double[] _diagWorstUpstream      = new double[DiagWorstPerWindow];
    private readonly long  [] _diagWorstFrameNo       = new long  [DiagWorstPerWindow];

    private VulkanDisplay(IVulkanPlatformGraphicsContext context, VulkanKhrSurface surface, VkSwapchainKHR swapchain,
        VkExtent2D swapchainExtent, IVulkanKhrSurfacePlatformSurface platformSurface, bool isDynamicMode)
    {
        _context = context;
        _surface = surface;
        _swapchain = swapchain;
        _swapchainExtent = swapchainExtent;
        _platformSurface = platformSurface;
        _semaphorePair = new VulkanSemaphorePair(_context);
        CommandBufferPool = new VulkanCommandBufferPool(_context);
        CreateSwapchainImages();

        // Create presentation fence for VSync synchronization in dynamic mode
        if (isDynamicMode)
        {
            _presentFence = new VulkanFence(_context, VkFenceCreateFlags.VK_FENCE_CREATE_SIGNALED_BIT);
        }

        InitializeGpuTimingDiagnostic();
    }

    private void InitializeGpuTimingDiagnostic()
    {
        // Read timestampPeriod (ns/tick) and the valid-bit count for the graphics queue
        // family. If validBits == 0, GPU timestamps aren't supported on this queue and we
        // disable the diagnostic.
        _context.InstanceApi.GetPhysicalDeviceProperties(_context.PhysicalDeviceHandle, out var props);
        _diagTimestampPeriodNs = props.limits.timestampPeriod;
        _diagTimestampValidBits = props.limits.timestampComputeAndGraphics != 0 ? 64u : 0u;
        if (_diagTimestampValidBits == 0)
        {
            Console.Error.WriteLine("[GPU] Timestamps unsupported on graphics queue (timestampComputeAndGraphics=0); GPU diagnostic disabled.");
            return;
        }

        var qpInfo = new VkQueryPoolCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO,
            queryType = VkQueryType.VK_QUERY_TYPE_TIMESTAMP,
            queryCount = (uint)(DiagRingDepth * 2),
        };
        _context.DeviceApi.CreateQueryPool(_context.DeviceHandle, ref qpInfo, IntPtr.Zero, out _diagQueryPool)
            .ThrowOnError("vkCreateQueryPool");
        Console.Error.WriteLine(
            $"[GPU] Diagnostic enabled. timestampPeriod={_diagTimestampPeriodNs:F2}ns/tick, ringDepth={DiagRingDepth}");
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
        {
            // Fallback: Immediate mode (allows tearing) - only if nothing else available
            presentMode = VkPresentModeKHR.VK_PRESENT_MODE_IMMEDIATE_KHR;
        }

        // Print which present mode the WSI actually accepted, and the full set advertised.
        // Mesa's MESA_VK_WSI_PRESENT_MODE env var works by limiting which modes are
        // advertised here, so this line is the unequivocal proof of what's in effect.
        Console.Error.WriteLine(
            $"[GPU] Present mode SELECTED: {presentMode}; AVAILABLE: [{string.Join(",", modes)}]; imageCount={imageCount}");

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
            var acquireResult = _context.DeviceApi.AcquireNextImageKHR(
                _context.DeviceHandle,
                _swapchain,
                ulong.MaxValue,
                _semaphorePair.ImageAvailableSemaphore.Handle,
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
        // GPU timing: top-of-pipe before any work for this frame is recorded.
        if (_diagTimestampValidBits != 0)
        {
            uint slot = (uint)((_diagFrameCount % DiagRingDepth) * 2);
            _context.DeviceApi.CmdResetQueryPool(commandBuffer.Handle, _diagQueryPool, slot, 2);
            _context.DeviceApi.CmdWriteTimestamp(commandBuffer.Handle,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, _diagQueryPool, slot);
        }

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

        // GPU timing: bottom-of-pipe after all work for this frame is recorded.
        if (_diagTimestampValidBits != 0)
        {
            uint slot = (uint)((_diagFrameCount % DiagRingDepth) * 2);
            _context.DeviceApi.CmdWriteTimestamp(commandBuffer.Handle,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, _diagQueryPool, slot + 1);
        }
        
        // Submit with presentation fence for VSync synchronization in dynamic mode
        if (_presentFence.HasValue)
        {
            // Reset the fence before submitting (step 2 from reference)
            VkFence fence = _presentFence.Value.Handle;
            _context.DeviceApi.ResetFences(_context.DeviceHandle, 1, &fence)
                .ThrowOnError("vkResetFences");
        }

        long diagSubmitCallTs = Stopwatch.GetTimestamp();
        commandBuffer.Submit(new[] { _semaphorePair.ImageAvailableSemaphore },
            new[] { VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT },
            new[] { _semaphorePair.RenderFinishedSemaphore },
            _presentFence);
        long diagSubmitDoneTs = Stopwatch.GetTimestamp();

        var semaphore = _semaphorePair.RenderFinishedSemaphore.Handle;
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

        long diagPresentCallTs = Stopwatch.GetTimestamp();
        var presentResult = _context.DeviceApi.vkQueuePresentKHR(_context.MainQueueHandle, ref presentInfo);
        long diagPresentDoneTs = Stopwatch.GetTimestamp();

        // Stash CPU samples for THIS frame; readback of GPU timestamps for an OLDER frame
        // happens at the bottom of EndPresentation.
        if (_diagTimestampValidBits != 0)
        {
            int slot = (int)(_diagFrameCount % DiagRingDepth);
            _diagCpuSubmitCallTs [slot] = diagSubmitCallTs;
            _diagCpuSubmitDoneTs [slot] = diagSubmitDoneTs;
            _diagCpuPresentCallTs[slot] = diagPresentCallTs;
            _diagCpuPresentDoneTs[slot] = diagPresentDoneTs;
            _diagFrameNos        [slot] = _diagFrameCount;
            _diagSlotPopulated   [slot] = true;
        }
        
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
            // Diagnostic: invalidate this slot so the readback skips it; advance counter
            // to keep the ring index consistent with future frames.
            if (_diagTimestampValidBits != 0)
            {
                int slot = (int)(_diagFrameCount % DiagRingDepth);
                _diagSlotPopulated[slot] = false;
                _diagFrameCount++;
            }
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
        
        // For VulkanDynamic: pass the fence wait action to render timer
        // The fence was already submitted with the main command buffer above
        if (_presentFence.HasValue && _context is VulkanContext vulkanContext)
        {
            // Pass the fence wait action to render timer for VSync synchronization.
            //
            // Do NOT take Device.Lock here. The wait is naturally sequenced before the
            // next EndPresentation by the timer's tick chain: the timer only ticks AFTER
            // this wait completes, and the next vkResetFences/vkQueueSubmit on this fence
            // can only run after that tick is dispatched and the next render lands. So no
            // concurrent fence operation can race with this wait under the existing
            // single-window control flow. Holding Device.Lock during a wait that can take
            // 10-37ms (GPU paced behind by FIFO) blocks the render thread's next
            // BeginDraw on Device.Lock, which is the dominant per-frame jitter source at
            // high refresh rates. Each fence is per-VulkanDisplay so multi-window apps
            // remain safe (distinct fence objects do not require cross-fence sync).
            vulkanContext.SetPresentFence(() => {
                _presentFence.Value.Wait(100_000_000); // 100ms timeout
            });
        }

        if (_diagTimestampValidBits != 0)
        {
            ReadbackAndReportPriorFrame();
            _diagFrameCount++;
        }
    }

    private unsafe void ReadbackAndReportPriorFrame()
    {
        // Read back the GPU timestamps for the frame DiagRingDepth-1 frames ago. By that
        // point the GPU has had ample time to retire that frame's command buffer (with
        // FIFO + 3 swapchain images, GPU is at most 3 frames behind, so 7 frames of
        // history is more than enough headroom).
        long targetFrame = _diagFrameCount - (DiagRingDepth - 1);
        if (targetFrame < 0) return;
        int slot = (int)(targetFrame % DiagRingDepth);
        if (!_diagSlotPopulated[slot] || _diagFrameNos[slot] != targetFrame) return;

        // Each slot has 2 timestamps: [start, end]. Read both with availability bit so
        // we can skip cleanly if (somehow) the GPU hasn't retired yet.
        // Layout per query (stride = 16 bytes): [value:u64, availability:u64]
        ulong* data = stackalloc ulong[4]; // [start, startAvail, end, endAvail]
        uint queryStart = (uint)(slot * 2);
        VkResult res = _context.DeviceApi.GetQueryPoolResults(
            _context.DeviceHandle, _diagQueryPool, queryStart, 2,
            new IntPtr(sizeof(ulong) * 4), data, sizeof(ulong) * 2,
            VkQueryResultFlags.VK_QUERY_RESULT_64_BIT
            | VkQueryResultFlags.VK_QUERY_RESULT_WITH_AVAILABILITY_BIT);
        if (res != VkResult.VK_SUCCESS && res != VkResult.VK_NOT_READY) return;
        if (data[1] == 0 || data[3] == 0) return; // not yet available

        ulong gpuStartTick = data[0];
        ulong gpuEndTick   = data[2];
        // Mask to validBits and handle wrap (rare but possible if hardware uses < 64 bits)
        if (_diagTimestampValidBits < 64)
        {
            ulong mask = (1UL << (int)_diagTimestampValidBits) - 1;
            gpuStartTick &= mask;
            gpuEndTick   &= mask;
        }
        ulong gpuTicks = gpuEndTick >= gpuStartTick
            ? gpuEndTick - gpuStartTick
            : gpuEndTick + (1UL << (int)_diagTimestampValidBits) - gpuStartTick;
        double gpuWorkMs = gpuTicks * _diagTimestampPeriodNs / 1_000_000.0;

        double tickToMs   = 1000.0 / Stopwatch.Frequency;
        double submitCpuMs  = (_diagCpuSubmitDoneTs [slot] - _diagCpuSubmitCallTs [slot]) * tickToMs;
        double presentCpuMs = (_diagCpuPresentDoneTs[slot] - _diagCpuPresentCallTs[slot]) * tickToMs;
        long   presentDoneTs = _diagCpuPresentDoneTs[slot];

        // Wall-clock interval is the time between this frame's present-returned and the
        // previous one's. The "upstream" portion is the residual after subtracting
        // everything we measured locally; this is the time spent in
        // Avalonia/Skia/scheduler/etc. between presents.
        double wallTotalMs = 0;
        double upstreamMs  = 0;
        if (_diagPrevReportFrameTs != 0)
        {
            wallTotalMs = (presentDoneTs - _diagPrevReportFrameTs) * tickToMs;
            upstreamMs  = wallTotalMs - gpuWorkMs - submitCpuMs - presentCpuMs;
            if (upstreamMs < 0) upstreamMs = 0; // GPU work overlapped previous-frame CPU; rounding artifact
        }
        _diagPrevReportFrameTs = presentDoneTs;

        if (wallTotalMs > DiagJitterThresholdMs)
        {
            _diagJitterCount++;
            int worstIdx = 0;
            for (int i = 1; i < DiagWorstPerWindow; i++)
                if (_diagWorstWall[i] < _diagWorstWall[worstIdx]) worstIdx = i;
            if (wallTotalMs > _diagWorstWall[worstIdx])
            {
                _diagWorstWall      [worstIdx] = wallTotalMs;
                _diagWorstGpu       [worstIdx] = gpuWorkMs;
                _diagWorstSubmitCpu [worstIdx] = submitCpuMs;
                _diagWorstPresentCpu[worstIdx] = presentCpuMs;
                _diagWorstUpstream  [worstIdx] = upstreamMs;
                _diagWorstFrameNo   [worstIdx] = targetFrame;
            }
        }

        _diagFramesInWindow++;
        long now = Stopwatch.GetTimestamp();
        if (_diagLastReportTs == 0) _diagLastReportTs = now;
        double sinceReportMs = (now - _diagLastReportTs) * tickToMs;
        if (sinceReportMs >= 1000.0)
        {
            Console.Error.WriteLine(
                $"[GPU] 1s: frames={_diagFramesInWindow} jitter={_diagJitterCount} "
                + $"avgFps={(_diagFramesInWindow * 1000.0 / sinceReportMs):F1}");
            for (int i = 0; i < DiagWorstPerWindow; i++)
            {
                if (_diagWorstWall[i] <= 0) continue;
                Console.Error.WriteLine(
                    $"  worst#{i} f={_diagWorstFrameNo[i]} wall={_diagWorstWall[i]:F2}ms "
                    + $"gpu_work={_diagWorstGpu[i]:F2} submit_cpu={_diagWorstSubmitCpu[i]:F2} "
                    + $"present_cpu={_diagWorstPresentCpu[i]:F2} upstream={_diagWorstUpstream[i]:F2}");
                _diagWorstWall[i] = 0; _diagWorstGpu[i] = 0;
                _diagWorstSubmitCpu[i] = 0; _diagWorstPresentCpu[i] = 0;
                _diagWorstUpstream[i] = 0; _diagWorstFrameNo[i] = 0;
            }
            _diagFramesInWindow = 0;
            _diagJitterCount = 0;
            _diagLastReportTs = now;
        }
    }

    public void Dispose()
    {
        _context.DeviceApi.DeviceWaitIdle(_context.DeviceHandle);
        _presentFence?.Dispose();
        _semaphorePair?.Dispose();
        DestroyCurrentImageViews();
        DestroySwapchain();
        CommandBufferPool?.Dispose();
        CommandBufferPool = null!;
        _surface?.Dispose();
        _surface = null!;
        if (_diagQueryPool.Handle != 0)
        {
            _context.DeviceApi.DestroyQueryPool(_context.DeviceHandle, _diagQueryPool, IntPtr.Zero);
            _diagQueryPool = default;
        }
    }

}
