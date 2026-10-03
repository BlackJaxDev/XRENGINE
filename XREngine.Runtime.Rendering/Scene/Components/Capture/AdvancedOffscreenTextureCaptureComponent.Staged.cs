using XREngine.Data.Rendering;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using StagedCaptureState = XREngine.Components.Lights.EStagedOffscreenCaptureState;

namespace XREngine.Components.Lights;

public abstract partial class AdvancedOffscreenTextureCaptureComponent
{
    private EStagedOffscreenCaptureState _stagedState;
    private bool _stagedCancelled;
    private RenderOutputRequest _stagedOutput;
    private FrameOutputPacingDecision _stagedPacing;

    internal bool HasStagedCapture
    {
        get
        {
            lock (_publicationSync)
                return _stagedState != StagedCaptureState.Idle;
        }
    }

    /// <summary>
    /// Reserves one private capture for collection on the host's collect thread.
    /// The reservation owns its target until submission or cancellation finishes.
    /// </summary>
    internal bool TryStageSynchronizedCapture()
    {
        if (!RuntimeEngine.IsRenderThread)
            throw new InvalidOperationException("Capture preparation requires the render thread.");
        lock (_publicationSync)
        {
            if (_retirementRequested || _quarantined || _writeInProgress || HasPendingWriter ||
                _publicationReferences != 0 || _canonicalLifetime is { CanWrite: false } || World is null)
                return false;
            _writeInProgress = true;
        }

        bool writeStarted = false;
        bool ownershipTransferred = false;
        try
        {
            EnsureResourcesCore();
            lock (_publicationSync)
            {
                if (_retirementRequested || _resourcesDirty || _viewport is null ||
                    _frameBuffer is null || _outputTexture is null ||
                    _canonicalLifetime is null || !_canonicalLifetime.TryBeginWrite())
                    return false;
                writeStarted = true;
                _hasCompletedCapture = false;
                UpdateCaptureCamera();
                _stagedOutput = CreateOutputRequest();
                _stagedPacing = FrameOutputPacingDecision.Due(
                    _stagedOutput.ViewKind, _stagedOutput.OutputKind, _stagedOutput.FrameId)
                    with { Request = _stagedOutput };
                _stagedCancelled = false;
                _stagedState = StagedCaptureState.Requested;
                ownershipTransferred = true;
                LastCaptureFailure = null;
                return true;
            }
        }
        catch (Exception exception)
        {
            LastCaptureFailure = exception.Message;
            Debug.RenderingWarning($"{GetType().Name} capture preparation failed: {exception.Message}");
            return false;
        }
        finally
        {
            lock (_publicationSync)
            {
                if (!ownershipTransferred)
                {
                    if (writeStarted)
                        _canonicalLifetime?.EndWrite(0);
                    _writeInProgress = false;
                }
            }
        }
    }

    /// <summary>Collects only during the canonical viewport collection callback.</summary>
    internal void CollectStagedCapture()
    {
        XRViewport viewport;
        FrameOutputPacingDecision pacing;
        lock (_publicationSync)
        {
            if (_stagedState != StagedCaptureState.Requested)
                return;
            if (_stagedCancelled || _retirementRequested)
            {
                ResetStagedCaptureCore();
                return;
            }
            viewport = _viewport!;
            pacing = _stagedPacing;
            _stagedState = StagedCaptureState.Collecting;
        }

        bool previousCapturePass = RuntimeEngine.Rendering.State.IsSceneCapturePass;
        RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
        bool mirror = OffscreenIntent.ViewIntent == ERenderPipelineOffscreenViewIntent.Mirror;
        if (mirror)
            RuntimeEngine.Rendering.State.PushMirrorPass();
        try
        {
            viewport.CollectVisible(collectMirrors: false, frameOutputPacing: pacing);
            lock (_publicationSync)
            {
                if (_stagedCancelled || _retirementRequested)
                    ResetStagedCaptureCore();
                else
                    _stagedState = StagedCaptureState.Collected;
            }
        }
        catch (Exception exception)
        {
            lock (_publicationSync)
            {
                LastCaptureFailure = exception.Message;
                ResetStagedCaptureCore();
            }
            Debug.RenderingWarning($"{GetType().Name} capture collection failed: {exception.Message}");
        }
        finally
        {
            if (mirror)
                RuntimeEngine.Rendering.State.PopMirrorPass();
            RuntimeEngine.Rendering.State.IsSceneCapturePass = previousCapturePass;
        }
    }

