namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed class VulkanResourceLifetimeRecord
    {
        public required VulkanResourceLifetimeKey Key;
        public required ulong Generation;
        public required string Owner;
        public EVulkanResourceLifetimeState State;
        public VulkanResourceGenerationPins Pins;
        public ulong LastSubmissionSerial;
        public ulong LastFrameOpContextId;
        public string? LastFrameOpKind;
        public ulong RetirementSerial;
        public VulkanRetirementTicket RetirementTicket;
    }
}
