using XREngine.Rendering;

namespace XREngine;

internal sealed class RuntimeEngineTimer
{
    private Action? _updateFrame;
    private Action? _postUpdateFrame;
    private Action? _collectVisible;
    private Action? _swapBuffers;
    private Action? _renderFrame;

    public RuntimeTimerFrame Update { get; } = new(ERuntimeTimerFrameKind.Update);
    public RuntimeTimerFrame Render { get; } = new(ERuntimeTimerFrameKind.Render);
    public event Action? UpdateFrame
    {
        add
        {
            if (value is not null)
            {
                _updateFrame += value;
                RuntimeRenderingHostServices.Scheduling.SubscribeUpdateFrame(value);
            }
        }
        remove
        {
            if (value is not null)
            {
                _updateFrame -= value;
                RuntimeRenderingHostServices.Scheduling.UnsubscribeUpdateFrame(value);
            }
        }
    }

    public event Action? PostUpdateFrame
    {
        add
        {
            if (value is not null)
            {
                _postUpdateFrame += value;
                RuntimeRenderingHostServices.Scheduling.SubscribePostUpdateFrame(value);
            }
        }
        remove
        {
            if (value is not null)
            {
                _postUpdateFrame -= value;
                RuntimeRenderingHostServices.Scheduling.UnsubscribePostUpdateFrame(value);
            }
        }
    }

    public event Action? CollectVisible
    {
        add
        {
            if (value is not null)
            {
                _collectVisible += value;
                RuntimeRenderingHostServices.Scheduling.SubscribeViewportCollectVisible(value);
            }
        }
        remove
        {
            if (value is not null)
            {
                _collectVisible -= value;
                RuntimeRenderingHostServices.Scheduling.UnsubscribeViewportCollectVisible(value);
            }
        }
    }

    public event Action? SwapBuffers
    {
        add
        {
            if (value is not null)
            {
                _swapBuffers += value;
                RuntimeRenderingHostServices.Scheduling.SubscribeViewportSwapBuffers(value);
            }
        }
        remove
        {
            if (value is not null)
            {
                _swapBuffers -= value;
                RuntimeRenderingHostServices.Scheduling.UnsubscribeViewportSwapBuffers(value);
            }
        }
    }

    public event Action? RenderFrame
    {
        add
        {
            if (value is not null)
            {
                _renderFrame += value;
                RuntimeRenderingHostServices.Scheduling.SubscribeRenderFrame(value);
            }
        }
        remove
        {
            if (value is not null)
            {
                _renderFrame -= value;
                RuntimeRenderingHostServices.Scheduling.UnsubscribeRenderFrame(value);
            }
        }
    }

    internal void RebindHost(
        IRuntimeRenderingHostServices previous,
        IRuntimeRenderingHostServices current)
    {
        Rebind(_updateFrame, previous.UnsubscribeUpdateFrame, current.SubscribeUpdateFrame);
        Rebind(_postUpdateFrame, previous.UnsubscribePostUpdateFrame, current.SubscribePostUpdateFrame);
        Rebind(_collectVisible, previous.UnsubscribeViewportCollectVisible, current.SubscribeViewportCollectVisible);
        Rebind(_swapBuffers, previous.UnsubscribeViewportSwapBuffers, current.SubscribeViewportSwapBuffers);
        Rebind(_renderFrame, previous.UnsubscribeRenderFrame, current.SubscribeRenderFrame);
    }

    private static void Rebind(Action? handlers, Action<Action> unsubscribe, Action<Action> subscribe)
    {
        if (handlers is null)
            return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            Action callback = (Action)handler;
            unsubscribe(callback);
            subscribe(callback);
        }
    }
}
