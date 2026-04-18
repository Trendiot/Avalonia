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

    // --- Per-phase timing diagnostic ---------------------------------------------------
    // Splits each frame into:
    //   gap        : time from previous Dispose() return -> this BeginDraw() entry
    //                (Avalonia render scheduler + idle/wait between frames)
    //   lock       : time inside _context.EnsureCurrent() (Device.Lock acquire)
    //   bdRest     : remainder of BeginDraw (FreeUsedCommandBuffers + EnsureSwapchainAvailable
    //                + image create/transition)
    //   skia       : time between BeginDraw() returning and Dispose() being called
    //                (this is where Skia/Avalonia composition does its drawing)
    //   present    : time inside RenderingSession.Dispose (StartPresentation + Blit
    //                + EndPresentation + lock release)
    //
    // Logs only the worst N frames per 1s window so console I/O cannot perturb timings.
    private const double JitterThresholdMs = 9.0;
    private const int WorstFramesPerWindow = 5;
    internal long DiagPrevDisposeEndTs;
    internal long DiagBeginDrawEnterTs;
    internal long DiagLockAcquiredTs;
    internal long DiagBeginDrawExitTs;
    private long _diagFrameCount;
    private long _diagJitterCount;
    private long _diagLastReportTs;
    private int _diagGen0AtFrameStart;
    private int _diagGen1AtFrameStart;
    private int _diagGen2AtFrameStart;
    private int _diagGen0AtWindowStart;
    private int _diagGen1AtWindowStart;
    private int _diagGen2AtWindowStart;
    private readonly double[] _diagWorstInterval = new double[WorstFramesPerWindow];
    private readonly double[] _diagWorstGap     = new double[WorstFramesPerWindow];
    private readonly double[] _diagWorstLock    = new double[WorstFramesPerWindow];
    private readonly double[] _diagWorstBdRest  = new double[WorstFramesPerWindow];
    private readonly double[] _diagWorstSkia    = new double[WorstFramesPerWindow];
    private readonly double[] _diagWorstPresent = new double[WorstFramesPerWindow];
    private readonly long  [] _diagWorstFrameNo = new long  [WorstFramesPerWindow];
    private readonly int   [] _diagWorstGcGen   = new int   [WorstFramesPerWindow];

    internal void DiagReportFrame(long disposeEndTs, double presentMs, double skiaMs)
    {
        _diagFrameCount++;
        double tickToMs = 1000.0 / Stopwatch.Frequency;

        if (DiagPrevDisposeEndTs == 0)
        {
            DiagPrevDisposeEndTs = disposeEndTs;
            _diagLastReportTs = disposeEndTs;
            _diagGen0AtWindowStart = GC.CollectionCount(0);
            _diagGen1AtWindowStart = GC.CollectionCount(1);
            _diagGen2AtWindowStart = GC.CollectionCount(2);
            return;
        }

        double intervalMs = (disposeEndTs - DiagPrevDisposeEndTs) * tickToMs;
        double gapMs    = (DiagBeginDrawEnterTs - DiagPrevDisposeEndTs) * tickToMs;
        // lock = time spent waiting on Device.Lock acquire only
        double lockMs   = (DiagLockAcquiredTs - DiagBeginDrawEnterTs) * tickToMs;
        // bdRest = post-lock BeginDraw work (FreeUsedCommandBuffers, EnsureSwapchain,
        // image transition / recreate)
        double bdRestMs = (DiagBeginDrawExitTs - DiagLockAcquiredTs) * tickToMs;
        DiagPrevDisposeEndTs = disposeEndTs;

        int gcGen = -1;
        if (GC.CollectionCount(2) > _diagGen2AtFrameStart) gcGen = 2;
        else if (GC.CollectionCount(1) > _diagGen1AtFrameStart) gcGen = 1;
        else if (GC.CollectionCount(0) > _diagGen0AtFrameStart) gcGen = 0;

        if (intervalMs > JitterThresholdMs)
        {
            _diagJitterCount++;
            int worstIdx = 0;
            for (int i = 1; i < WorstFramesPerWindow; i++)
                if (_diagWorstInterval[i] < _diagWorstInterval[worstIdx]) worstIdx = i;
            if (intervalMs > _diagWorstInterval[worstIdx])
            {
                _diagWorstInterval[worstIdx] = intervalMs;
                _diagWorstGap    [worstIdx] = gapMs;
                _diagWorstLock   [worstIdx] = lockMs;
                _diagWorstBdRest [worstIdx] = bdRestMs;
                _diagWorstSkia   [worstIdx] = skiaMs;
                _diagWorstPresent[worstIdx] = presentMs;
                _diagWorstFrameNo[worstIdx] = _diagFrameCount;
                _diagWorstGcGen  [worstIdx] = gcGen;
            }
        }

        double sinceReportMs = (disposeEndTs - _diagLastReportTs) * tickToMs;
        if (sinceReportMs >= 1000.0)
        {
            int g0 = GC.CollectionCount(0); int g1 = GC.CollectionCount(1); int g2 = GC.CollectionCount(2);
            Console.Error.WriteLine(
                $"[VkRT] 1s: frames={_diagFrameCount} jitter={_diagJitterCount} "
                + $"avgFps={(_diagFrameCount * 1000.0 / sinceReportMs):F1} "
                + $"gc=[g0:{g0 - _diagGen0AtWindowStart} g1:{g1 - _diagGen1AtWindowStart} g2:{g2 - _diagGen2AtWindowStart}]");
            for (int i = 0; i < WorstFramesPerWindow; i++)
            {
                if (_diagWorstInterval[i] <= 0) continue;
                string gcTag = _diagWorstGcGen[i] >= 0 ? $" GC-Gen{_diagWorstGcGen[i]}" : "";
                Console.Error.WriteLine(
                    $"  worst#{i} f={_diagWorstFrameNo[i]} interval={_diagWorstInterval[i]:F2}ms "
                    + $"gap={_diagWorstGap[i]:F2} lock={_diagWorstLock[i]:F2} "
                    + $"bdRest={_diagWorstBdRest[i]:F2} skia={_diagWorstSkia[i]:F2} "
                    + $"present={_diagWorstPresent[i]:F2}{gcTag}");
                _diagWorstInterval[i] = 0; _diagWorstGap[i] = 0; _diagWorstLock[i] = 0;
                _diagWorstBdRest[i] = 0; _diagWorstSkia[i] = 0; _diagWorstPresent[i] = 0;
                _diagWorstFrameNo[i] = 0; _diagWorstGcGen[i] = -1;
            }
            _diagFrameCount = 0; _diagJitterCount = 0; _diagLastReportTs = disposeEndTs;
            _diagGen0AtWindowStart = g0; _diagGen1AtWindowStart = g1; _diagGen2AtWindowStart = g2;
        }
    }

    internal void DiagSnapshotGcAtFrameStart()
    {
        _diagGen0AtFrameStart = GC.CollectionCount(0);
        _diagGen1AtFrameStart = GC.CollectionCount(1);
        _diagGen2AtFrameStart = GC.CollectionCount(2);
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
        DiagBeginDrawEnterTs = Stopwatch.GetTimestamp();
        DiagSnapshotGcAtFrameStart();
        var l = _context.EnsureCurrent();
        // Mark the moment Device.Lock was actually acquired so the report can split
        // "lock-wait" from "post-lock BeginDraw work".
        DiagLockAcquiredTs = Stopwatch.GetTimestamp();
        _display.CommandBufferPool.FreeUsedCommandBuffers();
        if (_display.EnsureSwapchainAvailable() || _image == null)
        {
            DestroyImage();
            CreateImage();
        }
        else
            _image.TransitionLayout(VkImageLayout.VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                VkAccessFlags.VK_ACCESS_NONE);

        DiagBeginDrawExitTs = Stopwatch.GetTimestamp();
        return new RenderingSession(this, _display, _image!, IsRgba, _platformSurface.Scaling, l,
            DiagBeginDrawExitTs);
    }

    public class RenderingSession : IVulkanRenderSession
    {
        private readonly VulkanImage _image;
        private readonly IDisposable _dispose;
        private readonly VulkanKhrRenderTarget _owner;
        private readonly long _beginDrawExitTs;

        public RenderingSession(VulkanKhrRenderTarget owner, VulkanDisplay display, VulkanImage image,
            bool isRgba, double scaling, IDisposable dispose, long beginDrawExitTs)
        {
            _owner = owner;
            _image = image;
            _dispose = dispose;
            _beginDrawExitTs = beginDrawExitTs;
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
            long disposeEnterTs = Stopwatch.GetTimestamp();
            double tickToMs = 1000.0 / Stopwatch.Frequency;
            double skiaMs = (disposeEnterTs - _beginDrawExitTs) * tickToMs;
            try
            {
                var commandBuffer = Display.StartPresentation();
                Display.BlitImageToCurrentImage(commandBuffer, _image);
                Display.EndPresentation(commandBuffer);
            }
            finally
            {
                _dispose.Dispose();
            }
            long disposeEndTs = Stopwatch.GetTimestamp();
            double presentMs = (disposeEndTs - disposeEnterTs) * tickToMs;
            _owner.DiagReportFrame(disposeEndTs, presentMs, skiaMs);
        }
    }
}
