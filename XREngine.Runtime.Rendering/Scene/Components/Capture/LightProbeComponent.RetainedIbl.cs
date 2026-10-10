namespace XREngine.Components.Capture.Lights;

public partial class LightProbeComponent
{
    /// <summary>Releases a borrowed producer synchronously; publication pins retain its guard without scheduling GPU teardown.</summary>
    protected void ReleaseRetainedIbl()
    {
        _activeIblOutput?.ReleaseProducer();
        SetField(ref _activeIblOutput, null, publishNotifications: false);
        IrradianceTexture = null;
        PrefilterTexture = null;
        IblTexturesValid = false;
        CaptureVersion = 0;
        SetField(ref _iblDestroyQueued, false, publishNotifications: false);
    }

    /// <summary>Restores dependent persistent setters and base-private setter state after target reflection hydration.</summary>
    protected void RestoreRetainedIblState(RetainedLightProbeIblProfile profile)
    {
        InfluenceSphereInnerRadius = 0;
        InfluenceSphereOuterRadius = profile.InfluenceSphereOuter;
        InfluenceSphereInnerRadius = profile.InfluenceSphereInner;
        InfluenceBoxOuterExtents = profile.InfluenceBoxOuter;
        InfluenceBoxInnerExtents = profile.InfluenceBoxInner;
        StreamedMipLevel = profile.SourceStreamedMipLevel;
        HdrEncoding = profile.SourceHdrEncoding;
    }

    /// <summary>Adopts target-owned decoded image references without inventing a GPU writer completion.</summary>
    protected void AdoptRetainedIbl(RetainedLightProbeIblProfile profile)
    {
        if (this is not PublishedRetainedLightProbeComponent || AutoCaptureOnActivate || RealtimeCapture)
            throw new NotSupportedException("WebGPU.RetainedProbe.CaptureRequested: a retained probe requires both authored capture switches disabled.");
        if (_activeIblOutput is { } active)
        {
            active.ValidateRetainedData();
            if (active.Provenance != ELightProbeIblProvenance.RetainedCookedData ||
                active.Generation != profile.SourceGeneration || !ReferenceEquals(active.Irradiance, profile.Irradiance) ||
                !ReferenceEquals(active.PrefilteredRadiance, profile.Prefilter))
                throw new InvalidOperationException("WebGPU.RetainedProbe.GenerationChanged: the active retained source closure differs from its target profile.");
            return;
        }
        if (_pendingIblOutput is not null)
            throw new InvalidOperationException("WebGPU.RetainedProbe.WriterConflict: a retained probe cannot adopt data while a GPU writer is pending.");
        SetField(ref _activeIblOutput, LightProbeIblOutputGeneration.RetainCookedData(profile), publishNotifications: false);
        IrradianceTexture = profile.Irradiance;
        PrefilterTexture = profile.Prefilter;
        CaptureVersion = profile.SourceGeneration;
        IblTexturesValid = true;
    }
}
