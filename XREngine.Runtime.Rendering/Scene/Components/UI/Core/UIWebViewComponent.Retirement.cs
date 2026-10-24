using XREngine.Data.Rendering;
using XREngine.Components;

namespace XREngine.Rendering.UI;

public sealed partial class UIWebViewComponent
{
    internal void RenderWebFrameForTest() => RenderFrame();

    private bool SetWebProducerFailure(string failure)
    {
        if (string.Equals(_webProducerFailure, failure, StringComparison.Ordinal))
            return false;
        Volatile.Write(ref _webProducerFailure, failure);
        return true;
    }

    private XRWindow? ResolveWebOwnerWindow()
    {
        if (Transform is not UITransform transform || transform.GetCanvasTransform() is not { } canvas)
            return null;

        XRWindow? match = null;
        if (canvas.DrawSpace == ECanvasDrawSpace.Screen)
        {
            UICanvasComponent? ownerCanvas = transform.GetCanvasComponent();
            if (ownerCanvas is null)
                return null;
            var windows = RuntimeEngine.Windows;
            for (int w = 0; w < windows.Count; w++)
            {
                XRWindow window = windows[w];
                var viewports = window.Viewports;
                for (int v = 0; v < viewports.Count; v++)
                {
                    XRViewport viewport = viewports[v];
                    if (!viewport.TryGetActiveScreenSpaceUserInterface(out var bound) ||
                        !ReferenceEquals(bound, ownerCanvas))
                        continue;
                    if (match is not null && !ReferenceEquals(match, window))
                        return null;
                    match = window;
                }
            }
            return match;
        }

        var renderWorld = SceneNode?.World.GetRenderWorld();
        if (renderWorld is null)
            return null;
        var worldWindows = RuntimeEngine.Windows;
        for (int w = 0; w < worldWindows.Count; w++)
        {
            XRWindow window = worldWindows[w];
            var viewports = window.Viewports;
            for (int v = 0; v < viewports.Count; v++)
            {
                XRViewport viewport = viewports[v];
                if (!ReferenceEquals(viewport.World, renderWorld))
                    continue;
                if (match is not null && !ReferenceEquals(match, window))
                    return null;
                match = window;
            }
        }
        return match;
    }

    private void RetireWebResources(IWebRendererBackend backend)
    {
        lock (_webProducerGate)
        {
            XRDataBuffer<byte>?[] buffers = [_pboBuffers[0], _pboBuffers[1]];
            _pboBuffers[0] = null;
            _pboBuffers[1] = null;
            _currentPboIndex = 0;
            _webRetiredResources.Add(new UIWebRetiredResources(backend, buffers, ++_webBackendGeneration));
        }
    }

    private void RetireUnusedWebBackend(IWebRendererBackend backend)
    {
        lock (_webProducerGate)
        {
            if (ReferenceEquals(_backend, backend))
                return;
            foreach (UIWebRetiredResources retired in _webRetiredResources)
                if (ReferenceEquals(retired.Backend, backend) && !retired.BackendDisposed)
                    return;
            _webRetiredResources.Add(new UIWebRetiredResources(backend,
                [null, null], _webBackendGeneration));
        }
    }

    private void QueueWebRetirements()
    {
        List<UIWebRetiredResources>? ready = null;
        lock (_webProducerGate)
        {
            if (_webProducerActive != 0 || _webRetiredResources.Count == 0)
                return;
            foreach (UIWebRetiredResources retired in _webRetiredResources)
            {
                if (retired.IsQueued || retired.IsClaimed || retired.IsQuarantined)
                    continue;
                (ready ??= []).Add(retired);
            }
        }
        if (ready is null)
            return;
        foreach (UIWebRetiredResources retired in ready)
        {
            lock (_webProducerGate)
            {
                if (retired.IsQueued || retired.IsClaimed || retired.IsQuarantined)
                    continue;
                retired.IsQueued = true;
            }
            try
            {
                RuntimeEngine.EnqueueRenderThreadTask(() => ReleaseWebRetirement(retired),
                    "UI.Web.RetireResources", RenderThreadJobKind.RenderPipelineResource);
            }
            catch
            {
                lock (_webProducerGate)
                    retired.IsQueued = false;
                throw;
            }
        }
    }

    private bool IsCurrentWebBackend(IWebRendererBackend backend, long generation)
    {
        if (!ReferenceEquals(_backend, backend) || generation != _webBackendGeneration ||
            Volatile.Read(ref _webProducerDestroyed) != 0 || !IsActive)
            return false;
        if (backend is IWebRendererBackendOwnerScope ownerScoped)
            ownerScoped.AssertCurrentOwner();
        return true;
    }

    private bool IsWebBackendBlocked(IWebRendererBackend backend)
    {
        if (ReferenceEquals(_webBackendDisposing, backend))
            return true;
        foreach (UIWebRetiredResources retired in _webRetiredResources)
            if (ReferenceEquals(retired.Backend, backend) &&
                (retired.IsQuarantined || (retired.IsClaimed && !retired.BackendDisposed)))
                return true;
        return false;
    }

    private void ReleaseWebRetirement(UIWebRetiredResources retired)
    {
        bool disposeBackend;
        lock (_webProducerGate)
        {
            if (retired.IsClaimed || !_webRetiredResources.Contains(retired))
                return;
            if (_webProducerActive != 0)
            {
                retired.IsQueued = false;
                return;
            }
            retired.IsClaimed = true;
            disposeBackend = !retired.BackendDisposed &&
                !(ReferenceEquals(_backend, retired.Backend) && IsActive &&
                  Volatile.Read(ref _webProducerDestroyed) == 0);
            if (disposeBackend)
                _webBackendDisposing = retired.Backend;
            else
                retired.BackendDisposed = true;
        }

        try
        {
            if (disposeBackend)
            {
                retired.Backend.Dispose();
                retired.BackendDisposed = true;
            }
        }
        catch (Exception exception)
        {
            string failure = $"UI.Web.RetirementDeferred: {exception.Message}";
            retired.Failure = failure;
            Debug.UIWarning(failure);
            retired.Quarantine();
        }
        finally
        {
            // PBOs belong to the old frame even if the backend instance is reused.
            if (!retired.BuffersDisposed)
            {
                bool allReleased = true;
                for (int i = 0; i < retired.Buffers.Length; i++)
                {
                    XRDataBuffer<byte>? buffer = retired.Buffers[i];
                    if (buffer is null)
                        continue;
                    try
                    {
                        buffer.Destroy();
                        retired.Buffers[i] = null;
                    }
                    catch (Exception exception)
                    {
                        allReleased = false;
                        string failure = $"UI.Web.BufferRetirementDeferred: {exception.Message}";
                        retired.Failure = failure;
                        Debug.UIWarning(failure);
                    }
                }
                retired.BuffersDisposed = allReleased;
                if (!allReleased)
                    retired.Quarantine();
            }
            lock (_webProducerGate)
            {
                if (ReferenceEquals(_webBackendDisposing, retired.Backend))
                    _webBackendDisposing = null;
                retired.IsClaimed = false;
                retired.IsQueued = false;
                if (retired.BackendDisposed && retired.BuffersDisposed)
                    _webRetiredResources.Remove(retired);
            }
        }
    }
}
