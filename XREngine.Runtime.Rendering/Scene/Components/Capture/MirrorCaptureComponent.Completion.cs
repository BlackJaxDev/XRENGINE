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
                        third = CaptureSlotsPerCamera > 2 ? view.Slots[2].GetDiagnostics() : null,
                        fourth = CaptureSlotsPerCamera > 3 ? view.Slots[3].GetDiagnostics() : null,
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
        UnregisterPortalSurfaceFromWorld();
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
                window.PreRenderViewportsCallback -= AdvanceMirrorCapturesFromPreRender;
                _captureWindow = null;
            }
            if (_beforeXrEyeRenderSubscribed)
            {
                RuntimeEngine.VRState.BeforeXrEyeRender -= AdvanceMirrorCapturesForXr;
                _beforeXrEyeRenderSubscribed = false;
            }
            _lastMirrorCaptureFrameId = ulong.MaxValue;
            WithdrawAllViews();
            if (!RetireViews())
                return false;
            Array.Clear(_requestedCameras);
            Array.Clear(_requestedCameraLastSeenFrame);
            Array.Clear(_requestedCameraIsStereoEye);
            Array.Clear(_slotRebindRequested);
            _retirementQueued = false;
            _capacityWarning = false;
            return true;
        }
    }
    private void WithdrawAllViews()
    {
        _nativeDisplay.Enabled = false;
        ClearStereoTransparentDisplays();
        for (int i = 0; i < CameraCapacity; ++i)
            WithdrawSingleView(i);
    }
    private void WithdrawSingleView(int index)
    {
        _nativeMaterial.WithdrawView(index);
        ClearStereoTransparentDisplays();
        if (_views[index] is not { } view)
            return;
        view.Published = null;
        for (int j = 0; j < CaptureSlotsPerCamera; ++j)
            view.Slots[j].Capture.WithdrawPublishedOutput();
    }
    private bool RetireViews()
    {
        bool retired = true;
        for (int i = 0; i < CameraCapacity; ++i)
            if (!RetireSingleView(i))
                retired = false;
        if (retired)
        {
            _sharedAdvancedPipeline?.Destroy();
            _sharedAdvancedPipeline = null;
            _sharedDefaultPipeline?.Destroy();
            _sharedDefaultPipeline = null;
        }
        return retired;
    }
    private bool RetireSingleView(int index)
    {
        if (_views[index] is not { } view)
            return true;
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
            return false;
        for (int j = 0; j < CaptureSlotsPerCamera; ++j)
            view.Slots[j].Node.Destroy();
        _views[index] = null;
        return true;
    }
}
