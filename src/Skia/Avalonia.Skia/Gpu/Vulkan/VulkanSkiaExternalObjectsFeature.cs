using System;
using System.Collections.Generic;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Vulkan;
using SkiaSharp;

namespace Avalonia.Skia.Vulkan;

internal class VulkanSkiaExternalObjectsFeature : IExternalObjectsRenderInterfaceContextFeature
{
    private readonly VulkanSkiaGpu _gpu;
    private readonly IVulkanPlatformGraphicsContext _context;
    private readonly IVulkanContextExternalObjectsFeature _feature;

    public VulkanSkiaExternalObjectsFeature(VulkanSkiaGpu gpu,
        IVulkanPlatformGraphicsContext context, IVulkanContextExternalObjectsFeature feature)
    {
        _gpu = gpu;
        _context = context;
        _feature = feature;
    }


    public IPlatformRenderInterfaceImportedImage ImportImage(IPlatformHandle handle,
        PlatformGraphicsExternalImageProperties properties) =>
        new Image(_gpu, _feature.ImportImage(handle, properties), properties);

    public IPlatformRenderInterfaceImportedSemaphore ImportSemaphore(IPlatformHandle handle) => new Semaphore(_feature.ImportSemaphore(handle));

    class Semaphore : IPlatformRenderInterfaceImportedSemaphore
    {
        private IVulkanExternalSemaphore? _inner;

        public Semaphore(IVulkanExternalSemaphore inner)
        {
            _inner = inner;
        }

        public void Dispose()
        {
            _inner?.Dispose();
            _inner = null;
        }

        public IVulkanExternalSemaphore Inner =>
            _inner ?? throw new ObjectDisposedException(nameof(IVulkanExternalSemaphore));
        
    }
    
    class Image : IPlatformRenderInterfaceImportedImage
    {
        private readonly VulkanSkiaGpu _gpu;
        private IVulkanExternalImage? _inner;
        private readonly PlatformGraphicsExternalImageProperties _properties;

        // [SCACHE4] Skip SKSurface entirely; use SKImage.FromTexture against a cached
        // GRBackendTexture. Test history that informed this design:
        //   - [SCACHE2] cache both GRBackendRenderTarget AND SKSurface -> jitter gone but
        //     stale-snapshot flicker (SKSurface generation tracking never bumps for
        //     external writes).
        //   - [SCACHE3] cache only GRBackendRenderTarget, recreate SKSurface per call ->
        //     no flicker but jitter returned (SKSurface creation itself is the heavy
        //     Skia work, not GRBackendRenderTarget).
        //   - [SCACHE4, this] skip SKSurface entirely. Cache GRBackendTexture (which
        //     holds the heavyweight VkImage-handle-keyed Skia internals: image views,
        //     descriptor sets, sampler bindings). Each "snapshot" call creates a fresh
        //     SKImage via SKImage.FromTexture, which is lightweight and bypasses any
        //     snapshot generation tracking.
        // This is the canonical Skia pattern for wrapping external Vulkan images
        // (per https://skia.org/docs/user/special/vulkan/). Invalidate when the
        // underlying VkImage handle changes (image was reimported on the producer side).
        private GRBackendTexture? _cachedTexture;
        private ulong _cachedImageHandle;

        public Image(VulkanSkiaGpu gpu, IVulkanExternalImage inner, PlatformGraphicsExternalImageProperties properties)
        {
            _gpu = gpu;
            _inner = inner;
            _properties = properties;
        }

        public void Dispose()
        {
            _cachedTexture?.Dispose();
            _cachedTexture = null;
            _inner?.Dispose();
            _inner = null;
        }

        public IBitmapImpl SnapshotWithKeyedMutex(uint acquireIndex, uint releaseIndex) => throw new NotSupportedException();

