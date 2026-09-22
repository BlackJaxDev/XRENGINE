namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Describes the stable, algorithm-neutral surface supplied by a render-pipeline host.
/// </summary>
public interface IGlobalIlluminationHostAdapter
{
    string HostId { get; }
    EGlobalIlluminationHostCapability Capabilities { get; }
    bool IsMinimalOutput { get; }

    bool Supports(EGlobalIlluminationExecutionAnchor anchor);
}
