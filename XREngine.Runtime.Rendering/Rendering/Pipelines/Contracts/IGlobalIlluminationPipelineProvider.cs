using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Supplies the global-illumination mode and resources consumed by reusable GI passes.
/// </summary>
public interface IGlobalIlluminationPipelineProvider
{
    EGlobalIlluminationMode GlobalIlluminationMode { get; set; }
    bool UsesRestirGI { get; }
    bool UsesVoxelConeTracing { get; }
    bool UsesLightVolumes { get; }
    bool UsesLightProbeGI { get; }
    bool UsesRadianceCascades { get; }
    bool UsesSurfelGI { get; }

    XRMaterial GetVoxelConeTracingVoxelizationMaterial();
}