        public IBitmapImpl SnapshotWithSemaphores(IPlatformRenderInterfaceImportedSemaphore waitForSemaphore,
            IPlatformRenderInterfaceImportedSemaphore signalSemaphore)
        {
            var info = _inner!.Info;

            // [SNP] inline timing: pinpoint which phase of SnapshotWithSemaphores is the
            // periodic 5-32ms stall. With [SCACHE2] caching, the surfCreate phase should
            // collapse to ~0 on cache hits; if grFlush also collapses, then per-snapshot
            // SkSurface churn was the cause of the post-resize jitter.
            double tickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

            _gpu.GrContext.ResetContext();
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

            ((Semaphore)waitForSemaphore).Inner.SubmitWaitSemaphore();
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

            ulong handle = (ulong)info.Handle;
            if (_cachedTexture == null || _cachedImageHandle != handle)
            {
                _cachedTexture?.Dispose();
                var imageInfo = new GRVkImageInfo
                {
                    CurrentQueueFamily = _gpu.Vulkan.Device.GraphicsQueueFamilyIndex,
                    Format = info.Format,
                    Image = handle,
                    ImageLayout = info.Layout,
                    ImageTiling = info.Tiling,
                    ImageUsageFlags = info.UsageFlags,
                    LevelCount = info.LevelCount,
                    SampleCount = info.SampleCount,
                    Protected = info.IsProtected,
                    Alloc = new GRVkAlloc
                    {
                        Memory = (ulong)info.MemoryHandle,
                        Size = info.MemorySize
                    }
                };
                _cachedTexture = new GRBackendTexture(_properties.Width, _properties.Height, imageInfo);
                _cachedImageHandle = handle;
            }
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();

            var image = SKImage.FromTexture(_gpu.GrContext, _cachedTexture,
                _properties.TopLeftOrigin ? GRSurfaceOrigin.TopLeft : GRSurfaceOrigin.BottomLeft,
                _properties.Format == PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm
                    ? SKColorType.Rgba8888
                    : SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();

            _gpu.GrContext.Flush();
            long t5 = System.Diagnostics.Stopwatch.GetTimestamp();

            ((Semaphore)signalSemaphore).Inner.SubmitSignalSemaphore();
            long t6 = System.Diagnostics.Stopwatch.GetTimestamp();

            DiagReport(
                (t1 - t0) * tickToMs,   // resetCtx
                (t2 - t1) * tickToMs,   // submitWait
                (t3 - t2) * tickToMs,   // surfaceCreate (includes GRBackendRenderTarget + SKSurface.Create)
                (t4 - t3) * tickToMs,   // snapshot
                (t5 - t4) * tickToMs,   // grFlush
                (t6 - t5) * tickToMs);  // submitSignal

            return new ImmutableBitmap(image);
        }

        // --- [SNP] per-phase diagnostic for SnapshotWithSemaphores ------------------------
        private const double SnpJitterThresholdMs = 5.0;
        private const int    SnpWorstPerWindow    = 5;
        private long _snpFrameCount;
        private long _snpJitterCount;
        private long _snpLastReportTs;
        private readonly double[] _snpWorstTotal       = new double[SnpWorstPerWindow];
        private readonly double[] _snpWorstResetCtx    = new double[SnpWorstPerWindow];
        private readonly double[] _snpWorstSubmitWait  = new double[SnpWorstPerWindow];
        private readonly double[] _snpWorstSurfCreate  = new double[SnpWorstPerWindow];
        private readonly double[] _snpWorstSnapshot    = new double[SnpWorstPerWindow];
        private readonly double[] _snpWorstGrFlush     = new double[SnpWorstPerWindow];
        private readonly double[] _snpWorstSubmitSig   = new double[SnpWorstPerWindow];
        private readonly long  [] _snpWorstFrameNo     = new long  [SnpWorstPerWindow];

        private void DiagReport(double resetCtxMs, double submitWaitMs, double surfCreateMs,
            double snapshotMs, double grFlushMs, double submitSigMs)
        {
            _snpFrameCount++;
            double tickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double totalMs = resetCtxMs + submitWaitMs + surfCreateMs + snapshotMs + grFlushMs + submitSigMs;

            if (totalMs > SnpJitterThresholdMs)
            {
                _snpJitterCount++;
                int worstIdx = 0;
                for (int i = 1; i < SnpWorstPerWindow; i++)
                    if (_snpWorstTotal[i] < _snpWorstTotal[worstIdx]) worstIdx = i;
                if (totalMs > _snpWorstTotal[worstIdx])
                {
                    _snpWorstTotal     [worstIdx] = totalMs;
                    _snpWorstResetCtx  [worstIdx] = resetCtxMs;
                    _snpWorstSubmitWait[worstIdx] = submitWaitMs;
                    _snpWorstSurfCreate[worstIdx] = surfCreateMs;
                    _snpWorstSnapshot  [worstIdx] = snapshotMs;
                    _snpWorstGrFlush   [worstIdx] = grFlushMs;
                    _snpWorstSubmitSig [worstIdx] = submitSigMs;
                    _snpWorstFrameNo   [worstIdx] = _snpFrameCount;
                }
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_snpLastReportTs == 0) _snpLastReportTs = now;
            double sinceReportMs = (now - _snpLastReportTs) * tickToMs;
            if (sinceReportMs >= 1000.0)
            {
                System.Console.Error.WriteLine(
                    $"[SNP] 1s: calls={_snpFrameCount} jitter={_snpJitterCount}");
                for (int i = 0; i < SnpWorstPerWindow; i++)
                {
                    if (_snpWorstTotal[i] <= 0) continue;
                    System.Console.Error.WriteLine(
                        $"  worst#{i} f={_snpWorstFrameNo[i]} total={_snpWorstTotal[i]:F2}ms "
                        + $"resetCtx={_snpWorstResetCtx[i]:F2} "
                        + $"submitWait={_snpWorstSubmitWait[i]:F2} "
                        + $"surfCreate={_snpWorstSurfCreate[i]:F2} "
                        + $"snapshot={_snpWorstSnapshot[i]:F2} "
                        + $"grFlush={_snpWorstGrFlush[i]:F2} "
                        + $"submitSig={_snpWorstSubmitSig[i]:F2}");
                    _snpWorstTotal[i] = 0; _snpWorstResetCtx[i] = 0;
                    _snpWorstSubmitWait[i] = 0; _snpWorstSurfCreate[i] = 0;
                    _snpWorstSnapshot[i] = 0; _snpWorstGrFlush[i] = 0;
                    _snpWorstSubmitSig[i] = 0; _snpWorstFrameNo[i] = 0;
                }
                _snpFrameCount = 0; _snpJitterCount = 0; _snpLastReportTs = now;
            }
        }

        public IBitmapImpl SnapshotWithTimelineSemaphores(IPlatformRenderInterfaceImportedSemaphore waitForSemaphore,
            ulong waitForValue, IPlatformRenderInterfaceImportedSemaphore signalSemaphore, ulong signalValue) =>
            throw new NotSupportedException();

        public IBitmapImpl SnapshotWithAutomaticSync() => throw new NotSupportedException();
    }

    public IPlatformRenderInterfaceImportedImage ImportImage(ICompositionImportableSharedGpuContextImage image) => throw new System.NotSupportedException();

    public CompositionGpuImportedImageSynchronizationCapabilities
        GetSynchronizationCapabilities(string imageHandleType) => _feature.GetSynchronizationCapabilities(imageHandleType);

    public byte[]? DeviceUuid => _feature.DeviceUuid;
    public byte[]? DeviceLuid => _feature.DeviceLuid;
    
    public IReadOnlyList<string> SupportedImageHandleTypes => _feature.SupportedImageHandleTypes;
    public IReadOnlyList<string> SupportedSemaphoreTypes => _feature.SupportedSemaphoreTypes;
}
