using XREngine.Rendering.GI.Contracts;

namespace XREngine.Rendering.GI.Integration;

/// <summary>
/// Algorithm-neutral Default pipeline surface. Resource names and FBOs remain host-private.
/// </summary>
public sealed class DefaultGlobalIlluminationHostAdapter : IGlobalIlluminationHostAdapter
{
    public static DefaultGlobalIlluminationHostAdapter Instance { get; } = new();

    private DefaultGlobalIlluminationHostAdapter() { }

    public string HostId => "default";
    public EGlobalIlluminationHostCapability Capabilities =>
        EGlobalIlluminationHostCapability.DeferredSurface |
        EGlobalIlluminationHostCapability.ScreenSpaceDiffuseOutput |
        EGlobalIlluminationHostCapability.ProbeSampling |
        EGlobalIlluminationHostCapability.StereoLayers |
        EGlobalIlluminationHostCapability.TemporalHistory |
        EGlobalIlluminationHostCapability.MaterialSampling |
        EGlobalIlluminationHostCapability.DebugPresentation;
    public bool IsMinimalOutput => false;

    public bool Supports(EGlobalIlluminationExecutionAnchor anchor)
        => true;
}
