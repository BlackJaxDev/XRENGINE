namespace XREngine.Rendering.Commands;

/// <summary>
/// On-demand classification of the immutable CPU draw-metadata mirror against
/// the production meshlet expansion contract. This is diagnostic state only;
/// capturing it never maps or reads back a GPU resource.
/// </summary>
public readonly record struct GpuMeshletEligibilitySnapshot(
    uint TotalCommands,
    uint ActiveCommands,
    uint PassCommands,
    uint EligibleCommands,
    ulong EligibleMeshlets,
    uint MissingMetadata,
    uint RejectedInstanceCount,
    uint RejectedSkin,
    uint RejectedStateClass,
    uint RejectedFlags,
    uint MissingMeshletRange,
    uint TransparentFlags,
    uint SkinnedFlags,
    uint DynamicTransformFlags,
    uint DoubleSidedFlags,
    uint InstancedFlags,
    uint AnimatedFlags,
    uint BlendShapeFlags,
    uint CustomShaderFlags,
    uint CpuFallbackOnlyFlags,
    uint NonCanonicalRasterStateFlags);
