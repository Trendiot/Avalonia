using System;
using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Rendering.Composition.Server;

internal class ServerVisualRenderContext
{
    public IDrawingContextImpl Canvas { get; }

    private ICompositionDirectRenderContext? _directRenderContext;
    private bool _directRenderContextQueried;

    /// <summary>
    /// Lazily-resolved direct render context for Vulkan rendering.
    /// Queried from the canvas's feature set on first access.
    /// </summary>
    public ICompositionDirectRenderContext? DirectRenderContext
    {
        get
        {
            if (!_directRenderContextQueried)
            {
                _directRenderContext = Canvas.GetFeature(typeof(ICompositionDirectRenderContext))
                    as ICompositionDirectRenderContext;
                _directRenderContextQueried = true;
            }
            return _directRenderContext;
        }
    }

    public ServerVisualRenderContext(IDrawingContextImpl canvas)
    {
        Canvas = canvas;
    }
}

/// <summary>
/// Provides access to the compositor's GPU image for direct rendering,
/// bypassing Skia. Implemented by platform-specific backends (e.g. Vulkan).
/// </summary>
public interface ICompositionDirectRenderContext
{
    /// <summary>
    /// Flush and sync the current graphics context (e.g. Skia's GrContext)
    /// before external code modifies the GPU image directly.
    /// </summary>
    void FlushAndSync();

    /// <summary>
    /// Reset the graphics context state after external rendering,
    /// so it re-queries GPU state on next use.
    /// </summary>
    void ResetContext();
}
