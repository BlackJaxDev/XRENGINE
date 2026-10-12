using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>A retained identity snapshot for an explicitly enabled mesh-resolution diagnostic.</summary>
public readonly record struct WebGpuMeshResolutionTrace(
    uint FrameSequence,
    int RendererId,
    string? RendererName,
    int MeshId,
    string? MeshName,
    int VertexCount,
    uint RequestedInstances,
    bool HasRenderDataPreparation,
    int SourceMaterialId,
    string? SourceMaterialName,
    EngineMaterialSemanticIdentity SourceSemantic,
    int LocalOverrideMaterialId,
    EngineMaterialSemanticIdentity LocalOverrideSemantic,
    int GlobalOverrideMaterialId,
    EngineMaterialSemanticIdentity GlobalOverrideSemantic,
    int PipelineOverrideMaterialId,
    EngineMaterialSemanticIdentity PipelineOverrideSemantic,
    int ResolvedMaterialId,
    string? ResolvedMaterialName,
    EngineMaterialSemanticIdentity ResolvedSemantic,
    string Reason,
    bool ShadowPass,
    bool DepthNormalVariant,
    string? FrameBufferName,
    ECullMode SourceMaterialCullMode,
    ECullMode ResolvedMaterialCullMode,
    ECullMode SelectedRenderOptionsCullMode,
    ECullMode EffectiveRasterCullMode,
    bool RenderOptionsOverridePresent,
    string Stage,
    string? FailureType,
    string? FailureMessage);
