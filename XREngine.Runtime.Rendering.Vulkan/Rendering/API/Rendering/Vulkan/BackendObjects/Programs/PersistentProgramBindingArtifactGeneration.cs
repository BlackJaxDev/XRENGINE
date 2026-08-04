using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact generation set that makes a detached program-binding artifact safe
/// to reuse across render frames.
/// </summary>
internal readonly record struct PersistentProgramBindingArtifactGeneration(
    ulong MaterialLayoutVersion,
    ulong MaterialValueVersion,
    ulong MaterialResourceVersion,
    long MaterialShaderRevision,
    long MaterialUberRevision,
    ulong ProgramLinkGeneration,
    ulong TypedPublisherSignature,
    ulong EngineUniformSignature,
    ulong EngineResourceSignature,
    ulong PipelineUniformGeneration,
    EUniformRequirements EngineRequirements,
    bool CaptureUniformsOnRender);
