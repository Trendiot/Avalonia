using System;
using System.Diagnostics;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using Avalonia.Vulkan;
using SkiaSharp;

namespace Avalonia.Skia.Vulkan;

class VulkanSkiaRenderTarget : ISkiaGpuRenderTarget
{
    private readonly VulkanSkiaGpu _gpu;
    private readonly IVulkanRenderTarget _target;

    // --- Skia phase timing diagnostic --------------------------------------------------
    // The session-disposal sequence in VulkanSkiaRenderSession.Dispose() does:
    //   1. SkSurface.Canvas.Flush()  -> flush draws into Skia's internal command stream
    //   2. SkSurface.Dispose()       -> release Skia surface object
    //   3. GrContext.Flush()         -> submit Skia's GPU command buffers (Vulkan)
    //                                   THIS IS WHERE SKIA CAN WAIT ON ITS OWN INTERNAL
    //                                   FENCES PROTECTING IN-FLIGHT RESOURCES
    //   4. _vulkanSession.Dispose()  -> our blit + queue submit + queue present
    // We time each phase. If `grContextFlush` carries the periodic 20-50ms spike, the
    // trigger is Skia's deferred resource tracking hitting a fence wait under FIFO. If
    // `vulkanDispose` (our submit+present) carries it, the trigger is in our path. If
    // `canvasFlush` carries it, it's CPU-side draw command translation in Skia.
    private const double DiagJitterThresholdMs = 9.0;
    private const int    DiagWorstPerWindow    = 5;
    private long _diagFrameCount;
    private long _diagJitterCount;
    private long _diagLastReportTs;
    private readonly double[] _diagWorstTotal       = new double[DiagWorstPerWindow];
    private readonly double[] _diagWorstCanvasFlush = new double[DiagWorstPerWindow];
    private readonly double[] _diagWorstSurfaceDisp = new double[DiagWorstPerWindow];
    private readonly double[] _diagWorstGrFlush     = new double[DiagWorstPerWindow];
    private readonly double[] _diagWorstVkDispose   = new double[DiagWorstPerWindow];
    private readonly long  [] _diagWorstFrameNo     = new long  [DiagWorstPerWindow];

    internal void DiagReport(double canvasFlushMs, double surfaceDisposeMs,
        double grFlushMs, double vulkanDisposeMs)
    {
        _diagFrameCount++;
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        double totalMs = canvasFlushMs + surfaceDisposeMs + grFlushMs + vulkanDisposeMs;

        if (totalMs > DiagJitterThresholdMs)
        {
            _diagJitterCount++;
            int worstIdx = 0;
            for (int i = 1; i < DiagWorstPerWindow; i++)
                if (_diagWorstTotal[i] < _diagWorstTotal[worstIdx]) worstIdx = i;
            if (totalMs > _diagWorstTotal[worstIdx])
            {
                _diagWorstTotal      [worstIdx] = totalMs;
                _diagWorstCanvasFlush[worstIdx] = canvasFlushMs;
                _diagWorstSurfaceDisp[worstIdx] = surfaceDisposeMs;
                _diagWorstGrFlush    [worstIdx] = grFlushMs;
                _diagWorstVkDispose  [worstIdx] = vulkanDisposeMs;
                _diagWorstFrameNo    [worstIdx] = _diagFrameCount;
            }
        }

        long now = Stopwatch.GetTimestamp();
        if (_diagLastReportTs == 0) _diagLastReportTs = now;
        double sinceReportMs = (now - _diagLastReportTs) * tickToMs;
        if (sinceReportMs >= 1000.0)
        {
            Console.Error.WriteLine(
                $"[SK] 1s: frames={_diagFrameCount} jitter={_diagJitterCount}");
            for (int i = 0; i < DiagWorstPerWindow; i++)
            {
                if (_diagWorstTotal[i] <= 0) continue;
                Console.Error.WriteLine(
                    $"  worst#{i} f={_diagWorstFrameNo[i]} total={_diagWorstTotal[i]:F2}ms "
                    + $"canvasFlush={_diagWorstCanvasFlush[i]:F2} "
                    + $"surfaceDispose={_diagWorstSurfaceDisp[i]:F2} "
                    + $"grFlush={_diagWorstGrFlush[i]:F2} "
                    + $"vkDispose={_diagWorstVkDispose[i]:F2}");
                _diagWorstTotal[i] = 0; _diagWorstCanvasFlush[i] = 0;
                _diagWorstSurfaceDisp[i] = 0; _diagWorstGrFlush[i] = 0;
                _diagWorstVkDispose[i] = 0; _diagWorstFrameNo[i] = 0;
            }
            _diagFrameCount = 0; _diagJitterCount = 0; _diagLastReportTs = now;
        }
    }

