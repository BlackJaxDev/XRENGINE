using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private WebGpuAdvancedSceneResidency? _advancedSceneResidency;

    internal int MaximumAdvancedStorageBytes
    {
        get
        {
            if (DeviceCapabilities is not { } capabilities ||
                !capabilities.Limits.TryGetValue("maxStorageBufferBindingSize", out long binding) ||
                !capabilities.Limits.TryGetValue("maxBufferSize", out long buffer))
                throw new InvalidOperationException("WebGPU.Advanced.StorageLimitsMissing: canonical arena limits are unavailable.");
            return checked((int)(Math.Min(256L * 1024 * 1024, Math.Min(binding, buffer)) & ~15L));
        }
    }

    internal bool CanStageAdvancedStorage(int sceneBytes, int geometryBytes)
        => CanStageEngineStorageUploads((long)sceneBytes + geometryBytes,
            (sceneBytes == 0 ? 0 : 1) + (geometryBytes == 0 ? 0 : 1));

    internal bool TryAcquireAdvancedScene(BackendReadyFramePackage package, uint currentDeformationBytes, uint previousDeformationBytes,
        out WebGpuAdvancedSceneSlot? slot, out string reason)
    {
        RequireReady();
        if (!_engineRecording)
            throw new InvalidOperationException("WebGPU.Advanced.FrameRequired: canonical publication acquisition requires an active engine frame.");
        if (RuntimeEngine.Rendering.State.RenderingWorld is { } world)
        {
            BrowserWebGpuQualitySettings quality = RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality;
            int directionalCount = world.Lights.DynamicDirectionalLights.Count;
            int pointCount = world.Lights.DynamicPointLights.Count;
            int spotCount = world.Lights.DynamicSpotLights.Count;
            if (directionalCount > quality.MaxDirectionalLights || pointCount > quality.MaxPointLights ||
                spotCount > quality.MaxSpotLights)
                throw new NotSupportedException($"WebGPU.Quality.LightCountExceeded: authored lights {directionalCount}/{pointCount}/{spotCount} exceed selected browser limits {quality.MaxDirectionalLights}/{quality.MaxPointLights}/{quality.MaxSpotLights} (directional/point/spot).");
        }
        if (_advancedSceneResidency is not { } residency)
        {
            residency = new WebGpuAdvancedSceneResidency(this);
            SetField(ref _advancedSceneResidency, residency, publishNotifications: false);
            ReclaimAdvancedSceneSlots();
        }
        return residency.TryAcquire(package, _engineFrameSequence, currentDeformationBytes, previousDeformationBytes, out slot, out reason);
    }

    private void ReclaimAdvancedSceneSlots()
    {
        if (_advancedSceneResidency is null && !_advancedReservationsInitialized) return;
        _advancedSceneResidency?.Reclaim(_engineCompletedSequence);
        ReclaimAdvancedReservations(_engineCompletedSequence);
    }

    private void EndAdvancedSceneRecording(bool submitted)
    {
        foreach (WebGpuAdvancedVisibilityOutput output in _advancedVisibilityOutputs.Values)
            output.EndRecording(_engineFrameSequence, submitted);
        _advancedSceneResidency?.EndRecording(_engineFrameSequence, submitted);
        EndAdvancedReservationRecording(submitted);
    }

    private void DisposeAdvancedSceneResidency()
    {
        _advancedSceneResidency?.Dispose();
        SetField(ref _advancedSceneResidency, null, publishNotifications: false);
    }
}
