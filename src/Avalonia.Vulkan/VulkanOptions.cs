using System;
using System.Collections.Generic;

namespace Avalonia.Vulkan;

public class VulkanOptions
{
    public VulkanInstanceCreationOptions VulkanInstanceCreationOptions { get; set; } = new();
    public VulkanDeviceCreationOptions VulkanDeviceCreationOptions { get; set; } = new();
    public IVulkanDevice? CustomSharedDevice { get; set; }

    /// <summary>
    /// Preferred swapchain present mode. The display engine driver selects
    /// the first entry from this list that the surface advertises; if none
    /// are advertised, falls back to <c>VK_PRESENT_MODE_FIFO_KHR</c> which
    /// the Vulkan spec mandates every surface support.
    ///
    /// <para>
    /// Default: <see cref="VulkanPreferredPresentMode.LowLatency"/>, which prefers
    /// <c>IMMEDIATE → MAILBOX → FIFO_RELAXED → FIFO</c>. On modern desktop
    /// platforms windowed surfaces are composited (DWM on Win32, Mutter/KWin/etc.
    /// on X11, the Wayland compositor on Wayland), so the compositor enforces
    /// tear-free presentation at its own vsync regardless of which present mode
    /// the swapchain uses. Under those conditions IMMEDIATE has the lowest
    /// acquire latency (no FIFO queue backpressure) and is the safest default.
    /// </para>
    ///
    /// <para>
    /// Use <see cref="VulkanPreferredPresentMode.NoTear"/> to force the classic
    /// <c>MAILBOX → FIFO_RELAXED → FIFO</c> order. Set this if your app may run
    /// without a desktop compositor (bare X11 / i3 without picom, embedded
    /// platforms, custom Vulkan fullscreen-exclusive paths) where the compositor
    /// is not in the path to absorb tearing.
    /// </para>
    /// </summary>
    public VulkanPreferredPresentMode PreferredPresentMode { get; set; }
        = VulkanPreferredPresentMode.LowLatency;
}

/// <summary>
/// Selects the swapchain present-mode preference order. See
/// <see cref="VulkanOptions.PreferredPresentMode"/> for the trade-off
/// between latency and tear safety on uncomposited surfaces.
/// </summary>
public enum VulkanPreferredPresentMode
{
    /// <summary>
    /// Prefer <c>IMMEDIATE → MAILBOX → FIFO_RELAXED → FIFO</c>. Lowest
    /// acquire latency. Tear-free *only* under a desktop compositor.
    /// </summary>
    LowLatency,

    /// <summary>
    /// Prefer <c>MAILBOX → FIFO_RELAXED → FIFO</c>. Tear-free even without
    /// a compositor (mode prevents mid-scan-out swaps at the swapchain
    /// level). On X11/Mesa MAILBOX is often unavailable, in which case
    /// this falls through to FIFO and you pay the vsync gate.
    /// </summary>
    NoTear,
}
public class VulkanInstanceCreationOptions
{
    public VkGetInstanceProcAddressDelegate? CustomGetProcAddressDelegate { get; set; }
    
    /// <summary>
    /// Sets the application name of the vulkan instance
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>
    /// Specifies the vulkan api version to use
    /// </summary>
    public Version VulkanVersion{ get; set; } =  new Version(1, 1, 0);

    /// <summary>
    /// Specifies additional extensions to enable if available on the instance
    /// </summary>
    public IList<string> InstanceExtensions { get; set; } = new List<string>();

    /// <summary>
    /// Specifies layers to enable if available on the instance
    /// </summary>
    public IList<string> EnabledLayers { get; set; } = new List<string>();

    /// <summary>
    /// Enables the debug layer
    /// </summary>
    public bool UseDebug { get; set; }

    /*


    /// <summary>
    /// Sets the presentation mode the swapchain uses if available.
    /// </summary>
    //public PresentMode PresentMode { get; set; } = PresentMode.Mailbox;*/
}

public class VulkanDeviceCreationOptions
{
    /// <summary>
    /// Specifies extensions to enable if available on the logical device
    /// </summary>
    public IList<string> DeviceExtensions { get; set; } = new List<string>();
    
    /// <summary>
    /// Selects the first suitable discrete gpu available
    /// </summary>
    public bool PreferDiscreteGpu { get; set; }
    
    public bool RequireComputeBit { get; set; }
}

public class VulkanPlatformSpecificOptions
{
    public IList<string> RequiredInstanceExtensions { get; set; } = new List<string>();
    public VkGetInstanceProcAddressDelegate? GetProcAddressDelegate { get; set; }
    public Func<IVulkanInstance, ulong>? DeviceCheckSurfaceFactory { get; set; }
    public Dictionary<Type, object> PlatformFeatures { get; set; } = new();
    public Action<Action>? OnPresentFence { get; set; }
    public bool IsDynamicMode { get; set; }

    /// <summary>
    /// Carried-through copy of <see cref="VulkanOptions.PreferredPresentMode"/> so
    /// <see cref="VulkanContext"/> can hand it to the swapchain creation path
    /// without re-threading <see cref="VulkanOptions"/> through every layer.
    /// Populated by <see cref="VulkanPlatformGraphics.TryCreate"/>.
    /// </summary>
    public VulkanPreferredPresentMode PreferredPresentMode { get; set; }
        = VulkanPreferredPresentMode.LowLatency;
}

public delegate IntPtr VkGetInstanceProcAddressDelegate(IntPtr instance, string name);
