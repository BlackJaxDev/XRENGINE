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

    /// <summary>Preflights a complete set of copy records before any member enters the frame packet.</summary>
    internal bool CanStageEngineStorageUploads(long byteCount, int recordCount)
        => byteCount >= 0 && recordCount >= 0 && byteCount <= EngineStorageCapacity - _engineStorageBytes &&
           recordCount <= EngineMaximumUploads - _engineUploadCount;

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
        if (_engineFrameStatisticsActive && _engineFrameStatistics is { } statistics)
        {
            statistics.CompletionPollInteropCalls++;
            statistics.LastFrameCompletionPollInteropCalls++;
        }
        double completed = WebGpuImports.PollEngineFrameCompletion(_session);
        if (!double.IsFinite(completed) || completed < 0 || completed > _engineFrameSequence || completed != Math.Truncate(completed))
            throw new InvalidOperationException("WebGPU.Advanced.CompletionInvalid: the executor returned an invalid completion watermark.");
        _advancedSceneResidency?.Reclaim(checked((uint)completed));
        ReclaimAdvancedReservations(checked((uint)completed));
    }

    internal void CountAdvancedPreparationUpload(int bytes)
    {
        if (!_engineFrameStatisticsActive || _engineFrameStatistics is not { } statistics) return;
        statistics.AdvancedPreparationUploadInteropCalls++;
        statistics.AdvancedPreparationUploadBytes += bytes;
        statistics.LastFrameAdvancedPreparationUploadInteropCalls++;
        statistics.LastFrameAdvancedPreparationUploadBytes += bytes;
    }

    private void EndAdvancedSceneRecording(bool submitted)
    {
        _advancedSceneResidency?.EndRecording(_engineFrameSequence, submitted);
        EndAdvancedReservationRecording(submitted);
    }

    private void DisposeAdvancedSceneResidency()
    {
        _advancedSceneResidency?.Dispose();
        SetField(ref _advancedSceneResidency, null, publishNotifications: false);
    }
}
