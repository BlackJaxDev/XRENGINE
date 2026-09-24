namespace XREngine.Rendering.Commands;

/// <summary>Scalar resident submission and aligned source description.</summary>
public readonly record struct S13aIdentityManifestSubmission(
    int SubmissionIndex, uint StableQueryKey, uint PrimitiveIndex,
    uint LegacyCommandIndex, uint PassIndex, ulong SourceOrder,
    AdvancedGpuHandle Draw, AdvancedGpuHandle Geometry,
    AdvancedGpuHandle Material, AdvancedGpuHandle Deformation,
    uint InstanceCount, uint Flags, uint StateClass,
    EAdvancedCanonicalCompatibilityReason CompatibilityReason,
    EAdvancedVelocityValidityReason TemporalEventReason,
    ulong DependencySignature, string? SourceLabel,
    string? SourceSubMeshName, string? ImportedEntityIdentity,
    bool ImportedEntityIdentityIsStable, string? FixtureKey,
    bool FixtureKeyIsStable,
    float[]? CurrentWorld, float[]? PreviousWorld,
    bool? LiveOwnerCastsShadows, bool? RenderOwnerCastsShadows,
    uint? RenderOwnerLayerMask, uint? PublishedLayerMask);
