using System;
using System.Diagnostics;
using Avalonia.Platform;
using Avalonia.Vulkan.Interop;
using Avalonia.Vulkan.UnmanagedInterop;

namespace Avalonia.Vulkan;


internal class VulkanKhrRenderTarget : IVulkanRenderTarget
{
    private readonly IVulkanPlatformGraphicsContext _context;
    private VulkanDisplay _display;
    private VulkanImage? _image;
    private readonly IVulkanKhrSurfacePlatformSurface _platformSurface;
    public VkFormat Format { get; }
    public bool IsRgba { get; }

    // --- [VKD] inside-Dispose phase timing ---------------------------------------------
    // Splits each RenderingSession.Dispose() into:
    //   acquire     : vkAcquireNextImageKHR (timed inside StartPresentation)
    //   ccb         : CommandBufferPool.CreateCommandBuffer (timed inside StartPresentation)
    //   spRest      : the rest of StartPresentation (begin recording + transition record)
    //   blit        : the Display.BlitImageToCurrentImage call (CPU command recording)
    //   endPres     : the entire Display.EndPresentation call (transition + reset + submit
    //                 + present + register fence callback)
    //   lockRel     : the _dispose.Dispose() that releases Device.Lock (in finally)
    private const double VkdJitterThresholdMs = 9.0;
    private const int    VkdWorstPerWindow    = 5;
    private long _vkdFrameCount;
    private long _vkdJitterCount;
    private long _vkdLastReportTs;
    private readonly double[] _vkdWorstTotal     = new double[VkdWorstPerWindow];
    private readonly double[] _vkdWorstAcquire   = new double[VkdWorstPerWindow];
    private readonly double[] _vkdWorstCcb       = new double[VkdWorstPerWindow];
    private readonly double[] _vkdWorstSpRest    = new double[VkdWorstPerWindow];
    private readonly double[] _vkdWorstBlit      = new double[VkdWorstPerWindow];
    private readonly double[] _vkdWorstEndPres   = new double[VkdWorstPerWindow];
    private readonly double[] _vkdWorstLockRel   = new double[VkdWorstPerWindow];
    private readonly long  [] _vkdWorstFrameNo   = new long  [VkdWorstPerWindow];

    private void VkdReport(double acquireMs, double ccbMs, double spRestMs,
        double blitMs, double endPresMs, double lockRelMs)
    {
        _vkdFrameCount++;
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        double totalMs = acquireMs + ccbMs + spRestMs + blitMs + endPresMs + lockRelMs;

        if (totalMs > VkdJitterThresholdMs)
        {
            _vkdJitterCount++;
            int worstIdx = 0;
            for (int i = 1; i < VkdWorstPerWindow; i++)
                if (_vkdWorstTotal[i] < _vkdWorstTotal[worstIdx]) worstIdx = i;
            if (totalMs > _vkdWorstTotal[worstIdx])
            {
                _vkdWorstTotal  [worstIdx] = totalMs;
                _vkdWorstAcquire[worstIdx] = acquireMs;
                _vkdWorstCcb    [worstIdx] = ccbMs;
                _vkdWorstSpRest [worstIdx] = spRestMs;
                _vkdWorstBlit   [worstIdx] = blitMs;
                _vkdWorstEndPres[worstIdx] = endPresMs;
                _vkdWorstLockRel[worstIdx] = lockRelMs;
                _vkdWorstFrameNo[worstIdx] = _vkdFrameCount;
            }
        }

        long now = Stopwatch.GetTimestamp();
        if (_vkdLastReportTs == 0) _vkdLastReportTs = now;
        double sinceReportMs = (now - _vkdLastReportTs) * tickToMs;
        if (sinceReportMs >= 1000.0)
        {
            Console.Error.WriteLine(
                $"[VKD] 1s: frames={_vkdFrameCount} jitter={_vkdJitterCount}");
            for (int i = 0; i < VkdWorstPerWindow; i++)
            {
                if (_vkdWorstTotal[i] <= 0) continue;
                Console.Error.WriteLine(
                    $"  worst#{i} f={_vkdWorstFrameNo[i]} total={_vkdWorstTotal[i]:F2}ms "
                    + $"acquire={_vkdWorstAcquire[i]:F2} ccb={_vkdWorstCcb[i]:F2} "
                    + $"spRest={_vkdWorstSpRest[i]:F2} blit={_vkdWorstBlit[i]:F2} "
                    + $"endPres={_vkdWorstEndPres[i]:F2} lockRel={_vkdWorstLockRel[i]:F2}");
                _vkdWorstTotal[i] = 0; _vkdWorstAcquire[i] = 0; _vkdWorstCcb[i] = 0;
                _vkdWorstSpRest[i] = 0; _vkdWorstBlit[i] = 0; _vkdWorstEndPres[i] = 0;
                _vkdWorstLockRel[i] = 0; _vkdWorstFrameNo[i] = 0;
            }
            _vkdFrameCount = 0; _vkdJitterCount = 0; _vkdLastReportTs = now;
        }
    }

