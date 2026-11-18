namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanResourceLifetimeRecord
{
    public required VulkanResourceLifetimeKey Key;
    public required ulong Generation;
    public required string Owner;
    public VulkanResourceSlotHandle Slot;
    public ulong PublishedGeneration;
    public VulkanPublishedDescriptorSetSnapshot? PublishedDescriptorSnapshot;
    public EVulkanResourceLifetimeState State;
    public VulkanResourceGenerationPins Pins;
    public ulong LastSubmissionSerial;
    public ulong LastFrameOpContextId;
    public string? LastFrameOpKind;
    public ulong RetirementSerial;
    public string? RetirementOwner;
    public VulkanRetirementTicket RetirementTicket;

    /// <summary>
    /// True for a buffer that can never be bound to a descriptor, a vertex or index
    /// binding, or an indirect argument: a staging or readback buffer with only transfer
    /// usage. Its recycling, retirement and destruction do not change any bound buffer,
    /// so they do not advance <see cref="VulkanResourceLifetimeTracker.NativeBufferBindingRevision"/>.
    /// </summary>
    public bool ExcludedFromNativeBufferBindingRevision;
}
