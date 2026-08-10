using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    private readonly object _retiredSynchronousSubmissionsLock = new();
    // The bounded producers are one exclusive synchronous arena operation plus the finite
    // readback rings. Storage is allocated with the command runtime, never after acceptance.
    private readonly VulkanRetiredSynchronousSubmission[] _retiredSynchronousSubmissions = new VulkanRetiredSynchronousSubmission[256];

    internal bool RetireIncompleteSynchronousSubmission(
        CommandBuffer commandBuffer,
        CommandPool commandPool,
        Fence fence,
        VulkanFrameDataArena? arena,
        in VulkanFrameDataSlice slice,
        bool removeOneTimeOwner,
        string owner,
        bool completeSynchronousLifetime = false,
        int frameSlotLifetime = -1)
    {
        if (!DeviceContext.IsOperational)
        {
            if (removeOneTimeOwner)
            {
                lock (_oneTimeCommandPoolsLock)
                    _oneTimeCommandPools.Remove(commandBuffer.Handle);
            }
            RemoveCommandBufferBindState(commandBuffer);
            return true;
        }

        VulkanRetiredSynchronousSubmission debt = new(
            commandBuffer,
            commandPool,
            fence,
            arena,
            in slice,
            removeOneTimeOwner,
            completeSynchronousLifetime,
            frameSlotLifetime,
            owner);
        lock (_retiredSynchronousSubmissionsLock)
        {
            for (int index = 0; index < _retiredSynchronousSubmissions.Length; index++)
            {
                if (_retiredSynchronousSubmissions[index].IsValid)
                    continue;
                _retiredSynchronousSubmissions[index] = debt;
                return true;
            }
        }

        Debug.VulkanWarning(
            "[Vulkan] The bounded synchronous-submission debt ledger is full; accepted native ownership remains intentionally unreleased.");
        return false;
    }

    /// <summary>Retries accepted synchronous work whose initiating wait could not settle ownership.</summary>
    internal void DrainRetiredSynchronousSubmissions()
    {
        lock (_retiredSynchronousSubmissionsLock)
        {
            if (!DeviceContext.IsOperational)
                return;

            for (int index = _retiredSynchronousSubmissions.Length - 1; index >= 0; index--)
            {
                VulkanRetiredSynchronousSubmission debt = _retiredSynchronousSubmissions[index];
                if (!debt.IsValid)
                    continue;
                if (debt.Fence.Handle == 0)
                    continue;

                Result status = Api.GetFenceStatus(DeviceContext.Device, debt.Fence);
                DeviceContext.ObserveNativeResult($"vkGetFenceStatus.{debt.Owner}.Retired", status);
                if (status == Result.NotReady)
                    continue;
                if (status != Result.Success)
                    continue;

                CompleteTrackedFence(debt.Fence);
                if (debt.Arena is not null &&
                    !debt.Arena.TryResetFrameSlot(
                        checked((uint)debt.Slice.FrameSlot),
                        debt.Slice.Generation,
                        submissionCompletionProven: true))
                {
                    continue;
                }

                Api.DestroyFence(DeviceContext.Device, debt.Fence, null);
                CommandBuffer commandBuffer = debt.CommandBuffer;
                if (debt.RemoveOneTimeOwner)
                {
                    lock (_oneTimeCommandPoolsLock)
                        _oneTimeCommandPools.Remove(commandBuffer.Handle);
                }
                RemoveCommandBufferBindState(commandBuffer);
                if (debt.FrameSlotLifetime >= 0)
                {
                    FreeCommandBufferWithLifetime(
                        debt.FrameSlotLifetime,
                        debt.CommandPool,
                        ref commandBuffer,
                        $"{debt.Owner}.RetiredCompleted");
                }
                else if (debt.CompleteSynchronousLifetime)
                {
                    lock (Pools.Gate)
                        Api.FreeCommandBuffers(DeviceContext.Device, debt.CommandPool, 1, ref commandBuffer);
                    ResourceRuntime.CompleteSynchronousCommandBuffer(debt.CommandBuffer);
                }
                else
                {
                    FreeVulkanCommandBufferTracked(
                        debt.CommandPool,
                        ref commandBuffer,
                        $"{debt.Owner}.RetiredCompleted");
                }
                _retiredSynchronousSubmissions[index] = default;
            }
        }
    }

    /// <summary>
    /// Drops managed references after terminal device loss. Native objects belong to the lost
    /// device and are intentionally left to logical-device destruction.
    /// </summary>
    internal void AbandonRetiredSynchronousSubmissionsAfterDeviceLoss()
    {
        lock (_retiredSynchronousSubmissionsLock)
        {
            for (int index = 0; index < _retiredSynchronousSubmissions.Length; index++)
            {
                VulkanRetiredSynchronousSubmission debt = _retiredSynchronousSubmissions[index];
                if (!debt.IsValid)
                    continue;
                if (debt.RemoveOneTimeOwner)
                {
                    lock (_oneTimeCommandPoolsLock)
                        _oneTimeCommandPools.Remove(debt.CommandBuffer.Handle);
                }
                RemoveCommandBufferBindState(debt.CommandBuffer);
            }
            Array.Clear(_retiredSynchronousSubmissions);
        }
    }
}
