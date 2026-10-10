namespace XREngine.Rendering;

/// <summary>Locates one packed mesh payload in a static deformation generation.</summary>
internal readonly record struct AdvancedGpuDeformationMeshPayloadEntry(
    ulong Hash,
    AdvancedGpuDeformationMeshSlice Slice,
    uint SpillBase,
    uint SpillCount,
    uint RecordBase,
    uint RecordCount,
    uint DeltaBase,
    uint DeltaCount,
    int NextHashEntry);
