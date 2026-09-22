using XREngine.Rendering.GI.Contracts;

namespace XREngine.Rendering.GI.Integration;

/// <summary>
/// Algorithm-neutral Advanced pipeline surface that retains native stage ownership and minimal-output restrictions.
/// </summary>
public sealed class AdvancedGlobalIlluminationHostAdapter : IGlobalIlluminationHostAdapter
{
    public AdvancedGlobalIlluminationHostAdapter(bool isMinimalOutput)
        => IsMinimalOutput = isMinimalOutput;

    public string HostId => "advanced";
    public EGlobalIlluminationHostCapability Capabilities => IsMinimalOutput
        ? EGlobalIlluminationHostCapability.None
        : EGlobalIlluminationHostCapability.NativeOpaqueSurface |
          EGlobalIlluminationHostCapability.ScreenSpaceDiffuseOutput |
          EGlobalIlluminationHostCapability.ProbeSampling |
          EGlobalIlluminationHostCapability.StereoLayers |
          EGlobalIlluminationHostCapability.TemporalHistory |
          EGlobalIlluminationHostCapability.MaterialSampling |
          EGlobalIlluminationHostCapability.DebugPresentation;
    public bool IsMinimalOutput { get; }

    public bool Supports(EGlobalIlluminationExecutionAnchor anchor)
        => !IsMinimalOutput || anchor is EGlobalIlluminationExecutionAnchor.ScenePreparation;
}
