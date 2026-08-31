using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Opt-in, fixed-capacity native receipts for directional-shadow atlas production and sampling.
/// These receipts establish native object identity only; they do not attest to image contents.
/// </summary>
public static class VulkanShadowAtlasDiagnostics
{
    private const int ReceiptCapacity = 8;
    private const int FrameOperationReceiptCapacity = 16;
    private static readonly object Sync = new();
    private static readonly VulkanShadowAtlasWriterReceipt[] Writers = new VulkanShadowAtlasWriterReceipt[ReceiptCapacity];
    private static readonly VulkanShadowAtlasConsumerReceipt[] Consumers = new VulkanShadowAtlasConsumerReceipt[ReceiptCapacity];
    private static readonly VulkanShadowAtlasFrameOperationReceipt[] EnqueuedOperations = new VulkanShadowAtlasFrameOperationReceipt[FrameOperationReceiptCapacity];
    private static readonly VulkanShadowAtlasFrameOperationReceipt[] PrimaryOperations = new VulkanShadowAtlasFrameOperationReceipt[FrameOperationReceiptCapacity];
    private static int _writerCount;
    private static int _consumerCount;
    private static int _enqueuedOperationCount;
    private static int _primaryOperationCount;

    internal static bool IsEnabled => XREnvironment.IsEnabled(XREngineEnvironmentVariables.VulkanFrameDataReuseDiag);

    internal static void RecordWriterScope(
        string? framebufferName,
        Image image,
        ulong imageGeneration,
        ImageView imageView,
        ImageSubresourceRange viewRange,
        AttachmentLoadOp loadOp,
        AttachmentStoreOp storeOp)
    {
        if (!IsEnabled ||
            string.IsNullOrWhiteSpace(framebufferName) ||
            !framebufferName.Contains("ShadowAtlas_Directional", StringComparison.Ordinal) ||
            image.Handle == 0)
        {
            return;
        }

        lock (Sync)
        {
            if (_writerCount >= ReceiptCapacity)
                return;

            Writers[_writerCount++] = new VulkanShadowAtlasWriterReceipt(
                framebufferName,
                image.Handle,
                imageGeneration,
                imageView.Handle,
                viewRange.BaseMipLevel,
                viewRange.LevelCount,
                viewRange.BaseArrayLayer,
                viewRange.LayerCount,
                false,
                0.0f,
                0,
                0,
                0,
                0,
                loadOp.ToString(),
                storeOp.ToString());
        }
    }

    /// <summary>
    /// Correlates the depth value and clipped rectangle from an actual emitted
    /// <c>vkCmdClearAttachments</c> call with its immediately preceding writer scope.
    /// </summary>
    internal static void RecordWriterDepthClear(string? framebufferName, float depth, Rect2D clearRect)
    {
        if (!IsEnabled ||
            string.IsNullOrWhiteSpace(framebufferName) ||
            !framebufferName.Contains("ShadowAtlas_Directional", StringComparison.Ordinal))
        {
            return;
        }

        lock (Sync)
            for (int index = _writerCount - 1; index >= 0; index--)
            {
                VulkanShadowAtlasWriterReceipt receipt = Writers[index];
                if (receipt.HasExecutedDepthClear ||
                    !string.Equals(receipt.FramebufferName, framebufferName, StringComparison.Ordinal))
                {
                    continue;
                }

                Writers[index] = receipt with
                {
                    HasExecutedDepthClear = true,
                    ExecutedClearDepth = depth,
                    ClearOffsetX = clearRect.Offset.X,
                    ClearOffsetY = clearRect.Offset.Y,
                    ClearWidth = clearRect.Extent.Width,
                    ClearHeight = clearRect.Extent.Height,
                };
                return;
            }
    }

    internal static void RecordDirectionalShadowAtlasConsumer(
        string? bindingName,
        Image image,
        ulong imageGeneration,
        ImageView imageView,
        ulong imageViewGeneration,
        in ImageSubresourceRange viewRange)
    {
        if (!IsEnabled ||
            !string.Equals(bindingName, "DirectionalShadowAtlas", StringComparison.Ordinal) ||
            image.Handle == 0)
        {
            return;
        }

        lock (Sync)
        {
            if (_consumerCount >= ReceiptCapacity)
                return;

            Consumers[_consumerCount++] = new VulkanShadowAtlasConsumerReceipt(
                bindingName!,
                image.Handle,
                imageGeneration,
                imageViewGeneration,
                imageView.Handle,
                viewRange.BaseMipLevel,
                viewRange.LevelCount,
                viewRange.BaseArrayLayer,
                viewRange.LayerCount);
        }
    }

