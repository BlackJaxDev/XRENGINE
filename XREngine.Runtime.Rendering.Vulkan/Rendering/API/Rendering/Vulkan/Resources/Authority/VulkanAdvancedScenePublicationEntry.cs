using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>One exact publication lowered once within a frame slot.</summary>
internal struct VulkanAdvancedScenePublicationEntry
{
    internal AdvancedSharedGpuSceneDatabase? Database;
    internal AdvancedGpuScenePublicationReference Publication;
    internal VulkanAdvancedScenePublicationState State;
    internal VulkanAdvancedSceneGlobalInputs? Globals;
    internal int ActiveUseCount;

    /// <summary>
    /// One flag per canonical texture row: set when this publication published the row as
    /// not resident because its source changed after publication. Later outputs that share
    /// this publication accept only these stale rows. The array is allocated once per entry
    /// and reused by later generations.
    /// </summary>
    internal byte[]? StaleTextureRows;

    /// <summary>Number of valid flags in <see cref="StaleTextureRows"/>.</summary>
    internal int StaleTextureRowCount;

    internal readonly ReadOnlySpan<byte> RecordedStaleTextureRows
        => StaleTextureRows is null ? [] : StaleTextureRows.AsSpan(0, StaleTextureRowCount);

    internal void Clear()
    {
        Database = null;
        Publication = default;
        State = default;
        ActiveUseCount = 0;
        StaleTextureRowCount = 0;
    }
}
