using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Evaluates the exact contract for compute, transfer, and query secondary
    /// recording.
    /// The primary remains authoritative for render-scope closure, pass barriers,
    /// and queue-ownership transfers before the secondary is executed.
    /// </summary>
    private VulkanSecondaryRecordingContract EvaluateSecondaryRecordingContract(
        FrameOperationSequence operations,
        int startIndex,
        in VulkanSecondaryRecordingBucket bucket,
        int resolvedPassIndex,
        bool barrierPlanHasPass,
        bool renderScopeActive,
        bool primaryQueryActive)
    {
        EVulkanSecondaryCommandFamily family = bucket.Family;
        VulkanQuerySecondaryInheritanceContract queryInheritance =
            VulkanQuerySecondaryInheritanceContract.Create(
                primaryQueryActive,
                ResourceRuntime.Queries.InheritedQueriesEnabled);
        if (!IsSecondaryFamilyEnabled(family))
        {
            return new(
                family,
                EVulkanSecondaryRecordingEligibility.FamilyDisabled,
                queryInheritance);
        }

        if (!_enableSecondaryCommandBuffers)
        {
            return new(
                family,
                EVulkanSecondaryRecordingEligibility.SecondaryCommandBuffersDisabled,
                queryInheritance);
        }

        if (bucket.Count <= 0 ||
            startIndex < 0 ||
            startIndex > operations.Length - bucket.Count)
        {
            return new(
                family,
                EVulkanSecondaryRecordingEligibility.EmptyRange,
                queryInheritance);
        }

        if (family == EVulkanSecondaryCommandFamily.Query)
        {
            EVulkanSecondaryRecordingEligibility queryEligibility =
                EvaluateQuerySecondaryOperations(
                    operations,
                    startIndex,
                    bucket);
            if (queryEligibility !=
                EVulkanSecondaryRecordingEligibility.Eligible)
            {
                return new(
                    family,
                    queryEligibility,
                    queryInheritance);
            }
        }

        if (!queryInheritance.CanExecuteWithoutInheritedQueryState)
        {
            return new(
                family,
                EVulkanSecondaryRecordingEligibility
                    .QueryInheritanceUnsupported,
                queryInheritance);
        }

        if (renderScopeActive)
        {
            return new(
                family,
                EVulkanSecondaryRecordingEligibility.ActiveRenderScope,
                queryInheritance);
        }

        QueueFamilyIndices queueFamilies = _deviceContext.QueueFamilies;
        bool queueFamilySupported = family switch
        {
            EVulkanSecondaryCommandFamily.Compute =>
                queueFamilies.GraphicsFamilyIndex.HasValue &&
                queueFamilies.GraphicsFamilySupportsCompute,
            EVulkanSecondaryCommandFamily.Synchronization =>
                queueFamilies.GraphicsFamilyIndex.HasValue,
            EVulkanSecondaryCommandFamily.Transfer =>
                queueFamilies.GraphicsFamilyIndex.HasValue &&
                queueFamilies.GraphicsFamilySupportsTransfer,
            EVulkanSecondaryCommandFamily.Query =>
                queueFamilies.GraphicsFamilyIndex.HasValue &&
                queueFamilies.GraphicsFamilySupportsTransfer,
            _ => false,
        };
        if (!queueFamilySupported)
        {
            return new(
                family,
                EVulkanSecondaryRecordingEligibility.QueueFamilyUnsupported,
                queryInheritance);
        }

        if (!barrierPlanHasPass)
        {
            return new(
                family,
                EVulkanSecondaryRecordingEligibility.BarrierPlanUnavailable,
                queryInheritance);
        }

        int endIndex = startIndex + bucket.Count;
        for (int operationIndex = startIndex;
             operationIndex < endIndex;
             operationIndex++)
        {
            bool operationValid = family switch
            {
                EVulkanSecondaryCommandFamily.Compute =>
                    operations.GetHeader(operationIndex).OpCode == EVulkanPrimaryPlanNodeKind.ComputeDispatch &&
                    IsComputeSecondaryOperationValid(in operations.GetComputeDispatch(operationIndex), bucket) ||
                    operations.GetHeader(operationIndex).OpCode == EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect &&
                    IsComputeIndirectSecondaryOperationValid(in operations.GetComputeDispatchIndirect(operationIndex), bucket),
                EVulkanSecondaryCommandFamily.Synchronization =>
                    operations.GetHeader(operationIndex).OpCode == EVulkanPrimaryPlanNodeKind.MemoryBarrier &&
                    IsExplicitFixedMemoryBarrier(in operations.GetMemoryBarrier(operationIndex), bucket),
                EVulkanSecondaryCommandFamily.Transfer =>
                    operations.GetHeader(operationIndex).OpCode == EVulkanPrimaryPlanNodeKind.BufferCopy &&
                    IsTransferSecondaryOperationValid(in operations.GetBufferCopy(operationIndex), bucket),
                EVulkanSecondaryCommandFamily.Query =>
                    operations.GetHeader(operationIndex).OpCode == EVulkanPrimaryPlanNodeKind.Query &&
                    operations.GetQuery(operationIndex).Operation ==
                        ERenderQueryOperation.CopyResults,
                _ => false,
            };
            if (!operationValid)
            {
                return new(
                    family,
                    EVulkanSecondaryRecordingEligibility.InvalidOperationState,
                    queryInheritance);
            }
        }

        return new(
            family,
            EVulkanSecondaryRecordingEligibility.Eligible,
            queryInheritance);
    }

    private bool IsSecondaryFamilyEnabled(
        EVulkanSecondaryCommandFamily family)
        => family switch
        {
            EVulkanSecondaryCommandFamily.Compute =>
                ComputeSecondaryCommandBuffersEnabled,
            // Only MemoryBarrierOp is admitted to this family. Render-graph,
            // image-layout, and queue-transfer barriers remain primary-owned.
            EVulkanSecondaryCommandFamily.Synchronization => true,
            EVulkanSecondaryCommandFamily.Transfer =>
                TransferSecondaryCommandBuffersEnabled,
            EVulkanSecondaryCommandFamily.Query =>
                QuerySecondaryCommandBuffersEnabled,
            _ => false,
        };

    private static EVulkanSecondaryRecordingEligibility
        EvaluateQuerySecondaryOperations(
            FrameOperationSequence operations,
            int startIndex,
            in VulkanSecondaryRecordingBucket bucket)
    {
        int endIndex = startIndex + bucket.Count;
        for (int operationIndex = startIndex;
             operationIndex < endIndex;
             operationIndex++)
        {
            if (operations.GetHeader(operationIndex).OpCode != EVulkanPrimaryPlanNodeKind.Query)
            {
                return EVulkanSecondaryRecordingEligibility
                    .InvalidOperationState;
            }

            ref readonly QueryPayload query = ref operations.GetQuery(operationIndex);
            EVulkanSecondaryRecordingEligibility eligibility = query.Operation switch
                {
                    ERenderQueryOperation.Reset =>
                        EVulkanSecondaryRecordingEligibility
                            .QueryResetPrimaryOwned,
                    ERenderQueryOperation.Begin or
                    ERenderQueryOperation.End =>
                        EVulkanSecondaryRecordingEligibility
                            .QueryPairPrimaryOwned,
                    ERenderQueryOperation.WriteTimestamp =>
                        EVulkanSecondaryRecordingEligibility
                            .QueryTimestampPrimaryOwned,
                    ERenderQueryOperation.WriteProperties =>
                        EVulkanSecondaryRecordingEligibility
                            .QueryPropertiesPrimaryOwned,
                    ERenderQueryOperation.CopyResults =>
                        IsQueryResultCopyOrdered(
                            operations,
                            operationIndex,
                            in query)
                            ? EVulkanSecondaryRecordingEligibility.Eligible
                            : EVulkanSecondaryRecordingEligibility
                                .QueryResultOrderingUnavailable,
                    _ => EVulkanSecondaryRecordingEligibility
                        .InvalidOperationState,
                };
            if (eligibility !=
                EVulkanSecondaryRecordingEligibility.Eligible)
            {
                return eligibility;
            }

            if (!query.Query.CanCopyResults(
                    query.ResultDestination,
                    query.ResultDestinationOffset,
                    query.ResultStride,
                    query.IncludeAvailability))
            {
                return EVulkanSecondaryRecordingEligibility
                    .InvalidOperationState;
            }
        }

        return EVulkanSecondaryRecordingEligibility.Eligible;
    }

    private static bool IsQueryResultCopyOrdered(
        FrameOperationSequence operations,
        int copyIndex,
        in QueryPayload copy)
    {
        bool queryActive = false;
        bool producerRecorded = false;
        for (int operationIndex = 0;
             operationIndex < copyIndex;
             operationIndex++)
        {
            if (operations.GetHeader(operationIndex).OpCode != EVulkanPrimaryPlanNodeKind.Query ||
                !ReferenceEquals(operations.GetQuery(operationIndex).Query, copy.Query))
            {
                continue;
            }

            switch (operations.GetQuery(operationIndex).Operation)
            {
                case ERenderQueryOperation.Reset:
                    queryActive = false;
                    producerRecorded = false;
                    break;
                case ERenderQueryOperation.Begin:
                    queryActive = true;
                    producerRecorded = false;
                    break;
                case ERenderQueryOperation.End:
                    if (queryActive)
                    {
                        queryActive = false;
                        producerRecorded = true;
                    }
                    break;
                case ERenderQueryOperation.WriteTimestamp:
                case ERenderQueryOperation.WriteProperties:
                    producerRecorded = true;
                    break;
            }
        }

        return producerRecorded && !queryActive;
    }

    private static bool IsComputeSecondaryOperationValid(
        in ComputeDispatchPayload operation,
        in VulkanSecondaryRecordingBucket bucket)
        => operation.GroupsX > 0 &&
           operation.GroupsY > 0 &&
           operation.GroupsZ > 0 &&
           operation.Program is not null &&
           operation.Snapshot is not null;

    private static bool IsComputeIndirectSecondaryOperationValid(
        in ComputeDispatchIndirectPayload operation,
        in VulkanSecondaryRecordingBucket bucket)
        => operation.Program is not null &&
           operation.Snapshot is not null &&
           operation.ArgumentOwner is not null &&
           operation.ArgumentBuffer.Handle != 0UL;

    private static bool IsExplicitFixedMemoryBarrier(
        in MemoryBarrierPayload operation,
        in VulkanSecondaryRecordingBucket bucket)
        => operation.Mask != 0;

    private static bool IsTransferSecondaryOperationValid(
        in BufferCopyPayload operation,
        in VulkanSecondaryRecordingBucket bucket)
    {
        if (operation.SourceOwner is null ||
            operation.DestinationOwner is null ||
            operation.SourceBuffer.Handle == 0 ||
            operation.DestinationBuffer.Handle == 0 ||
            operation.ByteCount == 0)
        {
            return false;
        }

        if (operation.SourceOwner.BufferHandle is not { } sourceHandle ||
            sourceHandle.Handle != operation.SourceBuffer.Handle ||
            operation.DestinationOwner.BufferHandle is not { } destinationHandle ||
            destinationHandle.Handle != operation.DestinationBuffer.Handle)
        {
            return false;
        }

        if ((operation.SourceOwner.LastUsageFlags &
                BufferUsageFlags.TransferSrcBit) == 0 ||
            (operation.DestinationOwner.LastUsageFlags &
                BufferUsageFlags.TransferDstBit) == 0 ||
            !IsBufferRangeValid(
                operation.SourceOwner.AllocatedByteSize,
                operation.SourceOffset,
                operation.ByteCount) ||
            !IsBufferRangeValid(
                operation.DestinationOwner.AllocatedByteSize,
                operation.DestinationOffset,
                operation.ByteCount))
        {
            return false;
        }

        return operation.SourceBuffer.Handle != operation.DestinationBuffer.Handle ||
               operation.SourceOffset >=
                   operation.DestinationOffset + operation.ByteCount ||
               operation.DestinationOffset >=
                   operation.SourceOffset + operation.ByteCount;
    }

    private static bool IsBufferRangeValid(
        ulong capacity,
        ulong offset,
        ulong count)
        => count <= capacity && offset <= capacity - count;
}
