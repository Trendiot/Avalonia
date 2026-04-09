using System;
using System.Numerics;
using Avalonia.Rendering.Composition.Server;

namespace Avalonia.Rendering.Composition;

/// <summary>
/// Base class for handling direct GPU rendering in the compositor,
/// bypassing Skia and all intermediate image copies.
///
/// Subclass in the platform-specific layer (e.g. Avalonia.Vulkan) to
/// provide strongly-typed GPU image and device parameters.
/// </summary>
public abstract class CompositionVulkanVisualHandler
{
    private ServerCompositionVulkanVisual? _host;

    public virtual void OnMessage(object message)
    {
    }

    public virtual void OnAnimationFrameUpdate()
    {
    }

    /// <summary>
    /// Called on the render thread to perform direct GPU rendering.
    /// Implement in the platform-specific subclass with typed parameters.
    /// </summary>
    internal abstract void OnDirectRender(ServerVisualRenderContext context);

    void VerifyAccess()
    {
        if (_host == null)
            throw new InvalidOperationException("Object is not yet attached to the compositor");
        _host.Compositor.VerifyAccess();
    }

    protected Vector EffectiveSize
    {
        get
        {
            VerifyAccess();
            return _host!.Size;
        }
    }

    protected TimeSpan CompositionNow
    {
        get
        {
            VerifyAccess();
            return _host!.Compositor.ServerNow;
        }
    }

    public virtual Rect GetRenderBounds() =>
        new(0, 0, EffectiveSize.X, EffectiveSize.Y);

    internal void Attach(ServerCompositionVulkanVisual visual) => _host = visual;

    protected void Invalidate()
    {
        VerifyAccess();
        _host!.HandlerInvalidate();
    }

    protected void Invalidate(Rect rc)
    {
        VerifyAccess();
        _host!.HandlerInvalidate(rc);
    }

    protected void RegisterForNextAnimationFrameUpdate()
    {
        VerifyAccess();
        _host!.HandlerRegisterForNextAnimationFrameUpdate();
    }
}
