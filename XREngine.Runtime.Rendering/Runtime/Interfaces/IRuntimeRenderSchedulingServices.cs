using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Required frame-callback and render/application/window-thread scheduling services.
/// </summary>
public interface IRuntimeRenderSchedulingServices
{

    /// <summary>
    /// Subscribes a callback to the host viewport swap-buffers frame event.
    /// </summary>
    void SubscribeViewportSwapBuffers(Action swapBuffers);

    /// <summary>
    /// Unsubscribes a callback from the host viewport swap-buffers frame event.
    /// </summary>
    void UnsubscribeViewportSwapBuffers(Action swapBuffers);

    /// <summary>
    /// Subscribes a callback to the host viewport collect-visible frame event.
    /// </summary>
    void SubscribeViewportCollectVisible(Action collectVisible);

    /// <summary>
    /// Unsubscribes a callback from the host viewport collect-visible frame event.
    /// </summary>
    void UnsubscribeViewportCollectVisible(Action collectVisible);

    /// <summary>
    /// Subscribes a callback to the host post-collect-visible frame event.
    /// </summary>
    void SubscribeViewportPostCollectVisible(Action postCollectVisible);

    /// <summary>
    /// Unsubscribes a callback from the host post-collect-visible frame event.
    /// </summary>
    void UnsubscribeViewportPostCollectVisible(Action postCollectVisible);

    void SubscribeUpdateFrame(Action callback);
    void UnsubscribeUpdateFrame(Action callback);
    void SubscribePostUpdateFrame(Action callback);
    void UnsubscribePostUpdateFrame(Action callback);
    void SubscribeRenderFrame(Action callback);
    void UnsubscribeRenderFrame(Action callback);

    /// <summary>
    /// Subscribes paired window swap and render callbacks to the host frame timer.
    /// </summary>
    void SubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame);

    /// <summary>
    /// Unsubscribes paired window swap and render callbacks from the host frame timer.
    /// </summary>
    void UnsubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame);

    /// <summary>
    /// Attempts to dispatch one complete host frame while the native window thread
    /// is inside an interactive resize modal loop.
    /// </summary>
    bool TryDispatchInteractiveResizeFrame();

    /// <summary>
    /// Subscribes a callback to host play-mode transition notifications that affect rendering.
    /// </summary>
    void SubscribePlayModeTransitions(Action callback);

    /// <summary>
    /// Unsubscribes a callback from host play-mode transition notifications.
    /// </summary>
    void UnsubscribePlayModeTransitions(Action callback);

    /// <summary>
    /// Queues work for execution on the host render thread.
    /// </summary>
    void EnqueueRenderThreadTask(Action task);

    /// <summary>
    /// Queues work for execution on the host render thread with explicit render-thread intent.
    /// </summary>
    void EnqueueRenderThreadTask(Action task, RenderThreadJobKind renderThreadKind);

    /// <summary>
    /// Queues named work for execution on the host render thread.
    /// </summary>
    void EnqueueRenderThreadTask(Action task, string reason);

    /// <summary>
    /// Queues named work for execution on the host render thread with explicit render-thread intent.
    /// </summary>
    void EnqueueRenderThreadTask(Action task, string reason, RenderThreadJobKind renderThreadKind);

    /// <summary>
    /// Invokes work on the host render thread and returns its result.
    /// </summary>
    T InvokeRenderThreadTask<T>(Func<T> task, string reason, RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown);

    /// <summary>
    /// Queues work for execution on the host application/update thread. Use this for
    /// non-GPU work (scene/editor/networking) so it does not stall the render thread.
    /// </summary>
    void EnqueueAppThreadTask(Action task);

    /// <summary>
    /// Queues named work for execution on the host application/update thread.
    /// </summary>
    void EnqueueAppThreadTask(Action task, string reason);

    /// <summary>
    /// Queues native window work on the owning window/event thread when a split
    /// pump host is active. Hosts without a split pump execute inline.
    /// </summary>
    void EnqueueWindowThreadTask(IRuntimeRenderWindowHost window, Action task, string reason)
        => task();

    /// <summary>
    /// Invokes native window work on the owning window/event thread and returns a
    /// result. Hosts without a split pump execute inline.
    /// </summary>
    T InvokeWindowThreadTask<T>(IRuntimeRenderWindowHost window, Func<T> task, string reason)
        => task();

    /// <summary>
    /// Queues a render-thread coroutine that returns <see langword="true"/> when it should continue.
    /// </summary>
    void EnqueueRenderThreadCoroutine(Func<bool> task);

    /// <summary>
    /// Queues a render-thread coroutine with explicit render-thread intent.
    /// </summary>
    void EnqueueRenderThreadCoroutine(Func<bool> task, RenderThreadJobKind renderThreadKind);

    /// <summary>
    /// Queues a named render-thread coroutine that returns <see langword="true"/> when it should continue.
    /// </summary>
    void EnqueueRenderThreadCoroutine(Func<bool> task, string reason);

    /// <summary>
    /// Queues a named render-thread coroutine with explicit render-thread intent.
    /// </summary>
    void EnqueueRenderThreadCoroutine(Func<bool> task, string reason, RenderThreadJobKind renderThreadKind);

    /// <summary>
    /// Processes pending render-thread tasks through the host task pump.
    /// </summary>
    void ProcessRenderThreadTasks();

    /// <summary>
    /// Signals that the active render frame has finished consuming render-side command buffers.
    /// </summary>
    void MarkRenderFrameReadyForCollect(IRuntimeRenderWindowHost window);
}
