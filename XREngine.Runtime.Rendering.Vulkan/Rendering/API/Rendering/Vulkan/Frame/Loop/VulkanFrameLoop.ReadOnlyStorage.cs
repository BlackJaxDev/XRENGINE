namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Lowers immutable publications before descriptor preparation. The same
    /// retired CPU-slot epoch is used before acquire and during native recording;
    /// a swapchain image index is not an allocation authority.
    /// </summary>
    private bool TryPrepareReadOnlyStorage(
        FramePlan framePlan,
        int frameSlot,
        out VulkanReadOnlyStoragePreparedAuthority? authority,
        out bool materialPending,
        out string reason)
    {
        authority = null;
        materialPending = false;
        if (FrameDataArena is not { } arena)
        {
            reason = "The frame has no storage arena for immutable publications.";
            return false;
        }

        VulkanReadOnlyStoragePreparedAuthority preparedAuthority =
            ResourceRuntime.ReadOnlyStoragePreparedMap.CreateAuthority(arena, frameSlot);
        VulkanMaterialTablePreparedAuthority materialAuthority =
            ResourceRuntime.MaterialTablePreparedMap.CreateAuthority(arena, frameSlot);
        if (!framePlan.GetNativeStaticOperationsForRecording().Stream.TryPrepareReadOnlyStorage(
                ResourceRuntime.ReadOnlyStoragePreparedMap, in preparedAuthority, arena, out reason) ||
            !framePlan.GetNativeDynamicOverlayOperationsForRecording().Stream.TryPrepareReadOnlyStorage(
                ResourceRuntime.ReadOnlyStoragePreparedMap, in preparedAuthority, arena, out reason))
        {
            return false;
        }

        if (ResourceRuntime.BackendObjectContext is not { } context ||
            !framePlan.GetNativeStaticOperationsForRecording().Stream.TryPrepareMaterialTables(
                ResourceRuntime.MaterialTablePreparedMap, in materialAuthority, context, ResourceRuntime.Buffers, out materialPending, out reason) ||
            !framePlan.GetNativeDynamicOverlayOperationsForRecording().Stream.TryPrepareMaterialTables(
                ResourceRuntime.MaterialTablePreparedMap, in materialAuthority, context, ResourceRuntime.Buffers, out materialPending, out reason))
            return false;

        authority = preparedAuthority;
        return true;
    }
}
