namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanRetirementTicket(
        ulong GraphicsSequence,
        ulong TransferSequence,
        ulong OtherSequence,
        long EnqueuedTimestamp,
        ulong ResourceGeneration,
        bool ExternalOwnershipPending,
        VulkanRetirementPinSet? PinSet = null)
    {
        public static VulkanRetirementTicket None => default;

        public VulkanRetirementTicket Merge(in VulkanRetirementTicket other)
            => new(
                Math.Max(GraphicsSequence, other.GraphicsSequence),
                Math.Max(TransferSequence, other.TransferSequence),
                Math.Max(OtherSequence, other.OtherSequence),
                EnqueuedTimestamp == 0
                    ? other.EnqueuedTimestamp
                    : other.EnqueuedTimestamp == 0
                        ? EnqueuedTimestamp
                        : Math.Min(EnqueuedTimestamp, other.EnqueuedTimestamp),
                Math.Max(ResourceGeneration, other.ResourceGeneration),
                ExternalOwnershipPending || other.ExternalOwnershipPending,
                VulkanRetirementPinSet.Merge(PinSet, other.PinSet));
    }
}
