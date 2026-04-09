namespace Avalonia.Vulkan;

/// <summary>
/// Interface for rendering directly to the compositor's Vulkan image,
/// bypassing Skia and all intermediate copies.
/// </summary>
public interface IVulkanDirectRenderer
{
    /// <summary>
    /// Called on the compositor/render thread with the device locked.
    /// The image is in COLOR_ATTACHMENT_OPTIMAL layout.
    /// The renderer MUST leave it in COLOR_ATTACHMENT_OPTIMAL.
    /// </summary>
    void Render(VulkanImageInfo compositionImage, IVulkanDevice device, PixelSize size);

    /// <summary>
    /// Called when the composition surface size changes (e.g. window resize).
    /// Use this to recreate size-dependent resources (framebuffers, etc).
    /// </summary>
    void OnSizeChanged(PixelSize size);
}
