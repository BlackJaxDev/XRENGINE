using System.Threading;
using XREngine.Data.Core;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Components.Lights;

public abstract partial class AdvancedOffscreenTextureCaptureComponent
{
    private int _captureQueued;
    private long _captureVersion;
    private string? _lastCaptureFailure;
    private long _captureRequestGeneration;
    private long _attachedCaptureGeneration;
    private bool _queuedCaptureAuthored;
    private XRWindow? _captureWindow;

    /// <summary>Monotonic count of successfully authored and GPU-completed outputs.</summary>
    [RuntimeOnly]
    [YamlIgnore]
    public long CaptureVersion
    {
        get => Interlocked.Read(ref _captureVersion);
        private set => SetField(ref _captureVersion, value);
    }

    /// <summary>Whether a queued refresh is waiting for authoring or GPU completion.</summary>
    [RuntimeOnly]
    [YamlIgnore]
    public bool IsCaptureQueued => Volatile.Read(ref _captureQueued) != 0;

    /// <summary>Returns cold-path authoring and submission diagnostics without polling the GPU.</summary>
    public object GetCaptureDiagnostics()
    {
        lock (_publicationSync)
            return new
        {
            CaptureVersion,
            IsCaptureQueued,
            LastCaptureFailure,
            resourcesDirty = _resourcesDirty,
            quarantined = _quarantined,
            writerAuthored = _writerAuthored,
            writerSubmission = _writerFence?.SubmissionStatus.ToString(),
            writerPackageGeneration = _writerPackageGeneration,
            outputId = _outputIdentity,
            viewportIdentity = _viewport?.FrameOutputIdentity,
            pipelineIdentity = _viewport?.RenderPipelineInstance.InstanceId,
            profile = (_viewport?.RenderPipeline as AdvancedRenderPipeline)?.OffscreenProfile,
            outputFormat = _outputTexture?.SizedInternalFormat.ToString(),
            publicationReferences = Volatile.Read(ref _publicationReferences),
            canonicalPublished = _canonicalLifetime?.IsPublished ?? false,
            canonicalReferences = _canonicalLifetime?.ReferenceCount ?? 0,
            retirementRequested = _retirementRequested,
            releaseQueued = _releaseQueued,
            allocatedWidth = _allocatedWidth,
            allocatedHeight = _allocatedHeight,
        };
    }

    /// <summary>Last terminal queued-capture failure; clean admission deferrals keep waiting.</summary>
    [RuntimeOnly]
    [YamlIgnore]
    public string? LastCaptureFailure
    {
        get => _lastCaptureFailure;
        private set => SetField(ref _lastCaptureFailure, value);
    }

    /// <summary>
    /// Diagnostic identity of the completed texture. GPU consumers must retain
    /// TryAcquireCompletedOutput's lease instead of treating this ID as ownership.
    /// </summary>
    [RuntimeOnly]
    [YamlIgnore]
    public Guid? CompletedOutputTextureId
    {
        get
        {
            lock (_publicationSync)
                return !_retirementRequested && !_writeInProgress && !HasPendingWriter && _hasCompletedCapture && !_quarantined
                    ? _outputTexture?.ID : null;
        }
    }

    /// <summary>Coalesces a refresh request and polls its exact writer on the render thread.</summary>
    public void QueueCapture()
    {
        long generation;
        lock (_publicationSync)
        {
            if (_retirementRequested || IsDestroyed)
            {
                LastCaptureFailure = "The capture owner is inactive or destroyed.";
                return;
            }
            if (_captureQueued != 0)
                return;
            generation = checked(++_captureRequestGeneration);
            Volatile.Write(ref _captureQueued, 1);
        }
        // One closure per explicit refresh keeps a stale queued attach from
        // becoming a later request after cancellation or reactivation.
        RuntimeEngine.AddRenderThreadCoroutine(() => AttachQueuedCapture(generation),
            $"{GetType().Name}.Capture", RenderThreadJobKind.RenderPipelineResource);
    }

    private bool AttachQueuedCapture(long generation)
    {
        lock (_publicationSync)
        {
            if (!IsCurrentCaptureRequest(generation))
                return true;
            var world = World;
            if (_retirementRequested || IsDestroyed || !IsActiveInHierarchy || world is null)
                return FinishQueuedCapture(generation, "The capture owner is inactive or destroyed.");
            // Subscribe under the same gate as cancellation. Only the render
            // thread touches window callbacks; cancellation schedules detachment.
            DetachCaptureWindow();
            foreach (XRWindow window in RuntimeEngine.Windows)
            {
                if (!ReferenceEquals(window.TargetWorldInstance, world.GetRenderWorld()))
                    continue;
                _captureWindow = window;
                _attachedCaptureGeneration = generation;
                _queuedCaptureAuthored = false;
                window.RenderViewportsCallback += RunQueuedCapture;
                return true;
            }
            return FinishQueuedCapture(generation, "No rendering window hosts the capture owner's world.");
        }
    }

    private void RunQueuedCapture()
        => AdvanceQueuedCapture();

    private bool AdvanceQueuedCapture()
    {
        long generation = _attachedCaptureGeneration;
        try
        {
            lock (_publicationSync)
                if (!IsCurrentCaptureRequest(generation) || _retirementRequested)
                    return FinishQueuedCapture(generation, "The capture request was cancelled.");
            if (!IsActiveInHierarchy || IsDestroyed || World is null)
                return FinishQueuedCapture(generation, "The capture owner is inactive or destroyed.");
            if (_quarantined)
                return FinishQueuedCapture(generation, "The capture owner has no trustworthy GPU completion.");
            if (HasPendingWriter)
            {
                bool completed = TryCompleteCapture();
                if (completed && _queuedCaptureAuthored)
                    return FinishQueuedCapture(generation, null);
                // A writer admitted before cancellation must settle, but does
                // not satisfy a new request with a different pose or extent.
                if (!HasPendingWriter)
                    _queuedCaptureAuthored = false;
                return false;
            }
            LastCaptureFailure = null;
            _queuedCaptureAuthored = TryCapture();
            return false;
        }
        catch (Exception exception)
        {
            Debug.RenderingWarning($"{GetType().Name} queued capture failed: {exception.Message}");
            return FinishQueuedCapture(generation, exception.Message);
        }
    }

    private bool IsCurrentCaptureRequest(long generation)
        => _captureQueued != 0 && _captureRequestGeneration == generation;

    private void CancelQueuedCapture()
    {
        lock (_publicationSync)
        {
            checked { ++_captureRequestGeneration; }
            Volatile.Write(ref _captureQueued, 0);
        }
        RuntimeEngine.EnqueueMainThreadTask(DetachCancelledCapture,
            $"{GetType().Name}.CancelCapture", RenderThreadJobKind.RenderPipelineResource);
    }

    private void DetachCancelledCapture()
    {
        lock (_publicationSync)
            if (!IsCurrentCaptureRequest(_attachedCaptureGeneration))
                DetachCaptureWindow();
    }

    private bool FinishQueuedCapture(long generation, string? failure)
    {
        lock (_publicationSync)
        {
            if (_attachedCaptureGeneration == generation)
                DetachCaptureWindow();
            if (IsCurrentCaptureRequest(generation))
            {
                LastCaptureFailure = failure;
                Volatile.Write(ref _captureQueued, 0);
            }
        }
        return true;
    }

    private void DetachCaptureWindow()
    {
        if (_captureWindow is { } window)
        {
            window.RenderViewportsCallback -= RunQueuedCapture;
            _captureWindow = null;
        }
        _attachedCaptureGeneration = 0;
        _queuedCaptureAuthored = false;
    }
}
