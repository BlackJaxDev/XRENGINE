namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable frame-slot-owned input for refreshing data referenced by a
/// reusable command buffer. The producer resolves operation identity, planner
/// context, draw slot, and compute descriptor identity before reuse
/// consumption begins.
/// </summary>
internal readonly record struct VulkanReusableFrameDataRefreshRequest(
    EVulkanReusableFrameDataRefreshKind Kind,
    int SourceOpIndex,
    int SourceOpCount,
    FrameOpContext Context,
    VulkanFrameOpPlannerStateKey PlannerKey,
    VkMeshRenderer? MeshRenderer,
    FrameOp? SourceOperation,
    int DrawUniformSlot,
    EVulkanBindingFrequencyMask FrequencyMask,
    VkRenderProgram? ComputeProgram,
    ComputeDispatchSnapshot? ComputeSnapshot,
    ulong ComputeDescriptorKey,
    uint ComputeGroupsX,
    uint ComputeGroupsY,
    uint ComputeGroupsZ,
    VulkanReusableFrameOwnerKey OwnerKey)
{
    /// <summary>
    /// Reads the immutable draw payload from the current sealed frame-plan
    /// operation. Refresh requests are consumed synchronously while that plan
    /// is active, so embedding another multi-kilobyte value copy provides no
    /// additional lifetime or mutation isolation.
    /// </summary>
    internal ref readonly PendingMeshDraw Draw
    {
        get
        {
            if (SourceOperation is MeshDrawOp meshDraw)
                return ref meshDraw.DrawRef;
            if (SourceOperation is IndirectDrawOp indirectDraw)
                return ref indirectDraw.DrawRef;

            throw new InvalidOperationException(
                "A mesh frame-data refresh request must reference a mesh draw operation.");
        }
    }

    internal static VulkanReusableFrameDataRefreshRequest CreateMesh(
        EVulkanReusableFrameDataRefreshKind kind,
        int sourceOpIndex,
        int sourceOpCount,
        in FrameOpContext context,
        in VulkanFrameOpPlannerStateKey plannerKey,
        VkMeshRenderer meshRenderer,
        FrameOp sourceOperation,
        int drawUniformSlot,
        EVulkanBindingFrequencyMask frequencyMask =
            EVulkanBindingFrequencyMask.All,
        in VulkanReusableFrameOwnerKey ownerKey = default)
        => new(
            kind,
            sourceOpIndex,
            sourceOpCount,
            context,
            plannerKey,
            meshRenderer,
            sourceOperation,
            drawUniformSlot,
            frequencyMask,
            null,
            null,
            0,
            0,
            0,
            0,
            ownerKey);

    internal static VulkanReusableFrameDataRefreshRequest CreateCompute(
        int sourceOpIndex,
        int sourceOpCount,
        in FrameOpContext context,
        in VulkanFrameOpPlannerStateKey plannerKey,
        VkRenderProgram program,
        ComputeDispatchSnapshot snapshot,
        ulong descriptorKey,
        uint groupsX,
        uint groupsY,
        uint groupsZ)
        => new(
            EVulkanReusableFrameDataRefreshKind.Compute,
            sourceOpIndex,
            sourceOpCount,
            context,
            plannerKey,
            null,
            null,
            -1,
            EVulkanBindingFrequencyMask.None,
            program,
            snapshot,
            descriptorKey,
            groupsX,
            groupsY,
            groupsZ,
            default);
}
