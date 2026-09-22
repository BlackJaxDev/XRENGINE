namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Inputs and output semantics a GI host can make available to a provider.
/// </summary>
[Flags]
public enum EGlobalIlluminationHostCapability
{
    None = 0,
    DeferredSurface = 1 << 0,
    NativeOpaqueSurface = 1 << 1,
    ScreenSpaceDiffuseOutput = 1 << 2,
    ProbeSampling = 1 << 3,
    StereoLayers = 1 << 4,
    TemporalHistory = 1 << 5,
    MaterialSampling = 1 << 6,
    DebugPresentation = 1 << 7,
}
