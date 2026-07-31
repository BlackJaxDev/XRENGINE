namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Typed primary-orchestration actions attached to an ordered operation node.
/// The mask describes which recorder phases may execute for that operation;
/// operation-specific payload remains in the immutable <see cref="FrameOp"/>.
/// </summary>
[Flags]
internal enum EVulkanPrimaryPlanAction : byte
{
    None = 0,
    BarrierBatch = 1 << 0,
    BeginRendering = 1 << 1,
    ExecuteSecondaryRange = 1 << 2,
    RecordOperation = 1 << 3,
    EndRendering = 1 << 4,
    PreparePresent = 1 << 5,
    ReleaseExternalImageOwnership = 1 << 6,
    QueueOwnershipTransfer = 1 << 7,
}
