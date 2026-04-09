using System;
using System.Collections.Generic;
using Avalonia.Logging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Transport;

namespace Avalonia.Rendering.Composition.Server;

/// <summary>
/// Server-side visual that delegates rendering to a CompositionVulkanVisualHandler,
/// bypassing Skia entirely for direct GPU rendering to the compositor's image.
/// </summary>
internal sealed class ServerCompositionVulkanVisual : ServerCompositionContainerVisual, IServerClockItem
{
    private readonly CompositionVulkanVisualHandler _handler;
    private bool _wantsNextAnimationFrameAfterTick;

    internal ServerCompositionVulkanVisual(ServerCompositor compositor, CompositionVulkanVisualHandler handler)
        : base(compositor)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _handler.Attach(this);
    }

    public void DispatchMessages(List<object> messages)
    {
        foreach (var message in messages)
        {
            try
            {
                _handler.OnMessage(message);
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Error, LogArea.Visual)
                    ?.Log(_handler, $"Exception in {_handler.GetType().Name}.OnMessage {{0}}", e);
            }
        }
    }

    public void OnTick()
    {
        _wantsNextAnimationFrameAfterTick = false;
        _handler.OnAnimationFrameUpdate();
        if (!_wantsNextAnimationFrameAfterTick)
            Compositor.Animations.RemoveFromClock(this);
    }

    public override LtrbRect? ComputeOwnContentBounds() =>
        new LtrbRect(_handler.GetRenderBounds());

    protected override void OnAttachedToRoot(ServerCompositionTarget target)
    {
        if (_wantsNextAnimationFrameAfterTick)
            Compositor.Animations.AddToClock(this);
        base.OnAttachedToRoot(target);
    }

    protected override void OnDetachedFromRoot(ServerCompositionTarget target)
    {
        Compositor.Animations.RemoveFromClock(this);
        base.OnDetachedFromRoot(target);
    }

    internal void HandlerInvalidate() => InvalidateContent();

    internal void HandlerInvalidate(Rect rc) => AddExtraDirtyRect(new LtrbRect(rc));

    internal void HandlerRegisterForNextAnimationFrameUpdate()
    {
        _wantsNextAnimationFrameAfterTick = true;
        if (Root != null)
            Compositor.Animations.AddToClock(this);
    }

    protected override void RenderCore(ServerVisualRenderContext ctx, LtrbRect currentTransformedClip)
    {
        // Flush any pending Skia drawing commands before direct GPU rendering
        var proxy = ctx.Canvas as CompositorDrawingContextProxy;
        if (proxy != null)
        {
            proxy.AutoFlush = true;
            proxy.Flush();
        }

        // Flush and sync the GPU context (Skia's GrContext)
        ctx.DirectRenderContext?.FlushAndSync();

        try
        {
            // Delegate to the handler — it receives the full render context
            // and extracts Vulkan-typed info in the platform-specific subclass
            _handler.OnDirectRender(ctx);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, LogArea.Visual)
                ?.Log(_handler, $"Exception in {_handler.GetType().Name}.OnRender {{0}}", e);
        }

        // Reset graphics context so Skia re-queries GPU state
        ctx.DirectRenderContext?.ResetContext();

        if (proxy != null)
            proxy.AutoFlush = false;
    }
}