    public VulkanSkiaRenderTarget(VulkanSkiaGpu gpu, IVulkanRenderTarget target)
    {
        _gpu = gpu;
        _target = target;
    }

    public void Dispose()
    {
        _target.Dispose();
    }

    public ISkiaGpuRenderSession BeginRenderingSession(IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        // TODO: use expectedPixelSize
        var session = _target.BeginDraw();
        bool success = false;
        try
        {
            var size = session.Size;
            var scaling = session.Scaling;
            if (size.Width <= 0 || size.Height <= 0 || scaling < 0)
            {
                session.Dispose();
                throw new InvalidOperationException(
                    $"Can't create drawing context for surface with {size} size and {scaling} scaling");
            }
            _gpu.GrContext.ResetContext();
            var sessionImageInfo = session.ImageInfo;
            var imageInfo = new GRVkImageInfo
            {
                CurrentQueueFamily = _gpu.Vulkan.Device.GraphicsQueueFamilyIndex,
                Format = sessionImageInfo.Format,
                Image = (ulong)sessionImageInfo.Handle,
                ImageLayout = sessionImageInfo.Layout,
                ImageTiling = sessionImageInfo.Tiling,
                ImageUsageFlags = sessionImageInfo.UsageFlags,
                LevelCount = sessionImageInfo.LevelCount,
                SampleCount = sessionImageInfo.SampleCount,
                Protected = sessionImageInfo.IsProtected,
                Alloc = new GRVkAlloc
                {
                    Memory = (ulong)sessionImageInfo.MemoryHandle,
                    Size = sessionImageInfo.MemorySize
                }
            };
            using var renderTarget = new GRBackendRenderTarget(size.Width, size.Height, imageInfo);
            var surface = SKSurface.Create(_gpu.GrContext, renderTarget,
                session.IsYFlipped ? GRSurfaceOrigin.TopLeft : GRSurfaceOrigin.BottomLeft,
                session.IsRgba ? SKColorType.Rgba8888 : SKColorType.Bgra8888, SKColorSpace.CreateSrgb());

            if (surface == null)
                throw new InvalidOperationException(
                    $"Surface can't be created with the provided render target");
            success = true;
            return new VulkanSkiaRenderSession(this, _gpu.GrContext, surface, session);
        }
        finally
        {
            if(!success)
                session.Dispose();
        }
    }

    public PlatformRenderTargetState State => _target.State;


    internal class VulkanSkiaRenderSession : ISkiaGpuRenderSession
    {
        private readonly VulkanSkiaRenderTarget _owner;
        private readonly IVulkanRenderSession _vulkanSession;

        public VulkanSkiaRenderSession(VulkanSkiaRenderTarget owner, GRContext grContext,
            SKSurface surface,
            IVulkanRenderSession vulkanSession)
        {
            _owner = owner;
            GrContext = grContext;
            SkSurface = surface;
            _vulkanSession = vulkanSession;
            SurfaceOrigin = vulkanSession.IsYFlipped ? GRSurfaceOrigin.TopLeft : GRSurfaceOrigin.BottomLeft;
        }

        public void Dispose()
        {
            double tickToMs = 1000.0 / Stopwatch.Frequency;
            long t0 = Stopwatch.GetTimestamp();
            SkSurface.Canvas.Flush();
            long t1 = Stopwatch.GetTimestamp();
            SkSurface.Dispose();
            long t2 = Stopwatch.GetTimestamp();
            GrContext.Flush();
            long t3 = Stopwatch.GetTimestamp();
            _vulkanSession.Dispose();
            long t4 = Stopwatch.GetTimestamp();

            _owner.DiagReport(
                (t1 - t0) * tickToMs,
                (t2 - t1) * tickToMs,
                (t3 - t2) * tickToMs,
                (t4 - t3) * tickToMs);
        }

        public GRContext GrContext { get; }
        public SKSurface SkSurface { get; }
        public double ScaleFactor => _vulkanSession.Scaling;
        public GRSurfaceOrigin SurfaceOrigin { get; }
    }
}
