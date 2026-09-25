namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Publishes a required producer only when its callback and every deferred
    /// mesh request form one complete ordered cohort. Failed cohorts are rolled
    /// back before they can enter the shared frame-operation stream.
    /// </summary>
    internal bool TryExecuteRequiredGpuProducerBatch(
        Func<bool> producer,
        XRFrameBuffer? samplingTarget,
        out XRGpuFence? retentionFence,
        out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(producer);
        retentionFence = null;
        failure = null;

        if (!_frameOperationQueue.TryBeginOrderedBatch())
        {
            failure = new InvalidOperationException(
                "Another ordered Vulkan frame-operation batch is already active on this recording thread.");
            return false;
        }

        bool batchActive = true;
        int capturedRequestCount = 0;
        ulong captureLeaseToken = 0UL;
        try
        {
            capturedRequestCount = MeshOperationRequests.CaptureTo(
                producer,
                _meshOperationRequestScratch,
                out bool producerComplete,
                out captureLeaseToken,
                out VulkanMeshRequestLaneCapacityFailure capacityFailure);
            if (capturedRequestCount < 0)
            {
                failure = CreateRequiredProducerCaptureCapacityFailure(
                    in capacityFailure);
                return false;
            }

            if (!producerComplete)
                return false;

            if (!MaterializeQueuedMeshRenderRequests(
                    capturedRequestCount,
                    allowPreparedCohort: false,
                    out string materializationFailure,
                    foregroundRequired: false,
                    requireCompleteCohort: true))
            {
                failure = new InvalidOperationException(
                    string.IsNullOrEmpty(materializationFailure)
                        ? "The required Vulkan GPU producer cohort did not materialize completely."
                        : materializationFailure);
                return false;
            }

            if (samplingTarget is not null)
            {
                if (!_frameOperationQueue.RequiredOrderedBatchHasWriter(
                        samplingTarget))
                {
                    failure = new InvalidOperationException(
                        "The required Vulkan GPU producer did not materialize a framebuffer writer before sampling publication.");
                    return false;
                }
                PublishFrameBufferAttachmentsForSampling(samplingTarget);
            }

            if (!_frameOperationQueue.TryGetRequiredOrderedBatchOperationCount(
                    out int markerPassIndex,
                    out FrameOpContext markerContext,
                    out int requiredOperationCount,
                    out string cohortFailure))
            {
                failure = new InvalidOperationException(cohortFailure);
                return false;
            }

            EnqueueFrameOp(VulkanCommandRuntime.CreateMemoryBarrierOperation(
                markerPassIndex,
                EMemoryBarrierMask.Framebuffer |
                EMemoryBarrierMask.TextureFetch |
                EMemoryBarrierMask.TextureUpdate,
                markerContext));

            if (_commandRuntime.TryEnqueueOrderedComputeFence(
                    _frameOperationQueue,
                    markerPassIndex,
                    markerContext,
                    requiredOperationCount + 1) is not
                VulkanTimelineGpuFence fence)
            {
                failure = new InvalidOperationException(
                    "The required Vulkan GPU producer batch could not allocate its timeline fence.");
                return false;
            }
            _frameOperationQueue.CommitOrderedBatch();
            batchActive = false;
            retentionFence = fence;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
        finally
        {
            if (batchActive)
            {
                try
                {
                    _frameOperationQueue.RollbackOrderedBatch();
                }
                catch (Exception ex)
                {
                    failure = CombineRequiredProducerFailure(failure, ex);
                }
            }

            if (capturedRequestCount > 0)
            {
                _meshOperationRequestScratch
                    .AsSpan(0, capturedRequestCount)
                    .Clear();
                _meshOperationMaterializationScratch
                    .AsSpan(0, capturedRequestCount)
                    .Clear();
                _meshOperationCohortEntryScratch
                    .AsSpan(0, capturedRequestCount)
                    .Clear();
            }

            if (batchActive && captureLeaseToken != 0UL)
            {
                try
                {
                    MeshOperationRequests.ReleaseCapturePublicationLeases(captureLeaseToken);
                }
                catch (Exception ex)
                {
                    failure = CombineRequiredProducerFailure(failure, ex);
                }
            }
        }
    }

    private static Exception CombineRequiredProducerFailure(
        Exception? current,
        Exception next)
        => current is null
            ? next
            : new AggregateException(current, next);

    private static Exception CreateRequiredProducerCaptureCapacityFailure(
        in VulkanMeshRequestLaneCapacityFailure capacityFailure)
    {
        if (!capacityFailure.HasFailure)
        {
            return new InvalidOperationException(
                "Required Vulkan GPU producer capture failed without a typed capacity record.");
        }

        return new VulkanAcceptedFramePlanCapacityException(
            capacityFailure.AcceptedFrameLane,
            capacityFailure.ConfiguredCapacity,
            capacityFailure.RequiredCapacity,
            $"capture='required-gpu-producer' meshLane={capacityFailure.Lane} " +
            $"accepted={capacityFailure.ActualOccupancy} " +
            $"rejected={capacityFailure.OverflowCount}.");
    }
}
