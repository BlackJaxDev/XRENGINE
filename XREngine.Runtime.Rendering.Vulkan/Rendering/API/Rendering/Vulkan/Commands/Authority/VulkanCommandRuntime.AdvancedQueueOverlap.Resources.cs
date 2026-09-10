using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private VulkanAdvancedQueueOverlapSlot?[]? _advancedQueueOverlapSlots;

    /// <summary>
    /// Obtains the retained resources for a frame slot after its prior accepted
    /// prefixes have been completed by the frame-loop completion authority.
    /// </summary>
    internal VulkanAdvancedQueueOverlapSlot PrepareAdvancedQueueOverlapSlot(
        uint frameSlot,
        CommandBuffer final)
    {
        if (final.Handle == 0)
            throw new ArgumentException("A final graphics primary is required.", nameof(final));

        VulkanAdvancedQueueOverlapSlot slot = GetOrCreateAdvancedQueueOverlapSlot(frameSlot);
        CompleteAdvancedQueueOverlapSlot(frameSlot);
        EnsureAdvancedQueueOverlapResources(slot);
        slot.ResetForRecording(final);
        return slot;
    }

    internal bool TryGetAdvancedQueueOverlapSlot(
        CommandBuffer final,
        out VulkanAdvancedQueueOverlapSlot slot)
    {
        VulkanAdvancedQueueOverlapSlot?[]? slots = _advancedQueueOverlapSlots;
        if (slots is not null)
        {
            for (int index = 0; index < slots.Length; index++)
            {
                VulkanAdvancedQueueOverlapSlot? candidate = slots[index];
                if (candidate is not null && candidate.Recorded &&
                    candidate.Final.Handle == final.Handle)
                {
                    slot = candidate;
                    return true;
                }
            }
        }

        slot = null!;
        return false;
    }

    /// <summary>
    /// Completes all accepted prefix submissions for a reused frame slot. This
    /// is called after the caller has established its normal frame-slot
    /// completion point; the independent queue still needs an explicit fence.
    /// </summary>
    internal unsafe void CompleteAdvancedQueueOverlapSlot(uint frameSlot)
    {
        VulkanAdvancedQueueOverlapSlot?[]? slots = _advancedQueueOverlapSlots;
        if (slots is null || frameSlot >= (uint)slots.Length ||
            slots[frameSlot] is not VulkanAdvancedQueueOverlapSlot slot ||
            !slot.HasAcceptedPrefixes)
            return;

        if (!DeviceContext.IsOperational)
            return;

        if (slot.FinalAccepted)
        {
            Result finalStatus = Api.GetFenceStatus(DeviceContext.Device, slot.FinalFence);
            if (finalStatus != Result.Success)
            {
                if (finalStatus is not Result.NotReady and not Result.Timeout)
                    DeviceContext.ObserveNativeResult(
                        "vkGetFenceStatus.AdvancedQueueOverlap.Final",
                        finalStatus);
                if (!DeviceContext.IsOperational)
                    return;
                throw new InvalidOperationException(
                    "Advanced frame-slot completion must be proven before releasing its prefix resources.");
            }
        }
        CompleteAdvancedQueueOverlapFence(slot, slot.PrefixFence, ref slot.PrefixAccepted, "Prefix");
        if (!DeviceContext.IsOperational)
            return;
        CompleteAdvancedQueueOverlapFence(slot, slot.ClassificationFence, ref slot.ClassificationAccepted, "Classification");
        if (!DeviceContext.IsOperational)
            return;
        CompleteAdvancedQueueOverlapFence(slot, slot.IndependentFence, ref slot.IndependentAccepted, "Independent");
        slot.FinalAccepted = false;
    }

    /// <summary>
    /// Settles a split submission after the ordinary final submit is known. A
    /// rejected final submit leaves binary semaphores signaled, so completion is
    /// proven before those semaphores are replaced.
    /// </summary>
    internal void CompleteAdvancedQueueOverlapSubmission(
        VulkanAdvancedQueueOverlapSlot slot,
        bool finalAccepted)
    {
        ArgumentNullException.ThrowIfNull(slot);
        slot.FinalAccepted = finalAccepted;
        slot.Recorded = false;
        if (finalAccepted)
            return;

        bool recreateSemaphores = slot.PrefixAccepted || slot.ClassificationAccepted;
        CompleteAdvancedQueueOverlapSlot(slot.FrameSlot);
        if (!DeviceContext.IsOperational)
            return;

        if (recreateSemaphores)
            RecreateAdvancedQueueOverlapSemaphores(slot);
    }

    internal void DestroyAdvancedQueueOverlapResources()
    {
        VulkanAdvancedQueueOverlapSlot?[]? slots = _advancedQueueOverlapSlots;
        if (slots is null)
            return;

        if (!DeviceContext.IsOperational)
        {
            AbandonAdvancedQueueOverlapResourcesAfterDeviceLoss();
            return;
        }

        bool hasPendingResources = false;
        for (int index = 0; index < slots.Length; index++)
        {
            VulkanAdvancedQueueOverlapSlot? slot = slots[index];
            if (slot is null)
                continue;

            // Never free native artifacts whose submissions are not proven
            // complete. Device-loss retirement owns those handles.
            if (slot.HasAcceptedPrefixes || slot.FinalAccepted)
            {
                if (!AreAdvancedQueueOverlapFencesSignaled(slot))
                {
                    if (!DeviceContext.IsOperational)
                    {
                        AbandonAdvancedQueueOverlapResourcesAfterDeviceLoss();
                        return;
                    }
                    hasPendingResources = true;
                    continue;
                }
                CompleteAdvancedQueueOverlapSlot(slot.FrameSlot);
            }

            AbandonAdvancedQueueOverlapRecordedCommandBuffer(slot.Prefix);
            AbandonAdvancedQueueOverlapRecordedCommandBuffer(slot.Classification);
            AbandonAdvancedQueueOverlapRecordedCommandBuffer(slot.Independent);
            DestroyAdvancedQueueOverlapSynchronization(slot);
            if (slot.Pool.Handle != 0)
                DestroyCommandPoolHostSynchronized(slot.Pool);
            slots[index] = null;
        }

        if (!hasPendingResources)
            _advancedQueueOverlapSlots = null;
    }

    /// <summary>
    /// Drops managed tracking state once device loss makes native completion
    /// unprovable. Native handles are intentionally left to logical-device
    /// destruction, matching synchronous-submission retirement policy.
    /// </summary>
    internal void AbandonAdvancedQueueOverlapResourcesAfterDeviceLoss()
    {
        VulkanAdvancedQueueOverlapSlot?[]? slots = _advancedQueueOverlapSlots;
        if (slots is null)
            return;

        for (int index = 0; index < slots.Length; index++)
        {
            VulkanAdvancedQueueOverlapSlot? slot = slots[index];
            if (slot is null)
                continue;

            AbandonAdvancedQueueOverlapDeviceLostCommandBuffer(slot.Prefix);
            AbandonAdvancedQueueOverlapDeviceLostCommandBuffer(slot.Classification);
            AbandonAdvancedQueueOverlapDeviceLostCommandBuffer(slot.Independent);
        }

        _advancedQueueOverlapSlots = null;
    }

    private VulkanAdvancedQueueOverlapSlot GetOrCreateAdvancedQueueOverlapSlot(uint frameSlot)
    {
        int requiredLength = checked((int)frameSlot + 1);
        VulkanAdvancedQueueOverlapSlot?[]? slots = _advancedQueueOverlapSlots;
        if (slots is null || slots.Length < requiredLength)
        {
            int length = Math.Max(requiredLength, slots?.Length * 2 ?? 2);
            Array.Resize(ref slots, length);
            _advancedQueueOverlapSlots = slots;
        }

        return slots[frameSlot] ??= new VulkanAdvancedQueueOverlapSlot(frameSlot);
    }

    private unsafe void EnsureAdvancedQueueOverlapResources(VulkanAdvancedQueueOverlapSlot slot)
    {
        if (!DeviceContext.IsOperational)
            throw new InvalidOperationException(
                $"Cannot initialize advanced queue-overlap resources while device state is {DeviceContext.State}.");

        if (slot.Pool.Handle == 0)
        {
            uint graphicsFamily = DeviceContext.QueueFamilies.GraphicsFamilyIndex ??
                throw new InvalidOperationException("Advanced queue overlap requires a graphics queue family.");
            CommandPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = graphicsFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };
            Result poolResult = CreateVulkanCommandPoolTracked(
                ref poolInfo,
                out slot.Pool,
                $"AdvancedQueueOverlap.FrameSlot[{slot.FrameSlot}].RetainedPool");
            if (poolResult != Result.Success)
                throw new InvalidOperationException($"Failed to create advanced queue-overlap command pool ({poolResult}).");
        }

        if (slot.Prefix.Handle == 0)
            slot.Prefix = AllocateTrackedCommandBuffer(Api, DeviceContext, ResourceRuntime, slot.Pool, CommandBufferLevel.Primary, $"AdvancedQueueOverlap.FrameSlot[{slot.FrameSlot}].Prefix");
        if (slot.Classification.Handle == 0)
            slot.Classification = AllocateTrackedCommandBuffer(Api, DeviceContext, ResourceRuntime, slot.Pool, CommandBufferLevel.Primary, $"AdvancedQueueOverlap.FrameSlot[{slot.FrameSlot}].Classification");
        if (slot.Independent.Handle == 0)
            slot.Independent = AllocateTrackedCommandBuffer(Api, DeviceContext, ResourceRuntime, slot.Pool, CommandBufferLevel.Primary, $"AdvancedQueueOverlap.FrameSlot[{slot.FrameSlot}].Independent");

        if (slot.Ready.Handle == 0 || slot.Classified.Handle == 0)
            CreateAdvancedQueueOverlapSemaphores(slot);
        if (slot.PrefixFence.Handle == 0 || slot.ClassificationFence.Handle == 0 || slot.IndependentFence.Handle == 0)
            CreateAdvancedQueueOverlapFences(slot);
    }

    private unsafe void CompleteAdvancedQueueOverlapFence(
        VulkanAdvancedQueueOverlapSlot slot,
        Fence fence,
        ref bool accepted,
        string role)
    {
        if (!accepted)
            return;

        Result result = Api.GetFenceStatus(DeviceContext.Device, fence);
        if (result is Result.NotReady or Result.Timeout)
            result = Api.WaitForFences(DeviceContext.Device, 1, &fence, true, ulong.MaxValue);
        if (result != Result.Success)
            DeviceContext.ObserveNativeResult("vkWaitForFences.AdvancedQueueOverlap", result);
        if (!DeviceContext.IsOperational)
            return;
        if (result != Result.Success)
            throw new InvalidOperationException($"Advanced queue-overlap {role} completion could not be proven ({result}).");

        CompleteTrackedFence(fence);
        Result resetResult = Api.ResetFences(DeviceContext.Device, 1, &fence);
        DeviceContext.ObserveNativeResult("vkResetFences.AdvancedQueueOverlap", resetResult);
        if (resetResult != Result.Success)
            throw new InvalidOperationException($"Failed to reset advanced queue-overlap {role} fence ({resetResult}).");
        accepted = false;
    }

    private bool AreAdvancedQueueOverlapFencesSignaled(VulkanAdvancedQueueOverlapSlot slot)
        => IsAdvancedQueueOverlapFenceSignaled(slot.FinalFence, slot.FinalAccepted, "Final") &&
           IsAdvancedQueueOverlapFenceSignaled(slot.PrefixFence, slot.PrefixAccepted, "Prefix") &&
           IsAdvancedQueueOverlapFenceSignaled(slot.ClassificationFence, slot.ClassificationAccepted, "Classification") &&
           IsAdvancedQueueOverlapFenceSignaled(slot.IndependentFence, slot.IndependentAccepted, "Independent");

    private bool IsAdvancedQueueOverlapFenceSignaled(
        Fence fence,
        bool required,
        string role)
    {
        if (!required)
            return true;

        Result result = Api.GetFenceStatus(DeviceContext.Device, fence);
        if (result is Result.Success)
            return true;
        if (result is Result.NotReady or Result.Timeout)
            return false;

        DeviceContext.ObserveNativeResult(
            $"vkGetFenceStatus.AdvancedQueueOverlap.{role}",
            result);
        return false;
    }

    private void AbandonAdvancedQueueOverlapRecordedCommandBuffer(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        _ = TryAbandonCommandBufferRecording(commandBuffer);
        RemoveCommandBufferState(commandBuffer);
    }

    private void AbandonAdvancedQueueOverlapDeviceLostCommandBuffer(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle != 0)
            RemoveCommandBufferState(commandBuffer);
    }

    private unsafe void CreateAdvancedQueueOverlapSemaphores(VulkanAdvancedQueueOverlapSlot slot)
    {
        SemaphoreCreateInfo info = new() { SType = StructureType.SemaphoreCreateInfo };
        Result readyResult = Api.CreateSemaphore(DeviceContext.Device, ref info, null, out slot.Ready);
        Result classifiedResult = readyResult == Result.Success
            ? Api.CreateSemaphore(DeviceContext.Device, ref info, null, out slot.Classified)
            : readyResult;
        if (readyResult == Result.Success && classifiedResult == Result.Success)
            return;

        DestroyAdvancedQueueOverlapSynchronization(slot);
        throw new InvalidOperationException($"Failed to create advanced queue-overlap semaphores ({readyResult}, {classifiedResult}).");
    }

    private unsafe void CreateAdvancedQueueOverlapFences(VulkanAdvancedQueueOverlapSlot slot)
    {
        FenceCreateInfo info = new() { SType = StructureType.FenceCreateInfo };
        Result prefixResult = Api.CreateFence(DeviceContext.Device, ref info, null, out slot.PrefixFence);
        Result classificationResult = prefixResult == Result.Success
            ? Api.CreateFence(DeviceContext.Device, ref info, null, out slot.ClassificationFence)
            : prefixResult;
        Result independentResult = classificationResult == Result.Success
            ? Api.CreateFence(DeviceContext.Device, ref info, null, out slot.IndependentFence)
            : classificationResult;
        if (prefixResult == Result.Success && classificationResult == Result.Success && independentResult == Result.Success)
            return;

        DestroyAdvancedQueueOverlapSynchronization(slot);
        throw new InvalidOperationException($"Failed to create advanced queue-overlap completion fences ({prefixResult}, {classificationResult}, {independentResult}).");
    }

    private unsafe void RecreateAdvancedQueueOverlapSemaphores(VulkanAdvancedQueueOverlapSlot slot)
    {
        if (slot.Ready.Handle != 0)
            Api.DestroySemaphore(DeviceContext.Device, slot.Ready, null);
        if (slot.Classified.Handle != 0)
            Api.DestroySemaphore(DeviceContext.Device, slot.Classified, null);
        slot.Ready = default;
        slot.Classified = default;
        CreateAdvancedQueueOverlapSemaphores(slot);
    }

    private unsafe void DestroyAdvancedQueueOverlapSynchronization(VulkanAdvancedQueueOverlapSlot slot)
    {
        if (slot.Ready.Handle != 0)
            Api.DestroySemaphore(DeviceContext.Device, slot.Ready, null);
        if (slot.Classified.Handle != 0)
            Api.DestroySemaphore(DeviceContext.Device, slot.Classified, null);
        if (slot.PrefixFence.Handle != 0)
            Api.DestroyFence(DeviceContext.Device, slot.PrefixFence, null);
        if (slot.ClassificationFence.Handle != 0)
            Api.DestroyFence(DeviceContext.Device, slot.ClassificationFence, null);
        if (slot.IndependentFence.Handle != 0)
            Api.DestroyFence(DeviceContext.Device, slot.IndependentFence, null);
        slot.Ready = default;
        slot.Classified = default;
        slot.PrefixFence = default;
        slot.ClassificationFence = default;
        slot.IndependentFence = default;
    }
}
