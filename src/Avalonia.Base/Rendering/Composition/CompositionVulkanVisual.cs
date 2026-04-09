using System.Collections.Generic;
using Avalonia.Rendering.Composition.Server;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Threading;

namespace Avalonia.Rendering.Composition;

/// <summary>
/// A composition visual that performs direct Vulkan rendering,
/// bypassing Skia and all intermediate image copies.
/// </summary>
public sealed class CompositionVulkanVisual : CompositionContainerVisual
{
    private static readonly ThreadSafeObjectPool<List<object>> s_messageListPool = new();
    private List<object>? _messages;

    internal CompositionVulkanVisual(Compositor compositor, CompositionVulkanVisualHandler handler)
        : base(compositor, new ServerCompositionVulkanVisual(compositor.Server, handler))
    {
    }

    public void SendHandlerMessage(object message)
    {
        if (_messages == null)
        {
            _messages = s_messageListPool.Get();
            Compositor.RequestCompositionUpdate(OnCompositionUpdate);
        }
        _messages.Add(message);
    }

    private void OnCompositionUpdate()
    {
        if (_messages == null)
            return;

        var messages = _messages;
        _messages = null;
        Compositor.PostServerJob(() =>
        {
            ((ServerCompositionVulkanVisual)Server).DispatchMessages(messages);
            messages.Clear();
            s_messageListPool.ReturnAndSetNull(ref messages);
        });
    }
}