    /// <summary>Publishes private commands after the world command swap has completed.</summary>
    internal void SwapStagedCapture()
    {
        XRViewport viewport;
        lock (_publicationSync)
        {
            if (_stagedState != StagedCaptureState.Collected)
                return;
            if (_stagedCancelled || _retirementRequested)
            {
                ResetStagedCaptureCore();
                return;
            }
            viewport = _viewport!;
            _stagedState = StagedCaptureState.Swapping;
        }

        try
        {
            viewport.SwapBuffers(allowScreenSpaceUISwap: false);
            lock (_publicationSync)
            {
                if (_stagedCancelled || _retirementRequested)
                    ResetStagedCaptureCore();
                else
                    _stagedState = StagedCaptureState.Published;
            }
        }
        catch (Exception exception)
        {
            lock (_publicationSync)
            {
                LastCaptureFailure = exception.Message;
                ResetStagedCaptureCore();
            }
            Debug.RenderingWarning($"{GetType().Name} capture swap failed: {exception.Message}");
        }
    }

    /// <summary>Submits a previously published exact package before window present.</summary>
    internal bool TryRenderStagedCapture()
    {
        if (!RuntimeEngine.IsRenderThread)
            throw new InvalidOperationException("Capture submission requires the render thread.");
        XRViewport viewport;
        XRFrameBuffer target;
        RenderOutputRequest output;
        lock (_publicationSync)
        {
            if (_stagedState != StagedCaptureState.Published)
                return false;
            if (_stagedCancelled || _retirementRequested || _resourcesDirty)
            {
                ResetStagedCaptureCore();
                return false;
            }
            viewport = _viewport!;
            target = _frameBuffer!;
            output = _stagedOutput;
            _stagedState = StagedCaptureState.Rendering;
        }

        bool previousCapturePass = RuntimeEngine.Rendering.State.IsSceneCapturePass;
        RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
        bool mirror = OffscreenIntent.ViewIntent == ERenderPipelineOffscreenViewIntent.Mirror;
        if (mirror)
            RuntimeEngine.Rendering.State.PushMirrorPass();
        try
        {
            bool authored = viewport.TryRenderWithCompletion(
                target, in output, out XRGpuFence? fence,
                out ERenderOutputCompletionAuthoringDisposition disposition);
            lock (_publicationSync)
            {
                _writerFence = fence;
                _writerAuthored = authored;
                _writerPipeline = viewport.RenderPipelineInstance;
                _writerCommands = _writerPipeline.ActiveMeshRenderCommands;
                _writerPackageGeneration = _writerCommands.RenderingBackendReadyPackage.PackageGeneration;
                _writerRenderer = AbstractRenderer.Current;
                if (fence is null &&
                    disposition == ERenderOutputCompletionAuthoringDisposition.UnfencedAfterAuthoring)
                    QuarantineResources();
                else if (fence is null)
                {
                    ReleaseCompletedWriterPackage();
                    viewport.ResetUnsubmittedCapture();
                }
                if (fence is null && !_quarantined)
                    _canonicalLifetime?.EndWrite(0);
                _stagedState = StagedCaptureState.Idle;
                _stagedCancelled = false;
                _writeInProgress = false;
            }
            return authored && fence is not null;
        }
        catch (Exception exception)
        {
            lock (_publicationSync)
            {
                LastCaptureFailure = exception.Message;
                // An exception during backend authoring has no reliable proof
                // that the target was not written. Keep its resources owned.
                QuarantineResources();
                _stagedState = StagedCaptureState.Idle;
                _writeInProgress = false;
            }
            Debug.RenderingWarning($"{GetType().Name} capture submission failed: {exception.Message}");
            return false;
        }
        finally
        {
            if (mirror)
                RuntimeEngine.Rendering.State.PopMirrorPass();
            RuntimeEngine.Rendering.State.IsSceneCapturePass = previousCapturePass;
        }
    }

    /// <summary>Rejects an unsubmitted capture after a cut, disable, or retirement.</summary>
    internal void CancelStagedCapture()
    {
        lock (_publicationSync)
        {
            if (_stagedState == StagedCaptureState.Idle)
                return;
            _stagedCancelled = true;
            if (_stagedState is StagedCaptureState.Requested or
                StagedCaptureState.Collected or StagedCaptureState.Published)
                ResetStagedCaptureCore();
        }
    }

    private void ResetStagedCaptureCore()
    {
        _viewport?.ResetUnsubmittedCapture();
        if (!_quarantined)
            _canonicalLifetime?.EndWrite(0);
        _stagedState = StagedCaptureState.Idle;
        _stagedCancelled = false;
        _writeInProgress = false;
    }
}
