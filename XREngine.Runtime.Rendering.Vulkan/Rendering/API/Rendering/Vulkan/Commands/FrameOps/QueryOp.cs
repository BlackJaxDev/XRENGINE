using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed record QueryOp(
    int PassIndex,
    XRFrameBuffer? Target,
    VkRenderQuery Query,
    RenderQueryDescriptor Descriptor,
    ERenderQueryOperation Operation,
    FrameOpContext Context,
    PipelineStageFlags2 TimestampStage = PipelineStageFlags2.AllCommandsBit,
    uint PointIndex = 0u,
    ReadOnlyMemory<ulong> SourceHandles = default,
    Silk.NET.Vulkan.Buffer ResultDestination = default,
    ulong ResultDestinationOffset = 0ul,
    ulong ResultStride = 0ul,
    bool IncludeAvailability = true) 
    : FrameOp(PassIndex, Target, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.Query;

    internal override int RecordPrimary(
        VulkanRenderer renderer,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (TryRecordSecondaryBucket(
                renderer,
                ref recordingState,
                in recordingInfo,
                $"Query.{Operation}",
                out int lastOperationIndex))
            return lastOperationIndex;

        switch (Operation)
        {
            case ERenderQueryOperation.Reset:
                return recordingInfo.OperationIndex;

            case ERenderQueryOperation.WriteTimestamp:
                if (recordingState.RecordingScratch.PreparedInlineQueries.Contains(Query) &&
                    Query.WriteTimestamp(
                        recordingState.CommandBuffer,
                        TimestampStage,
                        PointIndex) != ERenderQueryReadStatus.Ready)
                    recordingState.QueryFrameOpsRequireRerecordLocal = true;

                return recordingInfo.OperationIndex;

            case ERenderQueryOperation.WriteProperties:
                if (!recordingState.RecordingScratch.PreparedInlineQueries.Contains(Query) ||
                    Query.WriteProperties(
                        recordingState.CommandBuffer,
                        SourceHandles.Span) != ERenderQueryReadStatus.Ready)
                    recordingState.QueryFrameOpsRequireRerecordLocal = true;

                return recordingInfo.OperationIndex;

            case ERenderQueryOperation.CopyResults:
                if (Query.CopyResults(
                        recordingState.CommandBuffer,
                        ResultDestination,
                        ResultDestinationOffset,
                        ResultStride,
                        IncludeAvailability) != ERenderQueryReadStatus.Ready)
                    recordingState.QueryFrameOpsRequireRerecordLocal = true;

                return recordingInfo.OperationIndex;

            case ERenderQueryOperation.Begin:
                return RecordInlineQueryOperation(
                    renderer,
                    ref recordingState,
                    in recordingInfo,
                    begin: true);

            case ERenderQueryOperation.End:
                return RecordInlineQueryOperation(
                    renderer,
                    ref recordingState,
                    in recordingInfo,
                    begin: false);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(Operation),
                    Operation,
                    "Unsupported render-query operation.");
        }
    }

    private int RecordInlineQueryOperation(
        VulkanRenderer renderer,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo,
        bool begin)
    {
        bool firstBegin = begin &&
            !recordingState.RecordingScratch.BegunInlineQueries.Contains(Query);
        if (firstBegin &&
            !recordingState.RecordingScratch.PreparedInlineQueries.Contains(Query))
        {
            recordingState.QueryFrameOpsRequireRerecordLocal = true;
            Debug.VulkanWarningEvery(
                $"Vulkan.UnpreparedInlineOcclusionQuery.{Query.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Inline occlusion query begin suppressed because its pool was not prepared. Query='{0}' pass={1} op={2}.",
                Query.Data.Name ?? "<unnamed>",
                recordingInfo.PassIndex,
                recordingInfo.OperationIndex);
        }

        System.Diagnostics.Debug.Assert(
            recordingInfo.BeginsRendering,
            "Inline query begin/end primary-plan nodes must own render-scope entry.");
        if (recordingInfo.BeginsRendering &&
            (!recordingState.RenderScope.IsActive ||
             recordingState.RenderScope.Target != Target))
        {
            renderer.EndActiveRenderPass(ref recordingState);
            renderer.BeginRenderPassForTarget(
                ref recordingState,
                Target,
                recordingInfo.PassIndex,
                recordingState.ActiveContext);
        }

        bool labelActive = false;
        if (renderer.CanRecordCommandBufferDebugLabels)
        {
            labelActive = renderer.CmdBeginLabel(
                recordingState.CommandBuffer,
                $"Query.{Operation}");
        }

        if (begin)
        {
            BeginInlineQuery(
                ref recordingState,
                this,
                recordingInfo.OperationIndex,
                recordingInfo.PassIndex);
        }
        else
        {
            EndInlineQuery(
                ref recordingState,
                this,
                recordingInfo.OperationIndex,
                recordingInfo.PassIndex);
        }

        if (labelActive)
            renderer.CmdEndLabel(recordingState.CommandBuffer);

        return recordingInfo.OperationIndex;
    }

    private static void BeginInlineQuery(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        QueryOp operation,
        int operationIndex,
        int passIndex)
    {
        if (recordingState.ActiveInlineQuery is not null)
        {
            recordingState.QueryFrameOpsRequireRerecordLocal = true;
            operation.Query.InvalidateRecordedResultEpoch(
                recordingState.CommandBuffer);
            Debug.VulkanWarningEvery(
                $"Vulkan.NestedInlineQuery.{operation.Query.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.Query] Nested query begin rejected. active='{0}' requested='{1}' pass={2} op={3}.",
                recordingState.ActiveInlineQuery.Data.Name ??
                    recordingState.ActiveInlineQuery.Data.Descriptor.Kind.ToString(),
                operation.Query.Data.Name ?? operation.Descriptor.Kind.ToString(),
                passIndex,
                operationIndex);
            return;
        }

        if (recordingState.RecordingScratch.PreparedInlineQueries.Contains(operation.Query) &&
            recordingState.RecordingScratch.BegunInlineQueries.Add(operation.Query))
        {
            recordingState.ActiveInlineQuery =
                operation.Query.BeginQuery(recordingState.CommandBuffer) ==
                ERenderQueryReadStatus.Ready
                    ? operation.Query
                    : null;
            if (recordingState.ActiveInlineQuery is null)
                recordingState.QueryFrameOpsRequireRerecordLocal = true;
            recordingState.ActiveInlineQueryRecordedDraw = false;
            return;
        }

        if (!recordingState.RecordingScratch.PreparedInlineQueries.Contains(operation.Query))
            return;

        recordingState.ActiveInlineQuery = null;
        recordingState.QueryFrameOpsRequireRerecordLocal = true;
        Debug.VulkanWarningEvery(
            $"Vulkan.DuplicateInlineOcclusionQuery.{operation.Query.GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] Duplicate inline occlusion query begin suppressed in one command buffer. Query='{0}' pass={1} op={2}.",
            operation.Query.Data.Name ?? "<unnamed>",
            passIndex,
            operationIndex);
    }

    private static void EndInlineQuery(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        QueryOp operation,
        int operationIndex,
        int passIndex)
    {
        if (ReferenceEquals(recordingState.ActiveInlineQuery, operation.Query))
        {
            if (!recordingState.ActiveInlineQueryRecordedDraw)
            {
                recordingState.QueryFrameOpsRequireRerecordLocal = true;
                recordingState.ActiveInlineQuery!.InvalidateRecordedResultEpoch(
                    recordingState.CommandBuffer);
                Debug.VulkanWarningEvery(
                    $"Vulkan.EmptyInlineQuery.{recordingState.ActiveInlineQuery.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Inline occlusion query contained no recorded draw; this epoch will resolve visible. Query='{0}'.",
                    recordingState.ActiveInlineQuery.Data.Name ?? "<unnamed>");
            }

            operation.Query.EndQuery(recordingState.CommandBuffer);
            recordingState.ActiveInlineQuery = null;
            recordingState.ActiveInlineQueryRecordedDraw = false;
            return;
        }

        recordingState.QueryFrameOpsRequireRerecordLocal = true;
        operation.Query.InvalidateRecordedResultEpoch(recordingState.CommandBuffer);
        Debug.VulkanWarningEvery(
            $"Vulkan.MismatchedInlineQueryEnd.{operation.Query.GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[Vulkan.Query] Query end rejected because it does not match the active query. active='{0}' requested='{1}' pass={2} op={3}.",
            recordingState.ActiveInlineQuery?.Data.Name ?? "<none>",
            operation.Query.Data.Name ?? operation.Descriptor.Kind.ToString(),
            passIndex,
            operationIndex);
    }
}
