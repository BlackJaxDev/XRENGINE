namespace XREngine.Rendering;

/// <summary>
/// Exposes the capability result associated with an advanced pipeline instance.
/// Editor and diagnostic consumers should depend on this contract instead of a concrete
/// pipeline type.
/// </summary>
public interface IAdvancedRenderPipelineCapabilitySource
{
    AdvancedRenderPipelineCapabilityResult CapabilityResult { get; }
}
