using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Server;

namespace Avalonia.Vulkan;

/// <summary>
/// Strongly-typed handler for direct Vulkan rendering in the compositor.
/// Subclass this and override OnRender to receive VulkanImageInfo and IVulkanDevice.
/// The image is in COLOR_ATTACHMENT_OPTIMAL layout; leave it that way.
/// </summary>
public abstract class VulkanCompositionHandler : CompositionVulkanVisualHandler
{
    /// <summary>
    /// Called on the render thread with the compositor's Vulkan image.
    /// The image is in COLOR_ATTACHMENT_OPTIMAL layout.
    /// You must leave it in COLOR_ATTACHMENT_OPTIMAL when done.
    /// </summary>
    public abstract void OnRender(VulkanImageInfo compositionImage, IVulkanDevice device, PixelSize size);

    internal override void OnDirectRender(ServerVisualRenderContext context)
    {
        if (context.DirectRenderContext is IVulkanDirectRenderContext vkCtx)
        {
            OnRender(vkCtx.ImageInfo, vkCtx.Device, vkCtx.ImageInfo.PixelSize);
        }
    }
}

/// <summary>
/// Provides strongly-typed Vulkan access from the composition direct render context.
/// Implemented by the Skia-Vulkan backend.
/// </summary>
public interface IVulkanDirectRenderContext : ICompositionDirectRenderContext
{
    VulkanImageInfo ImageInfo { get; }
    IVulkanDevice Device { get; }
}
