namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One-shot, synchronous explicit-production allocation stress request. It has
/// no callback, global switch, or implicit GPU wait; unsupported checkpoints
/// must report unproven evidence rather than silently succeeding.
/// A zero <see cref="RequestedByteSize"/> is permitted only for a named logical
/// resource at <see cref="EVulkanExplicitProductionBufferStressCheckpoint.AfterLogicalSeal"/>,
/// where the exact sealed owner derives one byte beyond its observed allocation.
/// </summary>
public sealed record VulkanExplicitProductionBufferStressProbeRequest(
    XRDataBuffer Buffer,
    EVulkanExplicitProductionBufferStressCheckpoint Checkpoint,
    uint RequestedByteSize,
    string? LogicalResourceName = null);
