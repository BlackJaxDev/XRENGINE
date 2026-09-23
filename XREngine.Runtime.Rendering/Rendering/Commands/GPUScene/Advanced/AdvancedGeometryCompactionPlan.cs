namespace XREngine.Rendering.Commands;

/// <summary>
/// Fully staged copy-forward replacement for the canonical immutable geometry
/// arenas. The plan owns no live state until it is applied inside its reserved
/// publication transaction.
/// </summary>
internal sealed class AdvancedGeometryCompactionPlan
{
    internal AdvancedGeometryCompactionPlan(
        AdvancedImmutableByteArena staticVertices,
        AdvancedImmutableByteArena indices,
        AdvancedImmutableByteArena preSkinnedCurrent,
        AdvancedImmutableByteArena preSkinnedPrevious,
        AdvancedImmutableByteArena meshletDescriptors,
        AdvancedImmutableByteArena meshletVertexIndices,
        AdvancedImmutableByteArena meshletTriangleWords,
        AdvancedGpuHandle[] handles,
        AdvancedGeometryRecord[] records)
    {
        StaticVertices = staticVertices;
        Indices = indices;
        PreSkinnedCurrent = preSkinnedCurrent;
        PreSkinnedPrevious = preSkinnedPrevious;
        MeshletDescriptors = meshletDescriptors;
        MeshletVertexIndices = meshletVertexIndices;
        MeshletTriangleWords = meshletTriangleWords;
        Handles = handles;
        Records = records;
    }

    internal AdvancedImmutableByteArena StaticVertices { get; }
    internal AdvancedImmutableByteArena Indices { get; }
    internal AdvancedImmutableByteArena PreSkinnedCurrent { get; }
    internal AdvancedImmutableByteArena PreSkinnedPrevious { get; }
    internal AdvancedImmutableByteArena MeshletDescriptors { get; }
    internal AdvancedImmutableByteArena MeshletVertexIndices { get; }
    internal AdvancedImmutableByteArena MeshletTriangleWords { get; }
    internal AdvancedGpuHandle[] Handles { get; }
    internal AdvancedGeometryRecord[] Records { get; }

    internal int ReplacementCount => Handles.Length;

    internal ulong ReclaimedBytes { get; init; }
}

/// <summary>Exact byte counts produced by a no-allocation copy-forward preflight.</summary>
internal readonly record struct AdvancedGeometryCompactionEstimate(
    uint StaticVertices,
    uint Indices,
    uint PreSkinnedCurrent,
    uint PreSkinnedPrevious,
    uint MeshletDescriptors,
    uint MeshletVertexIndices,
    uint MeshletTriangleWords,
    int ReplacementCount,
    ulong ReclaimedBytes);
