using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns persistent queue synchronization, image-state tracking, and submission
/// marker storage used by the command authority.
/// </summary>
internal sealed unsafe class VulkanCommandSynchronizationState
{
    private const int QueueOperationHistoryCapacity = 64;

    internal Semaphore[]? acquireBridgeSemaphores;
    internal Semaphore _graphicsTimelineSemaphore;
    internal Semaphore _presentTimelineSemaphore;
    internal Semaphore _transferTimelineSemaphore;
    internal ulong[]? _frameSlotTimelineValues;
    internal ulong _acquireTimelineValue;
    internal ulong _graphicsTimelineValue;
    internal readonly VulkanSynchronizationThreadWorkspace _synchronizationThreadWorkspace = new();
    internal EVulkanSynchronizationBackend _activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
    internal readonly object _vulkanImageLayoutLock = new();
    internal readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageSubresourceState> _trackedImageSubresourceStates = new();
    internal readonly Dictionary<ulong, (ulong ResourceGeneration, EVulkanExternalImageOwnership Ownership)> _externalImageOwnershipByHandle = new();
    internal readonly Dictionary<ulong, VulkanRecordedImageLayoutState> _recordedImageLayoutsByCommandBuffer = new();
    internal readonly VulkanQueueOperationRecord[] _vulkanQueueOperationHistory =
        new VulkanQueueOperationRecord[QueueOperationHistoryCapacity];
    internal long _vulkanQueueOperationSerial;
    internal readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> _submissionImageStateScratch = new(64);
    internal readonly List<VulkanQueueSemaphoreRequirement> _submissionQueueSemaphoreRequirements = new(8);
    internal readonly object _submissionMarkerLock = new();
    internal readonly Dictionary<nint, List<VulkanRenderer.VulkanTimelineGpuFence>> _submissionMarkersByCommandBuffer = [];
    internal readonly Stack<VulkanRenderer.VulkanTimelineGpuFence> _timelineGpuFencePool = [];

    internal void RecordQueueOperation(
        EVulkanDeviceState deviceState,
        string operation,
        Queue queue,
        Result result,
        ulong submissionSerial,
        string? caller)
    {
        long serial = Interlocked.Increment(ref _vulkanQueueOperationSerial);
        int index = unchecked((int)((serial - 1) % QueueOperationHistoryCapacity));
        _vulkanQueueOperationHistory[index] = new VulkanQueueOperationRecord(
            unchecked((ulong)serial),
            operation,
            unchecked((ulong)queue.Handle),
            result,
            deviceState,
            submissionSerial,
            Environment.CurrentManagedThreadId,
            caller);
    }

    internal void FailAllSubmissionMarkers()
    {
        lock (_submissionMarkerLock)
        {
            foreach (List<VulkanRenderer.VulkanTimelineGpuFence> markers in _submissionMarkersByCommandBuffer.Values)
            {
                for (int index = 0; index < markers.Count; index++)
                    markers[index].Fail();
            }

            _submissionMarkersByCommandBuffer.Clear();
        }
    }

    internal void RemoveRecordedImageLayouts(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        lock (_vulkanImageLayoutLock)
            _recordedImageLayoutsByCommandBuffer.Remove(
                unchecked((ulong)commandBuffer.Handle));
    }

    internal static void FailUnsubmittedSubmissionMarkers(
        ReadOnlySpan<FrameOp> frameOperations)
    {
        for (int index = 0; index < frameOperations.Length; index++)
        {
            if (frameOperations[index] is SubmissionMarkerOp marker)
                marker.Fence.Fail();
        }
    }

    internal Result QueryTimelineCompletion(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanResourceLifetimeTracker lifetimeTracker,
        Semaphore semaphore,
        ulong value,
        out bool completed)
    {
        ulong currentValue = 0;
        Result result = api.GetSemaphoreCounterValue(
            deviceContext.Device,
            semaphore,
            &currentValue);
        deviceContext.ObserveNativeResult("vkGetSemaphoreCounterValue", result);
        completed = result == Result.Success && currentValue >= value;
        if (completed)
            CompleteTimelineSubmissions(lifetimeTracker, semaphore, currentValue);
        return result;
    }

    internal Result WaitForTimelineCompletion(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanResourceLifetimeTracker lifetimeTracker,
        Semaphore semaphore,
        ulong value,
        ulong timeoutNanoseconds)
    {
        SemaphoreWaitInfo waitInfo = new()
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
        };

        Semaphore* semaphorePtr = stackalloc Semaphore[1];
        ulong* valuePtr = stackalloc ulong[1];
        semaphorePtr[0] = semaphore;
        valuePtr[0] = value;
        waitInfo.PSemaphores = semaphorePtr;
        waitInfo.PValues = valuePtr;

        Result result = api.WaitSemaphores(
            deviceContext.Device,
            &waitInfo,
            timeoutNanoseconds);
        deviceContext.ObserveNativeResult("vkWaitSemaphores", result);
        if (result == Result.Success)
            CompleteTimelineSubmissions(lifetimeTracker, semaphore, value);
        return result;
    }

    private void CompleteTimelineSubmissions(
        VulkanResourceLifetimeTracker lifetimeTracker,
        Semaphore semaphore,
        ulong value)
    {
        ulong handle = semaphore.Handle;
        lock (lifetimeTracker.SyncRoot)
        {
            for (int index = lifetimeTracker.LifetimeSubmissions.Count - 1; index >= 0; index--)
            {
                VulkanLifetimeSubmission submission = lifetimeTracker.LifetimeSubmissions[index];
                if (submission.TimelineSemaphoreHandle != handle ||
                    submission.TimelineValue == 0 ||
                    submission.TimelineValue > value)
                {
                    continue;
                }

                lifetimeTracker.MarkQueueSequenceCompletedNoLock(
                    submission.QueueDomain,
                    submission.QueueSequence);
                lifetimeTracker.LifetimeSubmissions.RemoveAt(index);
            }
        }

        AdvanceCompletedImageLayouts(lifetimeTracker);
    }

    private void AdvanceCompletedImageLayouts(
        VulkanResourceLifetimeTracker lifetimeTracker)
    {
        ulong completedGraphics;
        ulong completedTransfer;
        ulong completedOther;
        lock (lifetimeTracker.SyncRoot)
        {
            completedGraphics = lifetimeTracker.CompletedGraphicsSequence;
            completedTransfer = lifetimeTracker.CompletedTransferSequence;
            completedOther = lifetimeTracker.CompletedOtherSequence;
        }

        lock (_vulkanImageLayoutLock)
        {
            foreach (VulkanImageSubresourceState state in _trackedImageSubresourceStates.Values)
            {
                if (state.GraphicsSequence <= completedGraphics &&
                    state.TransferSequence <= completedTransfer &&
                    state.OtherSequence <= completedOther)
                {
                    state.Completed = state.Submitted;
                }
            }
        }
    }
}
