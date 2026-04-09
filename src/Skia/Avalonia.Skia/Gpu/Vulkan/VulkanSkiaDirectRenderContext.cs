using Avalonia.Rendering.Composition.Server;
using Avalonia.Vulkan;
using SkiaSharp;

namespace Avalonia.Skia.Vulkan;

/// <summary>
/// Skia-Vulkan implementation of IVulkanDirectRenderContext.
/// Provides the compositor's VulkanImageInfo and manages GrContext flush/reset
/// around external Vulkan rendering.
/// </summary>
internal class VulkanSkiaDirectRenderContext : IVulkanDirectRenderContext
{
    private readonly GRContext? _grContext;

    public VulkanImageInfo ImageInfo { get; }
    public IVulkanDevice Device { get; }

    public VulkanSkiaDirectRenderContext(VulkanImageInfo imageInfo, IVulkanDevice device, GRContext? grContext)
    {
        ImageInfo = imageInfo;
        Device = device;
        _grContext = grContext;
    }

    public void FlushAndSync()
    {
        _grContext?.Flush();
    }

    public void ResetContext()
    {
        _grContext?.ResetContext();
    }
}
