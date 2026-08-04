namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Typed primary-orchestration actions attached to an ordered operation node.
/// The mask describes which recorder phases may execute for that operation;
/// operation-specific payload remains in the immutable <see cref="FrameOp"/>.
/// </summary>
[Flags]
internal enum EVulkanPrimaryPlanAction : byte
{
    /// <summary>
    /// No actions are required for this operation; it may be skipped in the primary plan.
    /// </summary>
    None = 0,
    /// <summary>
    /// This operation requires a barrier batch to be recorded before it can execute.
    /// A barrier batch is a set of memory and execution barriers that ensure proper synchronization between operations.
    /// </summary>
    BarrierBatch = 1 << 0,
    /// <summary>
    /// This operation requires the primary plan to begin a rendering pass before it can execute.
    /// A rendering pass is a sequence of rendering commands that operate on a set of attachments (e.g., color and depth buffers).
    /// </summary>
    BeginRendering = 1 << 1,
    /// <summary>
    /// This operation requires the primary plan to execute a secondary command buffer range before it can execute.
    /// A secondary command buffer range is a subset of commands recorded in a secondary command buffer that can be executed within a primary command buffer.
    /// </summary>
    ExecuteSecondaryRange = 1 << 2,
    /// <summary>
    /// This operation requires the primary plan to record commands into the command buffer.
    /// This means recording the specific commands required to perform the operation within the primary command buffer.
    /// </summary>
    RecordOperation = 1 << 3,
    /// <summary>
    /// This operation requires the primary plan to end a rendering pass after it has executed.
    /// A rendering pass must be properly ended to ensure that all rendering commands are executed and the attachments are in a consistent state.
    /// </summary>
    EndRendering = 1 << 4,
    /// <summary>
    /// This operation requires the primary plan to prepare for presentation after it has executed.
    /// Preparing for presentation involves transitioning the swapchain image to the appropriate layout and ensuring that all rendering commands have completed before presenting the image to the screen.
    /// </summary>
    PreparePresent = 1 << 5,
    /// <summary>
    /// This operation requires the primary plan to release external image ownership after it has executed.
    /// External image ownership means that the image is owned by an external entity (e.g., a different queue or process)
    /// and must be released before it can be used by the current command buffer.
    /// </summary>
    ReleaseExternalImageOwnership = 1 << 6,
    /// <summary>
    /// This operation requires the primary plan to transfer queue ownership after it has executed.
    /// This means that the ownership of resources (e.g., images or buffers) is being transferred from one queue to another,
    /// and the primary plan must handle this transfer appropriately.
    /// </summary>
    QueueOwnershipTransfer = 1 << 7,
}
