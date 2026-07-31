namespace XREngine;

/// <summary>
/// Exact reason an auto-uniform block or typed operation could not remain on
/// the compiled Vulkan binding path.
/// </summary>
public enum EVulkanAutoUniformFallbackReason
{
    None = 0,
    BindingSnapshotIneligible,
    ProgramUnavailable,
    InvalidBufferSize,
    BindingSchemaUnavailable,
    BindingSchemaMismatch,
    InvalidMemberName,
    UnsupportedShaderType,
    InvalidDestinationRange,
    InvalidArrayLayout,
    StructSnapshotRequired,
    EngineSourceTypeMismatch,
    MeshStateSourceTypeMismatch,
    TypedEngineSourceUnavailable,
    TypedEngineWriteFailed,
    TypedTemporalWriteFailed,
    TypedMeshStateSourceUnavailable,
    TypedMeshStateWriteFailed,
    TypedMaterialOrRuntimeWriteFailed,
    Count,
}
