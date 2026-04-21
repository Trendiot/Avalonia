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

        // Cached Skia backend texture wrapping the imported VkImage. The heavyweight
        // VkImage-handle-keyed Skia state (image views, descriptor sets, sampler
        // bindings) lives on the texture; recreating it per snapshot drove Skia's
        // internal Vulkan pools into a degraded steady state, producing periodic
        // 5–30 ms grFlush stalls that never recovered after the first external-image
        // identity change (resize / re-import).
        //
        // We cache the texture and create a fresh SKImage via SKImage.FromTexture per
        // snapshot — that's the canonical Skia pattern for wrapping an external
        // Vulkan image (per https://skia.org/docs/user/special/vulkan/). The fresh
        // SKImage has no snapshot-generation tracking, so it always reflects current
        // VkImage contents (which is what we want — the producer wrote to it
        // externally; SKSurface-based snapshots returned stale cached results because
        // Skia only bumps the surface's generation ID on draws through its own canvas).
        //
        // Invalidate the cache only when the underlying VkImage handle changes
        // (image was reimported on the producer side).
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
            // import (no SnapshotWithSemaphores call ever ran, so _cachedTexture
            // stayed null and Skia has no GrVkImage backed by our handle).
            // Avoids a vkQueueWaitIdle-equivalent stall on the common
            // import-but-never-render path (e.g. resize race teardowns).
            bool skiaTouchedThisImage = _cachedTexture != null;

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

            var image = SKImage.FromTexture(_gpu.GrContext, _cachedTexture,
                _properties.TopLeftOrigin ? GRSurfaceOrigin.TopLeft : GRSurfaceOrigin.BottomLeft,
                _properties.Format == PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm
                    ? SKColorType.Rgba8888
                    : SKColorType.Bgra8888,
                SKAlphaType.Premul, SKColorSpace.CreateSrgb());
            _gpu.GrContext.Flush();

            ((Semaphore)signalSemaphore).Inner.SubmitSignalSemaphore();

            return new ImmutableBitmap(image);
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
