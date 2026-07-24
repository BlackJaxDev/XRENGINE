using Silk.NET.Vulkan;
using System.Threading;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly object _submissionMarkerLock = new();
    private readonly Dictionary<nint, List<VulkanTimelineGpuFence>> _submissionMarkersByCommandBuffer = [];
    private readonly Stack<VulkanTimelineGpuFence> _timelineGpuFencePool = [];

    private VulkanTimelineGpuFence RentTimelineGpuFence()
    {
        lock (_submissionMarkerLock)
        {
            VulkanTimelineGpuFence fence = _timelineGpuFencePool.Count > 0
                ? _timelineGpuFencePool.Pop()
                : new VulkanTimelineGpuFence();
            fence.Reset(this);
            return fence;
        }
    }

    private void ReturnTimelineGpuFence(VulkanTimelineGpuFence fence)
    {
        lock (_submissionMarkerLock)
            _timelineGpuFencePool.Push(fence);
    }

    private void RegisterSubmissionMarker(CommandBuffer commandBuffer, VulkanTimelineGpuFence fence)
    {
        lock (_submissionMarkerLock)
        {
            if (!_submissionMarkersByCommandBuffer.TryGetValue(commandBuffer.Handle, out List<VulkanTimelineGpuFence>? markers))
            {
                markers = [];
                _submissionMarkersByCommandBuffer.Add(commandBuffer.Handle, markers);
            }

            markers.Add(fence);
        }
    }

    private void ResetSubmissionMarkersForCommandBuffer(CommandBuffer commandBuffer)
    {
        lock (_submissionMarkerLock)
        {
            if (!_submissionMarkersByCommandBuffer.TryGetValue(commandBuffer.Handle, out List<VulkanTimelineGpuFence>? markers))
                return;

            for (int i = 0; i < markers.Count; i++)
                markers[i].Fail();
            markers.Clear();
        }
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

        lock (_submissionMarkerLock)
        {
            for (uint commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                nint commandBufferHandle = submitInfo.PCommandBuffers[commandIndex].Handle;
                if (!_submissionMarkersByCommandBuffer.TryGetValue(commandBufferHandle, out List<VulkanTimelineGpuFence>? markers))
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
        lock (_submissionMarkerLock)
        {
            foreach (List<VulkanTimelineGpuFence> markers in _submissionMarkersByCommandBuffer.Values)
                for (int i = 0; i < markers.Count; i++)
                    markers[i].Fail();

            _submissionMarkersByCommandBuffer.Clear();
        }
    }

    internal sealed class VulkanTimelineGpuFence : XRGpuFence
    {
        private VulkanRenderer? _renderer;
        private ulong _semaphoreHandle;
        private ulong _timelineValue;
        private int _state;

        internal void Reset(VulkanRenderer renderer)
        {
            ResetForReuse();
            _renderer = renderer;
            _semaphoreHandle = 0;
            _timelineValue = 0;
            Volatile.Write(ref _state, 0);
        }

        internal void Bind(ulong semaphoreHandle, ulong timelineValue)
        {
            if (semaphoreHandle == 0 || timelineValue == 0)
            {
                Fail();
                return;
            }

            _semaphoreHandle = semaphoreHandle;
            _timelineValue = timelineValue;
            Volatile.Write(ref _state, 1);
        }

        internal void Fail()
            => Volatile.Write(ref _state, 2);

        protected override EGpuFenceStatus PollCore()
        {
            if (Volatile.Read(ref _state) == 2)
                return EGpuFenceStatus.Failed;

            VulkanRenderer? currentRenderer = _renderer;
            if (currentRenderer is null || !currentRenderer.IsDeviceOperational)
            {
                Fail();
                return EGpuFenceStatus.Failed;
            }

            ulong semaphoreHandle = _semaphoreHandle;
            ulong timelineValue = _timelineValue;
            if (Volatile.Read(ref _state) == 0 || semaphoreHandle == 0 || timelineValue == 0)
                return EGpuFenceStatus.Pending;

            try
            {
                return currentRenderer.HasTimelineValueCompleted(new Semaphore(semaphoreHandle), timelineValue)
                    ? EGpuFenceStatus.Signaled
                    : EGpuFenceStatus.Pending;
            }
            catch
            {
                Fail();
                return EGpuFenceStatus.Failed;
            }
        }

        protected override void DisposeCore()
        {
            VulkanRenderer? owner = _renderer;
            bool reusable = Volatile.Read(ref _state) != 0;
            _renderer = null;
            _semaphoreHandle = 0;
            _timelineValue = 0;
            Fail();
            if (reusable && owner is not null)
                owner.ReturnTimelineGpuFence(this);
        }
    }
}
