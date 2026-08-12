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
    PendingMeshDraw Draw,
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
    internal static VulkanReusableFrameDataRefreshRequest CreateMesh(
        EVulkanReusableFrameDataRefreshKind kind,
        int sourceOpIndex,
        int sourceOpCount,
        in FrameOpContext context,
        in VulkanFrameOpPlannerStateKey plannerKey,
        VkMeshRenderer meshRenderer,
        in PendingMeshDraw draw,
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
            draw,
            drawUniformSlot,
            frequencyMask,
            default,
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
            default,
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
