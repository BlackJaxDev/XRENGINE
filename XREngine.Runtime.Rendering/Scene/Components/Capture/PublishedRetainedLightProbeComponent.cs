using XREngine.Core.Files;

namespace XREngine.Components.Capture.Lights;

/// <summary>Browser-only reflection carrier; ordinary light-probe component persistence remains unchanged.</summary>
[CookedBinaryReflectionOnly]
public sealed class PublishedRetainedLightProbeComponent : LightProbeComponent, IPostCookedBinaryDeserialize
{
    private RetainedLightProbeIblProfile? _publishedRetainedIbl;
    private bool _decoded;

    public RetainedLightProbeIblProfile? PublishedRetainedIbl
    {
        get => _publishedRetainedIbl;
        set => SetField(ref _publishedRetainedIbl, value);
    }

    public void OnPostCookedBinaryDeserialize()
    {
        RetainedLightProbeIblProfile profile = PublishedRetainedIbl
            ?? throw new InvalidDataException("WebGPU.RetainedProbe.ProfileMissing: the published probe lacks retained-image provenance.");
        profile.RestoreDecodedImages();
        RestoreRetainedIblState(profile);
        AdoptRetainedIbl(profile);
        _decoded = true;
    }

    protected override bool ShouldInitializeCaptureResourcesOnActivate => false;

    protected override void InitializeForCapture()
        => throw new NotSupportedException("WebGPU.RetainedProbe.CaptureRequested: retained target data has no cubemap capture or prefilter producer.");

    protected override void OnDestroying()
    {
        ReleaseRetainedIbl();
        base.OnDestroying();
    }

    protected override void OnComponentActivated()
    {
        if (!_decoded || PublishedRetainedIbl is not { } profile)
            throw new InvalidOperationException("WebGPU.RetainedProbe.NotHydrated: activation requires complete target-cooked data.");
        AdoptRetainedIbl(profile);
        base.OnComponentActivated();
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        // These setters normally start an environment convolution while reflection
        // hydration is still attaching borrowed references. The target carrier
        // adopts its complete retained profile only in the post-deserialize hook.
        if (propName is nameof(EnvironmentTextureEquirect) or nameof(IrradianceResolution) or
            nameof(UseDirectCubemapIblGeneration) or nameof(HdrEncoding) or nameof(RealtimeCapture) or nameof(AutoCaptureOnActivate))
        {
            if (_decoded && (propName is not (nameof(RealtimeCapture) or nameof(AutoCaptureOnActivate)) || RealtimeCapture || AutoCaptureOnActivate))
                throw new NotSupportedException("WebGPU.RetainedProbe.CaptureRequested: retained probe data does not provide environment capture or prefilter production.");
            return;
        }
        base.OnPropertyChanged(propName, prev, field);
    }
}
