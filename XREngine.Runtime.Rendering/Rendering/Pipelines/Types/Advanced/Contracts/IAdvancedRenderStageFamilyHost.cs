namespace XREngine.Rendering;

/// <summary>
/// Exposes the Advanced frame-contract definition used by the active pipeline
/// instance. The definition supplies commands and resource declarations only;
/// the host remains the owner of rendering state, output reservation, and
/// frame lifecycle.
/// </summary>
public interface IAdvancedRenderStageFamilyHost
{
    AdvancedRenderPipeline AdvancedStageFamilyDefinition { get; }
}
