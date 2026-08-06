using Silk.NET.Vulkan;
using System.Threading;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    private VulkanTimelineGpuFence RentTimelineGpuFence()
    {
        lock (_commandRuntime.Synchronization._submissionMarkerLock)
        {
            VulkanTimelineGpuFence fence = _commandRuntime.Synchronization._timelineGpuFencePool.Count > 0
                ? _commandRuntime.Synchronization._timelineGpuFencePool.Pop()
                : new VulkanTimelineGpuFence();
            fence.Reset(this);
            return fence;
        }
    }

    private void ReturnTimelineGpuFence(VulkanTimelineGpuFence fence)
    {
        lock (_commandRuntime.Synchronization._submissionMarkerLock)
            _commandRuntime.Synchronization._timelineGpuFencePool.Push(fence);
    }

    internal void RegisterSubmissionMarker(CommandBuffer commandBuffer, VulkanTimelineGpuFence fence)
    {
        lock (_commandRuntime.Synchronization._submissionMarkerLock)
        {
            if (!_commandRuntime.Synchronization._submissionMarkersByCommandBuffer.TryGetValue(commandBuffer.Handle, out List<VulkanTimelineGpuFence>? markers))
            {
                markers = [];
                _commandRuntime.Synchronization._submissionMarkersByCommandBuffer.Add(commandBuffer.Handle, markers);
            }

            markers.Add(fence);
        }
    }

    private void ResetSubmissionMarkersForCommandBuffer(CommandBuffer commandBuffer)
    {
        lock (_commandRuntime.Synchronization._submissionMarkerLock)
        {
            if (!_commandRuntime.Synchronization._submissionMarkersByCommandBuffer.TryGetValue(commandBuffer.Handle, out List<VulkanTimelineGpuFence>? markers))
                return;

            for (int i = 0; i < markers.Count; i++)
                markers[i].Fail();
            markers.Clear();
        }
    }

    /// <summary>
    /// Associates the current frame's CPU fence objects with a replayed command
    /// buffer. Submission markers do not encode Vulkan commands; their position
    /// only contributes a render-pass boundary to the structural frame-op
    /// signature. Rebinding them here lets stable GPU-driven primaries replay
    /// while each caller still receives the timeline value from this submission.
    /// </summary>
    private void PrepareSubmissionMarkersForCommandBufferReuse(
        CommandBuffer commandBuffer,
        ReadOnlySpan<FrameOp> frameOps,
        ReadOnlySpan<FrameOp> dynamicUiFrameOps)
    {
        ResetSubmissionMarkersForCommandBuffer(commandBuffer);
        RegisterSubmissionMarkersForCommandBuffer(commandBuffer, frameOps);
        RegisterSubmissionMarkersForCommandBuffer(commandBuffer, dynamicUiFrameOps);
    }

    private void PrepareSubmissionMarkersForCommandBufferReuse(
        CommandBuffer commandBuffer,
        ReadOnlySpan<FrameOp> frameOps)
    {
        ResetSubmissionMarkersForCommandBuffer(commandBuffer);
        RegisterSubmissionMarkersForCommandBuffer(commandBuffer, frameOps);
    }

    private void RegisterSubmissionMarkersForCommandBuffer(
        CommandBuffer commandBuffer,
        ReadOnlySpan<FrameOp> frameOps)
    {
        for (int index = 0; index < frameOps.Length; index++)
            if (frameOps[index] is SubmissionMarkerOp marker)
                RegisterSubmissionMarker(commandBuffer, marker.Fence);
    }

    /// <summary>
    /// Fails markers whose drained frame operations could not be recorded into a
    /// command buffer. Without this abort path, those fences remain permanently
    /// unbound because no command-buffer handle exists for submit/reset cleanup.
    /// </summary>
    private static void FailUnsubmittedSubmissionMarkers(
        ReadOnlySpan<FrameOp> frameOps,
        ReadOnlySpan<FrameOp> dynamicUiFrameOps)
    {
        FailUnsubmittedSubmissionMarkers(frameOps);
        FailUnsubmittedSubmissionMarkers(dynamicUiFrameOps);
    }

    private static void FailUnsubmittedSubmissionMarkers(ReadOnlySpan<FrameOp> frameOps)
    {
        for (int index = 0; index < frameOps.Length; index++)
            if (frameOps[index] is SubmissionMarkerOp marker)
                marker.Fence.Fail();
    }

    private void ResolveSubmissionMarkers(ref SubmitInfo submitInfo, bool submissionSucceeded)
    {
        if (submitInfo.CommandBufferCount == 0 || submitInfo.PCommandBuffers is null)
            return;

        ulong semaphoreHandle = 0;
        ulong timelineValue = 0;
        if (submissionSucceeded)
            ResolveSubmissionTimelineSignal(ref submitInfo, out semaphoreHandle, out timelineValue);

        lock (_commandRuntime.Synchronization._submissionMarkerLock)
        {
            for (uint commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                nint commandBufferHandle = submitInfo.PCommandBuffers[commandIndex].Handle;
                if (!_commandRuntime.Synchronization._submissionMarkersByCommandBuffer.TryGetValue(commandBufferHandle, out List<VulkanTimelineGpuFence>? markers))
                    continue;

                bool canBind = submissionSucceeded && semaphoreHandle != 0 && timelineValue != 0;
                for (int markerIndex = 0; markerIndex < markers.Count; markerIndex++)
                {
                    if (canBind)
                        markers[markerIndex].Bind(semaphoreHandle, timelineValue);
                    else
                        markers[markerIndex].Fail();
                }
                markers.Clear();
            }
        }
    }

    private void FailAllSubmissionMarkers()
    {
        lock (_commandRuntime.Synchronization._submissionMarkerLock)
        {
            foreach (List<VulkanTimelineGpuFence> markers in _commandRuntime.Synchronization._submissionMarkersByCommandBuffer.Values)
                for (int i = 0; i < markers.Count; i++)
                    markers[i].Fail();

            _commandRuntime.Synchronization._submissionMarkersByCommandBuffer.Clear();
        }
    }

}
