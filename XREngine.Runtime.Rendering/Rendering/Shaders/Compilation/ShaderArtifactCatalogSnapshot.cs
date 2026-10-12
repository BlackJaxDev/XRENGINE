namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>A complete, validated publication shared by stable session catalog views.</summary>
internal sealed record ShaderArtifactCatalogSnapshot(
    ShaderProgramArtifactCatalog Artifacts,
    EngineMaterialVariantCatalog MaterialVariants,
    WebPipelineArtifactCatalog PipelineArtifacts,
    WebComputeArtifactCatalog ComputeArtifacts);