    internal static void RecordEnqueuedOperation(FrameOp operation)
        => RecordFrameOperation(
            EnqueuedOperations,
            ref _enqueuedOperationCount,
            EVulkanShadowAtlasFrameOperationReceiptStage.Enqueued,
            operation.Kind,
            operation.PassIndex,
            operation.Target);

    internal static void RecordPrimaryOperation(
        EVulkanShadowAtlasFrameOperationReceiptStage stage,
        EVulkanPrimaryPlanNodeKind operationKind,
        int passIndex,
        XRFrameBuffer? target)
        => RecordFrameOperation(
            PrimaryOperations,
            ref _primaryOperationCount,
            stage,
            operationKind,
            passIndex,
            target);

    private static void RecordFrameOperation(
        VulkanShadowAtlasFrameOperationReceipt[] destination,
        ref int count,
        EVulkanShadowAtlasFrameOperationReceiptStage stage,
        EVulkanPrimaryPlanNodeKind operationKind,
        int passIndex,
        XRFrameBuffer? target)
    {
        if (!IsEnabled ||
            target?.Name is not { } targetName ||
            !targetName.Contains("ShadowAtlas_Directional", StringComparison.Ordinal))
        {
            return;
        }

        lock (Sync)
        {
            if (count >= destination.Length)
                return;

            destination[count++] = new VulkanShadowAtlasFrameOperationReceipt(
                stage,
                GetOperationKindName(operationKind),
                passIndex,
                target.GetHashCode(),
                targetName);
        }
    }

    private static string GetOperationKindName(EVulkanPrimaryPlanNodeKind operationKind)
        => operationKind switch
        {
            EVulkanPrimaryPlanNodeKind.MeshDraw => "MeshDraw",
            EVulkanPrimaryPlanNodeKind.IndirectDraw => "IndirectDraw",
            EVulkanPrimaryPlanNodeKind.Clear => "Clear",
            EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling => "PublishFramebufferForSampling",
            EVulkanPrimaryPlanNodeKind.MemoryBarrier => "MemoryBarrier",
            EVulkanPrimaryPlanNodeKind.SubmissionMarker => "SubmissionMarker",
            EVulkanPrimaryPlanNodeKind.BufferCopy => "BufferCopy",
            EVulkanPrimaryPlanNodeKind.ComputeDispatch => "ComputeDispatch",
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect => "ComputeDispatchIndirect",
            EVulkanPrimaryPlanNodeKind.TextureUpload => "TextureUpload",
            EVulkanPrimaryPlanNodeKind.Query => "Query",
            EVulkanPrimaryPlanNodeKind.TransformFeedback => "TransformFeedback",
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount => "MeshTaskDispatchIndirectCount",
            EVulkanPrimaryPlanNodeKind.DlssUpscale => "DlssUpscale",
            EVulkanPrimaryPlanNodeKind.DlssFrameGeneration => "DlssFrameGeneration",
            EVulkanPrimaryPlanNodeKind.AdvancedVisibility => "AdvancedVisibility",
            EVulkanPrimaryPlanNodeKind.Blit => "Blit",
            _ => "Unknown",
        };

    /// <summary>Returns a point-in-time copy of the one-shot native receipts.</summary>
    public static VulkanShadowAtlasDiagnosticSnapshot GetSnapshot()
    {
        lock (Sync)
        {
            var writers = new VulkanShadowAtlasWriterReceipt[_writerCount];
            var consumers = new VulkanShadowAtlasConsumerReceipt[_consumerCount];
            var enqueuedOperations = new VulkanShadowAtlasFrameOperationReceipt[_enqueuedOperationCount];
            var primaryOperations = new VulkanShadowAtlasFrameOperationReceipt[_primaryOperationCount];
            Array.Copy(Writers, writers, _writerCount);
            Array.Copy(Consumers, consumers, _consumerCount);
            Array.Copy(EnqueuedOperations, enqueuedOperations, _enqueuedOperationCount);
            Array.Copy(PrimaryOperations, primaryOperations, _primaryOperationCount);
            return new VulkanShadowAtlasDiagnosticSnapshot(
                IsEnabled,
                writers,
                consumers,
                enqueuedOperations,
                primaryOperations);
        }
    }
}
