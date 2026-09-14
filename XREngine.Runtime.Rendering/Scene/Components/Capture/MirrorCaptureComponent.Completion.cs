using XREngine.Data.Core;

namespace XREngine.Components.Lights;

public partial class MirrorCaptureComponent
{
    /// <summary>Cold-path ownership diagnostics; never polls a GPU fence off the render thread.</summary>
    public object GetMirrorCaptureDiagnostics()
    {
        lock (_mirrorLifetimeSync)
        {
            object?[] views = new object?[CameraCapacity];
            for (int i = 0; i < views.Length; ++i)
                if (_views[i] is { } view)
                    views[i] = new
                    {
                        cameraIdentity = view.Camera.RenderIdentity,
                        publishedTexture = view.Published?.Texture?.ID,
                        first = view.Slots[0].GetDiagnostics(),
                        second = view.Slots[1].GetDiagnostics(),
                    };
            return new
            {
                captureVersion = _completedCaptures,
                cameraCapacity = CameraCapacity,
                slotsPerCamera = CaptureSlotsPerCamera,
                retirementRequested = _retirementRequested,
                releaseQueued = _retirementQueued,
                lastCaptureFailure = _lastCaptureFailure,
                outputTexture = EnvironmentTexture?.ID,
                outputWidth = EnvironmentTexture?.Width,
                outputHeight = EnvironmentTexture?.Height,
                views,
            };
        }
    }
    private void RequestMirrorRetirement()
    {
        lock (_mirrorLifetimeSync)
        {
            _retirementRequested = true;
            if (_retirementQueued)
                return;
            _retirementQueued = true;
        }
        RuntimeEngine.AddRenderThreadCoroutine(AdvanceMirrorRetirement,
            "MirrorCapture.Retire", RenderThreadJobKind.RenderPipelineResource);
    }
    private bool AdvanceMirrorRetirement()
    {
        lock (_mirrorLifetimeSync)
        {
            if (!_retirementRequested)
            {
                // A reactivation after partial retirement rebuilds only after
                // all old private owners have drained. It cannot adopt inactive slots.
                _configurationDirty = true;
                _retirementQueued = false;
                RuntimeEngine.AddRenderThreadCoroutine(AttachCaptureWindow,
                    "MirrorCapture.Reattach", RenderThreadJobKind.RenderPipelineResource);
                return true;
            }
            if (_captureWindow is { } window)
            {
                window.RenderViewportsCallback -= AdvanceMirrorCaptures;
                _captureWindow = null;
            }
            WithdrawAllViews();
            if (!RetireViews())
                return false;
            Array.Clear(_requestedCameras);
            _retirementQueued = false;
            _capacityWarning = false;
            return true;
        }
    }
    private void WithdrawAllViews()
    {
        _nativeDisplay.Enabled = false;
        for (int i = 0; i < CameraCapacity; ++i)
        {
            _nativeMaterial.WithdrawView(i);
            if (_views[i] is not { } view)
                continue;
            view.Published = null;
            for (int j = 0; j < CaptureSlotsPerCamera; ++j)
                view.Slots[j].Capture.WithdrawPublishedOutput();
        }
    }
    private bool RetireViews()
    {
        bool retired = true;
        for (int i = 0; i < CameraCapacity; ++i)
        {
            if (_views[i] is not { } view)
                continue;
            bool viewRetired = true;
            for (int j = 0; j < CaptureSlotsPerCamera; ++j)
            {
                MirrorCaptureSlot slot = view.Slots[j];
                slot.SettleReaders();
                slot.Node.IsActiveSelf = false;
                if (!slot.Capture.ResourcesRetired || !slot.ReadersRetired)
                    viewRetired = false;
            }
            if (!viewRetired)
            {
                retired = false;
                continue;
            }
            for (int j = 0; j < CaptureSlotsPerCamera; ++j)
                view.Slots[j].Node.Destroy();
            _views[i] = null;
        }
        return retired;
    }
}