    public VulkanKhrRenderTarget(IVulkanKhrSurfacePlatformSurface surface, IVulkanPlatformGraphicsContext context, bool isDynamicMode = false)
    {
        _platformSurface = surface;
        _display = VulkanDisplay.CreateDisplay(context, surface, isDynamicMode);
        _context = context;
        IsRgba = _display.SurfaceFormat.format >= VkFormat.VK_FORMAT_R8G8B8A8_UNORM &&
                 _display.SurfaceFormat.format <= VkFormat.VK_FORMAT_R8G8B8A8_SRGB;

        // Skia seems to only create surfaces from images with unorm format
        Format = IsRgba ? VkFormat.VK_FORMAT_R8G8B8A8_UNORM : VkFormat.VK_FORMAT_B8G8R8A8_UNORM;
    }

    public PlatformRenderTargetState State => PlatformRenderTargetState.Ready;

    private void CreateImage()
    {
        _image = new VulkanImage(_context, _display.CommandBufferPool, Format, _display.Size);
    }

    private void DestroyImage()
    {
        _context.DeviceApi.DeviceWaitIdle(_context.DeviceHandle);
        _image?.Dispose();
        _image = null;
    }

    public void Dispose()
    {
        _context.DeviceApi.DeviceWaitIdle(_context.DeviceHandle);
        DestroyImage();
        _display?.Dispose();
        _display = null!;
    }


    public IVulkanRenderSession BeginDraw()
    {
        var l = _context.EnsureCurrent();
        // Non-blocking pool drain. FreeUsedCommandBuffers blocks on _fence.Wait() with no
        // timeout for every queued command buffer; under FIFO_KHR pacing the GPU is
        // typically still processing the previous submit, which made BeginDraw stall a
        // full vsync cycle (or more under burst). FreeFinishedCommandBuffers only
        // disposes already-signaled CBs, leaving in-flight ones queued for next frame.
        _display.CommandBufferPool.FreeFinishedCommandBuffers();
        if (_display.EnsureSwapchainAvailable() || _image == null)
        {
            DestroyImage();
            CreateImage();
        }
        else
            _image.TransitionLayout(VkImageLayout.VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                VkAccessFlags.VK_ACCESS_NONE);

        return new RenderingSession(this, _display, _image!, IsRgba, _platformSurface.Scaling, l);
    }

    public class RenderingSession : IVulkanRenderSession
    {
        private readonly VulkanKhrRenderTarget _owner;
        private readonly VulkanImage _image;
        private readonly IDisposable _dispose;

        public RenderingSession(VulkanKhrRenderTarget owner, VulkanDisplay display, VulkanImage image,
            bool isRgba, double scaling, IDisposable dispose)
        {
            _owner = owner;
            _image = image;
            _dispose = dispose;
            Display = display;
            IsRgba = isRgba;
            Scaling = scaling;
        }

        public VulkanDisplay Display { get; }
        public PixelSize Size => _image.Size;
        public double Scaling { get; }
        public bool IsYFlipped => true;
        public VulkanImageInfo ImageInfo => _image.ImageInfo;

        public bool IsRgba { get; }

        public void Dispose()
        {
            double tickToMs = 1000.0 / Stopwatch.Frequency;
            long t0 = Stopwatch.GetTimestamp();
            double blitMs = 0, endPresMs = 0, lockRelMs = 0;
            try
            {
                var commandBuffer = Display.StartPresentation();
                long t1 = Stopwatch.GetTimestamp();   // StartPresentation done

                Display.BlitImageToCurrentImage(commandBuffer, _image);
                long t2 = Stopwatch.GetTimestamp();
                blitMs = (t2 - t1) * tickToMs;

                Display.EndPresentation(commandBuffer);
                long t3 = Stopwatch.GetTimestamp();
                endPresMs = (t3 - t2) * tickToMs;
            }
            finally
            {
                long tBeforeRelease = Stopwatch.GetTimestamp();
                _dispose.Dispose();
                long tAfterRelease = Stopwatch.GetTimestamp();
                lockRelMs = (tAfterRelease - tBeforeRelease) * tickToMs;
            }
            // Acquire/CCB/spRest were captured into instance fields by StartPresentation
            // before the blit ran; safe to read here on the same thread.
            _owner.VkdReport(
                Display.DiagAcquireMs,
                Display.DiagCreateCommandBufferMs,
                Display.DiagSpRestMs,
                blitMs,
                endPresMs,
                lockRelMs);
        }
    }
}
