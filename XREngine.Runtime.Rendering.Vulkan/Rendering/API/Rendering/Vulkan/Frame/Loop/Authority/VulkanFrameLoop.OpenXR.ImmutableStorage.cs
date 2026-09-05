namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>Reopens both upload arenas under the same exact output completion proof.</summary>
    private void ReopenOpenXrFrameDataSlot(uint slotIndex, bool completionProven)
    {
        if (MappedFrameArena is { } mapped &&
            !mapped.TryResetFrameSlot(slotIndex, mapped.Generation, completionProven))
            throw new InvalidOperationException($"OpenXR mapped frame-data slot {slotIndex} could not be reopened.");
        if (FrameDataArena is not { } storage ||
            !storage.TryResetFrameSlot(slotIndex, storage.Generation, completionProven))
            throw new InvalidOperationException($"OpenXR immutable storage slot {slotIndex} could not be reopened.");
    }

    /// <summary>
    /// Lowers captured storage publications into the exact output slot before
    /// either an eye recorder or the strict-SPS array recorder resolves descriptors.
    /// </summary>
    private VulkanReadOnlyStoragePreparedAuthority? PrepareOpenXrImmutableStorage(
        FrameOperationStream operations,
        uint frameDataSlotIndex,
        uint openXrViewIndex)
    {
        VulkanFrameDataArena arena = FrameDataArena
            ?? throw new InvalidOperationException("OpenXR immutable storage requires an active frame-data arena.");

        int slotIndex = checked((int)frameDataSlotIndex);
        VulkanReadOnlyStoragePreparedAuthority authority =
            ResourceRuntime.ReadOnlyStoragePreparedMap.CreateAuthority(arena, slotIndex);
        if (!operations.TryPrepareReadOnlyStorage(
                ResourceRuntime.ReadOnlyStoragePreparedMap,
                in authority,
                arena,
                out string failure))
        {
            throw CreateOpenXrEyePresentNowFailure(
                openXrViewIndex,
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "eye-read-only-storage",
                "OpenXREyeSubmit -> immutable storage preparation",
                failure);
        }

        VulkanMaterialTablePreparedAuthority materialAuthority =
            ResourceRuntime.MaterialTablePreparedMap.CreateAuthority(arena, slotIndex);
        if (ResourceRuntime.BackendObjectContext is not { } materialContext ||
            !operations.TryPrepareMaterialTables(
                ResourceRuntime.MaterialTablePreparedMap,
                in materialAuthority,
                materialContext,
                ResourceRuntime.Buffers,
                out _,
                out failure))
        {
            throw CreateOpenXrEyePresentNowFailure(
                openXrViewIndex,
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "eye-material-storage",
                "OpenXREyeSubmit -> immutable material preparation",
                failure);
        }

        return authority;
    }
}
