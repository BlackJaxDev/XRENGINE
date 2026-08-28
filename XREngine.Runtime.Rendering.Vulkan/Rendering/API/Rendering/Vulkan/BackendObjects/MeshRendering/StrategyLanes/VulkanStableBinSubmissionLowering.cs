namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Coordinator-only lowering of a frozen bin plan into exact set-1 offsets.
/// It never reads a GPU count, retries a producer, or substitutes CPU direct
/// recording for a requested GPU lane.
/// </summary>
internal static class VulkanStableBinSubmissionLowering
{
    private const uint IndexedIndirectArgumentStride = 20u;
    private const uint MeshIndirectArgumentStride = 12u;

    internal static bool TryLower(
        VulkanSealedBinSubmissionPlan plan,
        in VulkanPreparedStableBinHeader header,
        in AdvancedIndirectRange range,
        in VulkanAdvancedVisibilityResourceState visibilityState,
        out VulkanStableBinSubmission submission,
        out VulkanStableBinSubmissionLoweringFailure failure)
    {
        ArgumentNullException.ThrowIfNull(plan);
        submission = default;
        failure = VulkanStableBinSubmissionLoweringFailure.None;
        if (!plan.BinKey.IsValid || header.RecordCount <= 0 ||
            header.Key != plan.BinKey || header.ResourceManifest is null)
        {
            failure = VulkanStableBinSubmissionLoweringFailure.InvalidHeader;
            return false;
        }

        if (plan.ResolvedStrategy == EMeshSubmissionStrategy.CpuDirect)
        {
            if (range.Key.Producer is not
                (EAdvancedGeometryProducer.CpuDirectStaticIndexed or
                 EAdvancedGeometryProducer.CpuDirectPreSkinned))
            {
                failure = VulkanStableBinSubmissionLoweringFailure.ProducerLaneMismatch;
                return false;
            }
            submission = new(plan, header, default, 0u, 0u, 0u, 0u);
            return true;
        }

        bool indexed = plan.ResolvedStrategy is
            EMeshSubmissionStrategy.GpuIndirectZeroReadback or
            EMeshSubmissionStrategy.GpuIndirectInstrumented;
        bool meshlet = plan.ResolvedStrategy is
            EMeshSubmissionStrategy.GpuMeshletZeroReadback or
            EMeshSubmissionStrategy.GpuMeshletInstrumented;
        if ((indexed && range.Key.Producer != EAdvancedGeometryProducer.IndirectIndexed) ||
            (meshlet && range.Key.Producer is not
                (EAdvancedGeometryProducer.StaticMeshlet or
                 EAdvancedGeometryProducer.SkinnedMeshlet)))
        {
            failure = VulkanStableBinSubmissionLoweringFailure.ProducerLaneMismatch;
            return false;
        }
        if (!indexed && !meshlet)
        {
            failure = VulkanStableBinSubmissionLoweringFailure.UnsupportedStrategy;
            return false;
        }
        if (!range.CountWrittenByGpu || range.PayloadCapacity == 0u ||
            !visibilityState.IsValid ||
            range.FirstPayloadIndex >= visibilityState.IndirectArgumentCapacity ||
            range.PayloadCapacity > visibilityState.IndirectArgumentCapacity - range.FirstPayloadIndex ||
            !visibilityState.RangeCounts.IsValid ||
            (indexed && (!visibilityState.IndirectArguments.IsValid ||
                visibilityState.IndirectArguments.Buffer.Handle == 0)) ||
            (meshlet && (!visibilityState.MeshArguments.IsValid ||
                visibilityState.MeshArguments.Buffer.Handle == 0)) ||
            visibilityState.RangeCounts.Buffer.Handle == 0)
        {
            failure = VulkanStableBinSubmissionLoweringFailure.VisibilityStateUnavailable;
            return false;
        }

        try
        {
            ulong indexedArgumentOffset = indexed
                ? checked(visibilityState.IndirectArguments.Offset +
                    range.ArgumentBufferOffset)
                : 0u;
            ulong indexedArgumentLength = checked(
                (ulong)range.PayloadCapacity * IndexedIndirectArgumentStride);
            if (indexed && indexedArgumentOffset + indexedArgumentLength >
                    visibilityState.IndirectArguments.Offset + visibilityState.IndirectArguments.Length)
            {
                failure = VulkanStableBinSubmissionLoweringFailure.IndirectArgumentCapacityExceeded;
                return false;
            }
            ulong meshArgumentOffset = meshlet
                ? checked(visibilityState.MeshArguments.Offset +
                    range.FirstPayloadIndex * MeshIndirectArgumentStride)
                : 0u;
            ulong meshArgumentLength = checked(
                (ulong)range.PayloadCapacity * MeshIndirectArgumentStride);
            if (meshlet && meshArgumentOffset + meshArgumentLength >
                    visibilityState.MeshArguments.Offset + visibilityState.MeshArguments.Length)
            {
                failure = VulkanStableBinSubmissionLoweringFailure.IndirectArgumentCapacityExceeded;
                return false;
            }
            // The fixed counter ABI reserves one uint per range at the start
            // of the counters slice. It is written by the producer and read
            // only by vkCmdDrawIndexedIndirectCount.
            ulong countOffset = checked(
                visibilityState.RangeCounts.Offset + range.CountBufferOffset);
            if (countOffset + sizeof(uint) >
                visibilityState.RangeCounts.Offset + visibilityState.RangeCounts.Length)
            {
                failure = VulkanStableBinSubmissionLoweringFailure.IndirectArgumentCapacityExceeded;
                return false;
            }
            submission = new(
                plan,
                header,
                visibilityState,
                indexedArgumentOffset,
                meshArgumentOffset,
                countOffset,
                range.PayloadCapacity);
            return true;
        }
        catch (OverflowException)
        {
            failure = VulkanStableBinSubmissionLoweringFailure.OffsetOverflow;
            return false;
        }
    }
}
