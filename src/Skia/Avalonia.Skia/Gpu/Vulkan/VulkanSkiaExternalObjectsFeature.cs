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

        // Cached Skia backend texture + SKImage wrapping the imported VkImage.
        //
        // The heavyweight VkImage-handle-keyed Skia state (image views, descriptor
        // sets, sampler bindings) lives on the texture; recreating it per snapshot
        // drove Skia's internal Vulkan pools into a degraded steady state, producing
        // periodic 5–30 ms grFlush stalls that never recovered after the first
        // external-image identity change (resize / re-import).
        //
        // The SKImage is ALSO cached, not just the texture. This addresses a
        // separate correctness bug: RefCountable.Ref<T> (which wraps the
        // IBitmapImpl we return) has a finalizer that calls Dispose(false) on
        // refcount-zero. If any Ref<IBitmapImpl> escapes to the GC — a very
        // real scenario during rapid import churn or page teardowns — the
        // finalizer runs on the .NET Finalizer thread and synchronously calls
        // SKImage.Dispose(), whose native destructor (~SkImage_Ganesh) touches
        // GrContext-owned state. That's concurrent access with the compositor
        // thread and produces torn-state SIGSEGV inside Skia's GrResourceCache
        // internals.
        //
        // By caching the SKImage and treating the consumer's Dispose as a
        // no-op (via customImageDispose), the SKImage's native lifetime is
        // anchored to this Image instance and released only inside our
        // Dispose(), which is always invoked on the compositor render thread
        // (CompositionGpuImportedObjectBase.DisposeAsync routes through
        // Compositor.InvokeServerJobAsync). No finalizer thread ever sees it.
        //
        // SKImage.FromTexture is safe to cache across snapshots: unlike
        // SKSurface.Snapshot (whose generation ID only bumps on draws through
        // Skia's own canvas — producing stale results when the external
        // producer writes directly to the VkImage), SKImage.FromTexture reads
        // current backend-texture contents at every draw, so the cached
        // SKImage always reflects the latest producer writes.
        //
        // Invalidate both caches only when the underlying VkImage handle
        // changes (image was reimported on the producer side).
        private GRBackendTexture? _cachedTexture;
        private SKImage? _cachedImage;
        private ulong _cachedImageHandle;

        public Image(VulkanSkiaGpu gpu, IVulkanExternalImage inner, PlatformGraphicsExternalImageProperties properties)
        {
            _gpu = gpu;
            _inner = inner;
            _properties = properties;
        }

        public void Dispose()
        {
            // Order matters: Skia holds long-lived sk_sp<GrVkImage> references
            // backed by our underlying VkImage handle through several internal
            // caches, and destroying the VkImage/View/Memory before those
            // references drop produces a use-after-free that surfaces deep
            // inside ~GrVkFramebuffer when the framebuffer cache is later
            // evicted (SIGILL inside GrResourceCache::notifyARefCntReachedZero
            // on a corrupted GrVkImage vtable).
            //
            // The Skia caches that outlive ImmutableBitmap disposal:
            //   • Per-snapshot SKImages — released by the consumer, but their
            //     CB-captured refs live until the next checkCommandBuffers().
            //   • The framebuffer cache (GrVkResourceProvider::fFramebuffers)
            //     holds GrVkFramebuffer objects whose sk_sp<GrVkImage>
            //     fColorAttachment keeps the wrapped image alive past CB
            //     recycling, until the framebuffer is evicted.
            //   • Descriptor sets bound to image views Skia created on top of
            //     our VkImage handle.
            //
            // Sequence below:
            //   1. Drop the cached descriptor so no NEW SKImages can be
            //      created referencing this import after we begin teardown.
            //   2. Flush + submit + sync the GrContext so every CB that
            //      captured a ref to a wrapped GrVkImage completes on the
            //      GPU and is recycled — checkCommandBuffers() drops those
            //      refs during the recycle.
            //   3. Purge unlocked cached resources so the framebuffer cache
            //      releases its sk_sp<GrVkImage> ref and Skia destroys the
            //      wrapped GrVkImage cleanly while our VkImage is still
            //      valid (Skia's destructor frees its own image views and
            //      descriptor sets, which need the underlying VkImage
            //      handle to still exist for well-defined cleanup).
            //   4. Now safe to destroy the underlying VkImage/View/Memory.
            //
            // We're on the compositor render thread (CompositionInterop
            // routes Dispose through Compositor.InvokeServerJobAsync), so
            // GrContext access is safe here.
            // Skip the synchronous flush + purge if Skia never touched this
            // import (no SnapshotWithSemaphores call ever ran, so the caches
            // stayed null and Skia has no GrVkImage backed by our handle).
            // Avoids a vkQueueWaitIdle-equivalent stall on the common
            // import-but-never-render path (e.g. resize race teardowns).
            bool skiaTouchedThisImage = _cachedImage != null;

            // Teardown order within this method matters: SKImage first (drops
            // its ref on GRBackendTexture → GrVkTexture), then GRBackendTexture
            // (Skia descriptor), then flush/purge to reap any cached
            // framebuffers or descriptor sets that still reference the wrapped
            // GrVkImage, THEN the underlying VkImage/View/Memory. All of this
            // runs on the compositor thread (we're invoked via
            // Compositor.InvokeServerJobAsync), so Skia teardown is safe to
            // execute here.
            _cachedImage?.Dispose();
            _cachedImage = null;

            _cachedTexture?.Dispose();
            _cachedTexture = null;

            if (_inner != null)
            {
                if (skiaTouchedThisImage)
                {
                    _gpu.GrContext.Flush(submit: true, synchronous: true);
                    _gpu.GrContext.PurgeResources();
                }
                _inner.Dispose();
                _inner = null;
            }
        }

        public IBitmapImpl SnapshotWithKeyedMutex(uint acquireIndex, uint releaseIndex) => throw new NotSupportedException();

        public IBitmapImpl SnapshotWithSemaphores(IPlatformRenderInterfaceImportedSemaphore waitForSemaphore,
            IPlatformRenderInterfaceImportedSemaphore signalSemaphore)
        {
            var info = _inner!.Info;

            _gpu.GrContext.ResetContext();
            ((Semaphore)waitForSemaphore).Inner.SubmitWaitSemaphore();

            ulong handle = (ulong)info.Handle;
            if (_cachedImage == null || _cachedImageHandle != handle)
            {
                // Handle changed (or first call). Rebuild both caches. Old
                // SKImage goes first so its ref on the old GRBackendTexture
                // drops before we dispose the texture — otherwise Skia
                // internally sees a still-referenced texture being deleted.
                _cachedImage?.Dispose();
                _cachedImage = null;
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
                _cachedImage = SKImage.FromTexture(_gpu.GrContext, _cachedTexture,
                    _properties.TopLeftOrigin ? GRSurfaceOrigin.TopLeft : GRSurfaceOrigin.BottomLeft,
                    _properties.Format == PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm
                        ? SKColorType.Rgba8888
                        : SKColorType.Bgra8888,
                    SKAlphaType.Premul, SKColorSpace.CreateSrgb());
                _cachedImageHandle = handle;
            }

            _gpu.GrContext.Flush();

            ((Semaphore)signalSemaphore).Inner.SubmitSignalSemaphore();

            // Pass a no-op customImageDispose so ImmutableBitmap.Dispose()
            // does not touch our cached SKImage. We own its lifetime and
            // release it in Image.Dispose() on the compositor thread.
            // Critically, this also prevents RefCountable.Ref<T>'s finalizer
            // from triggering SKImage native teardown on the .NET Finalizer
            // thread (which races with the compositor over GrContext and
            // produces SIGSEGV inside SkImage_Ganesh::~SkImage_Ganesh).
            return new ImmutableBitmap(_cachedImage, customImageDispose: static () => { });
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
