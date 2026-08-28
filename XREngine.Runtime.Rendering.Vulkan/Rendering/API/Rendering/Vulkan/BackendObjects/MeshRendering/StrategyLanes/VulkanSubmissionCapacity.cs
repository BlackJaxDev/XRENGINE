namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Fixed capacity proof for a sealed lane. Expected expansion is derived before
/// recording; execution clamps a defective producer instead of retrying.
/// </summary>
internal readonly record struct VulkanSubmissionCapacity(
    uint SourceCount,
    uint SourceCapacity,
    uint MaxOutputPerSource,
    uint OutputCapacity,
    uint WorstCaseOutputCount)
{
    internal static bool TryCreate(
        uint sourceCount,
        uint sourceCapacity,
        uint maxOutputPerSource,
        uint outputCapacity,
        out VulkanSubmissionCapacity capacity,
        out VulkanSubmissionPlanRejectionReason rejection)
    {
        capacity = default;
        rejection = VulkanSubmissionPlanRejectionReason.None;
        if (sourceCount > sourceCapacity)
        {
            rejection = VulkanSubmissionPlanRejectionReason.SourceCountExceedsCapacity;
            return false;
        }
        if (sourceCount != 0u && maxOutputPerSource == 0u)
        {
            rejection = VulkanSubmissionPlanRejectionReason.WorstCaseOutputExceedsCapacity;
            return false;
        }

        ulong worstCase = (ulong)sourceCount * maxOutputPerSource;
        if (worstCase > uint.MaxValue)
        {
            rejection = VulkanSubmissionPlanRejectionReason.OutputCapacityOverflow;
            return false;
        }
        if (worstCase > outputCapacity)
        {
            rejection = VulkanSubmissionPlanRejectionReason.WorstCaseOutputExceedsCapacity;
            return false;
        }

        capacity = new(
            sourceCount,
            sourceCapacity,
            maxOutputPerSource,
            outputCapacity,
            (uint)worstCase);
        return true;
    }
}
