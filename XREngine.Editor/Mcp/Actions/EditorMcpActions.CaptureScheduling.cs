using System;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>
    /// Starts a diagnostic capture at its owning window's render boundary. Every
    /// scheduling, scope, and readback failure completes the request; cancellation
    /// removes a pending callback without interrupting GPU resource cleanup.
    /// </summary>
    private static async Task<T> CaptureAfterWindowRenderAsync<T>(
        XRWindow window,
        Action<AbstractRenderer, TaskCompletionSource<T>> beginCapture,
        CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        object gate = new();
        bool pending = true;
        Action? handler = null;

        handler = () =>
        {
            lock (gate)
            {
                if (!pending)
                    return;
                pending = false;
                window.PostRenderViewportsCallback -= handler;
            }

            try
            {
                token.ThrowIfCancellationRequested();
                if (completion.Task.IsCompleted)
                    return;
                AbstractRenderer renderer = AbstractRenderer.Current
                    ?? throw new InvalidOperationException("The capture window has no current renderer.");
                if (!ReferenceEquals(renderer, window.Renderer))
                    throw new InvalidOperationException("The capture callback ran on a different window's renderer.");
                beginCapture(renderer, completion);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                completion.TrySetCanceled(token);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var registration = timeout.Token.Register(() =>
        {
            lock (gate)
            {
                pending = false;
                window.PostRenderViewportsCallback -= handler;
            }
            if (token.IsCancellationRequested)
                completion.TrySetCanceled(token);
            else
                completion.TrySetException(new TimeoutException("The capture window did not complete the requested readback within 20 seconds."));
        });

        void Schedule()
        {
            lock (gate)
            {
                if (pending)
                    window.PostRenderViewportsCallback += handler;
            }
        }

        try
        {
            if (Engine.IsRenderThread)
                Schedule();
            else
                Engine.InvokeOnMainThread(Schedule, "MCP: Schedule window capture", executeNowIfAlreadyMainThread: true);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                pending = false;
                window.PostRenderViewportsCallback -= handler;
            }
        }
    }
}
