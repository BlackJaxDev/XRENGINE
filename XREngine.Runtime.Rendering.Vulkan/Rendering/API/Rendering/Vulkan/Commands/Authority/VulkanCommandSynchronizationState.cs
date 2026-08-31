using System.Diagnostics;
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
    /// <summary>
    /// Non-owning view of the desktop swapchain-image completion ledger. Desktop
    /// descriptor frame-data slots are keyed by acquired image, not by the
    /// frame-in-flight slot that happened to acquire it.
    /// </summary>
    internal ulong[]? _desktopImageTimelineValues;
    internal ulong _acquireTimelineValue;
    internal ulong _graphicsTimelineValue;
    private readonly object _graphicsTimelineReservationGate = new();
    internal readonly VulkanSynchronizationThreadWorkspace _synchronizationThreadWorkspace = new();
    internal EVulkanSynchronizationBackend _activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
    internal readonly object _vulkanImageLayoutLock = new();
    internal readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageSubresourceState> _trackedImageSubresourceStates = new();
    // The dictionary is the cold publication/diagnostic index. Sealed submission
    // only follows generation-tagged entries in this flat directory.
    private VulkanStableImageSubresourceSlotState[] _stableImageSubresourceSlots =
        new VulkanStableImageSubresourceSlotState[1024];
    private uint[] _stableImageSubresourceSlotFreeLinks = new uint[1024];
    private uint _stableImageSubresourceSlotCount = 1u;
    private uint _freeStableImageSubresourceSlotHead;
    internal readonly Dictionary<ulong, (ulong ResourceGeneration, EVulkanExternalImageOwnership Ownership)> _externalImageOwnershipByHandle = new();
    internal readonly Dictionary<ulong, VulkanRecordedImageLayoutState> _recordedImageLayoutsByCommandBuffer = new();
    internal readonly VulkanQueueOperationRecord[] _vulkanQueueOperationHistory =
        new VulkanQueueOperationRecord[QueueOperationHistoryCapacity];
    internal long _vulkanQueueOperationSerial;
    internal readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> _submissionImageStateScratch = new(64);
    internal readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> _secondaryDescriptorRequirementScratch = new(32);
    internal object SecondaryDescriptorRequirementScratchGate { get; } = new();
    internal readonly List<VulkanQueueSemaphoreRequirement> _submissionQueueSemaphoreRequirements = new(8);
    internal readonly object _submissionMarkerLock = new();
    internal readonly Dictionary<nint, List<VulkanTimelineGpuFence>> _submissionMarkersByCommandBuffer = [];
    internal readonly Stack<VulkanTimelineGpuFence> _timelineGpuFencePool = [];

    internal VulkanStableImageSubresourceSlotHandle PublishStableImageSubresourceNoLock(
        VulkanImageSubresourceState state)
    {
        VulkanStableImageSubresourceSlotHandle existing = state.StableSlot;
        if (TryGetStableImageSubresourceStateNoLock(existing, out VulkanImageSubresourceState? published) &&
            ReferenceEquals(published, state))
        {
            return existing;
        }

        uint index;
        if (_freeStableImageSubresourceSlotHead != 0u)
        {
            index = _freeStableImageSubresourceSlotHead;
            _freeStableImageSubresourceSlotHead = _stableImageSubresourceSlotFreeLinks[index];
            _stableImageSubresourceSlotFreeLinks[index] = 0u;
        }
        else
        {
            index = _stableImageSubresourceSlotCount++;
            EnsureStableImageSubresourceSlotCapacity(index);
        }

        VulkanStableImageSubresourceSlotState slot = _stableImageSubresourceSlots[index] ??= new();
        slot.Generation = NextStableImageSubresourceSlotGeneration(slot.Generation);
        slot.State = state;
        return state.StableSlot = new VulkanStableImageSubresourceSlotHandle(
            new VulkanStableImageSubresourceIndex(index),
            slot.Generation);
    }

    internal bool TryGetStableImageSubresourceStateNoLock(
        VulkanStableImageSubresourceSlotHandle handle,
        out VulkanImageSubresourceState? state)
    {
        state = null;
        if (!handle.IsValid || handle.Index.Value >= _stableImageSubresourceSlotCount)
            return false;

        VulkanStableImageSubresourceSlotState? slot =
            _stableImageSubresourceSlots[handle.Index.Value];
        if (slot is null || slot.Generation != handle.Generation || slot.State is null)
            return false;

        state = slot.State;
        return true;
    }

    internal void RetireStableImageSubresourceNoLock(VulkanImageSubresourceState state)
    {
        VulkanStableImageSubresourceSlotHandle handle = state.StableSlot;
        if (!handle.IsValid || handle.Index.Value >= _stableImageSubresourceSlotCount)
            return;

        VulkanStableImageSubresourceSlotState? slot =
            _stableImageSubresourceSlots[handle.Index.Value];
        if (slot is null || slot.Generation != handle.Generation || !ReferenceEquals(slot.State, state))
            return;

        // Nulling before linking the slot makes any stale sealed handle fail.
        slot.State = null;
        _stableImageSubresourceSlotFreeLinks[handle.Index.Value] = _freeStableImageSubresourceSlotHead;
        _freeStableImageSubresourceSlotHead = handle.Index.Value;
        state.StableSlot = VulkanStableImageSubresourceSlotHandle.Invalid;
    }

    internal void ClearStableImageSubresourcesNoLock()
    {
        for (uint index = 1u; index < _stableImageSubresourceSlotCount; ++index)
        {
            VulkanStableImageSubresourceSlotState? slot = _stableImageSubresourceSlots[index];
            if (slot?.State is { } state)
                state.StableSlot = VulkanStableImageSubresourceSlotHandle.Invalid;
            if (slot is not null)
                slot.State = null;
            _stableImageSubresourceSlotFreeLinks[index] = index - 1u;
        }
        _freeStableImageSubresourceSlotHead = _stableImageSubresourceSlotCount > 1u
            ? _stableImageSubresourceSlotCount - 1u
            : 0u;
    }

    private static ulong NextStableImageSubresourceSlotGeneration(ulong generation)
    {
        unchecked { ++generation; }
        return generation == 0UL ? 1UL : generation;
    }

    private void EnsureStableImageSubresourceSlotCapacity(uint requiredIndex)
    {
        if (requiredIndex < (uint)_stableImageSubresourceSlots.Length)
            return;

        int capacity = _stableImageSubresourceSlots.Length;
        do capacity = checked(capacity * 2);
        while (requiredIndex >= (uint)capacity);
        Array.Resize(ref _stableImageSubresourceSlots, capacity);
        Array.Resize(ref _stableImageSubresourceSlotFreeLinks, capacity);
    }

    /// <summary>
    /// Reserves the next graphics timeline value across desktop, explicit-target,
    /// recovery, and OpenXR producers. Failed submissions may leave gaps; no
    /// consumer waits for a value until its native submission is accepted.
    /// </summary>
    internal ulong ReserveGraphicsTimelineValue(ulong minimumValue = 1UL)
    {
        lock (_graphicsTimelineReservationGate)
        {
            ulong next = _graphicsTimelineValue == ulong.MaxValue
                ? throw new InvalidOperationException("Vulkan graphics timeline value exhausted.")
                : _graphicsTimelineValue + 1UL;
            _graphicsTimelineValue = Math.Max(next, minimumValue);
            return _graphicsTimelineValue;
        }
    }

    /// <summary>
    /// Debug-only assertion that fires when <c>AllCommandsBit</c> is used in a barrier.
    /// </summary>
    [Conditional("DEBUG")]
    internal static void WarnBroadBarrierStages(
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage,
        string? caller = null)
    {
        if ((srcStage & PipelineStageFlags.AllCommandsBit) == 0 &&
            (dstStage & PipelineStageFlags.AllCommandsBit) == 0)
        {
            return;
        }

        string site = string.IsNullOrEmpty(caller) ? "<unknown>" : caller;
        Debug.VulkanWarningEvery(
            "Vulkan.BarrierAudit",
            TimeSpan.FromSeconds(10),
            "[Vulkan][BarrierAudit] Broad AllCommandsBit barrier originating from {0}. Consider narrowing src/dst stages for performance.",
            site);
    }

    /// <summary>
    /// Converts a legacy pipeline-stage mask to its synchronization2 equivalent,
    /// using all commands when the legacy mask is empty.
    /// </summary>
    internal static PipelineStageFlags2 NormalizePipelineStages2(PipelineStageFlags stageMask)
        => (PipelineStageFlags2)(ulong)(stageMask == 0 ? PipelineStageFlags.AllCommandsBit : stageMask);

    /// <summary>
    /// Converts a legacy access mask to its synchronization2 equivalent.
    /// </summary>
    internal static AccessFlags2 NormalizeAccessFlags2(AccessFlags accessMask)
        => (AccessFlags2)(ulong)accessMask;

    /// <summary>
    /// Resolves the semantic image-access intent represented by a Vulkan layout
    /// and image aspect.
    /// </summary>
    internal static EVulkanImageAccessIntent ResolveVulkanImageAccessIntent(
        ImageLayout layout,
        ImageAspectFlags aspectMask)
        => layout switch
        {
            ImageLayout.Undefined => EVulkanImageAccessIntent.Undefined,
            ImageLayout.PresentSrcKhr => EVulkanImageAccessIntent.Present,
            ImageLayout.ColorAttachmentOptimal or ImageLayout.AttachmentOptimal =>
                (aspectMask & ImageAspectFlags.ColorBit) != 0
                    ? EVulkanImageAccessIntent.ColorAttachment
                    : EVulkanImageAccessIntent.DepthStencilAttachment,
            ImageLayout.DepthAttachmentOptimal or
            ImageLayout.StencilAttachmentOptimal or
            ImageLayout.DepthStencilAttachmentOptimal => EVulkanImageAccessIntent.DepthStencilAttachment,
            ImageLayout.DepthReadOnlyOptimal or
            ImageLayout.StencilReadOnlyOptimal or
            ImageLayout.DepthStencilReadOnlyOptimal => EVulkanImageAccessIntent.DepthStencilRead,
            ImageLayout.ShaderReadOnlyOptimal or ImageLayout.ReadOnlyOptimal => EVulkanImageAccessIntent.SampledRead,
            ImageLayout.TransferSrcOptimal => EVulkanImageAccessIntent.TransferRead,
            ImageLayout.TransferDstOptimal => EVulkanImageAccessIntent.TransferWrite,
            _ => EVulkanImageAccessIntent.StorageReadWrite,
        };

    /// <summary>
    /// Creates the canonical stage, access, descriptor-layout, ownership, and
    /// generation state for a Vulkan image layout.
    /// </summary>
    internal static VulkanImageAccessState ResolveVulkanImageAccessState(
        ImageLayout layout,
        ImageAspectFlags aspectMask,
        uint queueFamilyIndex = Vk.QueueFamilyIgnored,
        ulong serial = 0,
        ulong resourceGeneration = 0)
    {
        const PipelineStageFlags shaderStages =
            PipelineStageFlags.VertexShaderBit |
            PipelineStageFlags.FragmentShaderBit |
            PipelineStageFlags.ComputeShaderBit;

        EVulkanImageAccessIntent intent = ResolveVulkanImageAccessIntent(layout, aspectMask);
        PipelineStageFlags stages;
        AccessFlags access;
        ImageLayout descriptorLayout;
        switch (intent)
        {
            case EVulkanImageAccessIntent.Undefined:
                stages = PipelineStageFlags.TopOfPipeBit;
                access = AccessFlags.None;
                descriptorLayout = ImageLayout.Undefined;
                break;
            case EVulkanImageAccessIntent.Present:
                stages = PipelineStageFlags.BottomOfPipeBit;
                access = AccessFlags.MemoryReadBit;
                descriptorLayout = ImageLayout.Undefined;
                break;
            case EVulkanImageAccessIntent.ColorAttachment:
                stages = PipelineStageFlags.ColorAttachmentOutputBit;
                access = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;
                descriptorLayout = ImageLayout.Undefined;
                break;
            case EVulkanImageAccessIntent.DepthStencilAttachment:
                stages = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
                access = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
                descriptorLayout = ImageLayout.Undefined;
                break;
            case EVulkanImageAccessIntent.SampledRead:
                stages = shaderStages;
                access = AccessFlags.ShaderReadBit;
                descriptorLayout = ImageLayout.ShaderReadOnlyOptimal;
                break;
            case EVulkanImageAccessIntent.DepthStencilRead:
                stages = shaderStages | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
                access = AccessFlags.ShaderReadBit | AccessFlags.DepthStencilAttachmentReadBit;
                descriptorLayout = ImageLayout.DepthStencilReadOnlyOptimal;
                break;
            case EVulkanImageAccessIntent.TransferRead:
                stages = PipelineStageFlags.TransferBit;
                access = AccessFlags.TransferReadBit;
                descriptorLayout = ImageLayout.Undefined;
                break;
            case EVulkanImageAccessIntent.TransferWrite:
                stages = PipelineStageFlags.TransferBit;
                access = AccessFlags.TransferWriteBit;
                descriptorLayout = ImageLayout.Undefined;
                break;
            default:
                stages = shaderStages;
                access = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
                descriptorLayout = ImageLayout.General;
                break;
        }

        return new VulkanImageAccessState(
            layout,
            NormalizePipelineStages2(stages),
            NormalizeAccessFlags2(access),
            queueFamilyIndex,
            descriptorLayout,
            serial,
            resourceGeneration);
    }

    /// <summary>
    /// Produces the state published by the command-buffer tracker.
    /// </summary>
    internal static VulkanImageAccessState ResolveRecordedVulkanImageAccessState(
        ImageLayout layout,
        ImageAspectFlags aspectMask,
        PipelineStageFlags stageMask,
        AccessFlags accessMask,
        uint queueFamilyIndex,
        ulong serial,
        ulong resourceGeneration)
    {
        VulkanImageAccessState canonical = ResolveVulkanImageAccessState(
            layout,
            aspectMask,
            queueFamilyIndex,
            serial,
            resourceGeneration);
        PipelineStageFlags2 requestedStages = NormalizePipelineStages2(stageMask);
        AccessFlags2 requestedAccess = NormalizeAccessFlags2(accessMask);
        if (layout == ImageLayout.General)
        {
            return canonical with
            {
                StageMask = requestedStages == 0 ? canonical.StageMask : requestedStages,
                AccessMask = requestedAccess == 0 ? canonical.AccessMask : requestedAccess,
            };
        }

        bool stagesAreCompatible =
            requestedStages != 0 &&
            (requestedStages & ~canonical.StageMask) == 0;
        bool accessIsCompatible =
            requestedAccess != 0 &&
            (requestedAccess & ~canonical.AccessMask) == 0;
        if (!stagesAreCompatible || !accessIsCompatible)
            return canonical;

        return canonical with
        {
            StageMask = requestedStages,
            AccessMask = requestedAccess,
        };
    }

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
            foreach (List<VulkanTimelineGpuFence> markers in _submissionMarkersByCommandBuffer.Values)
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

    /// <summary>
    /// Clears image-layout state invalidated by a successful native command
    /// reset while preserving the command-local journal's allocated capacity.
    /// Full removal is reserved for native command-buffer destruction.
    /// </summary>
    internal void ClearRecordedImageLayoutsAfterSuccessfulReset(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        lock (_vulkanImageLayoutLock)
        {
            if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)commandBuffer.Handle),
                    out VulkanRecordedImageLayoutState? recorded))
            {
                return;
            }

            recorded.Subresources.Clear();
            recorded.EntrySubresources.Clear();
            recorded.SecondaryDescriptorRequirements.Clear();
            recorded.SecondaryDescriptorImagePayloadGenerations.Clear();
            recorded.TouchedSubresources.Clear();
            recorded.QueueOwnershipTransfers.Clear();
            recorded.EntryStateIncomplete = false;
            recorded.EntryStateFailure = default;
            recorded.RecordingGeneration = 0;
        }
    }

    /// <summary>
    /// Resolves one common submitted layout across the requested image range.
    /// This is the renderer-free read side of the command authority's image
    /// state ledger; output target selection consumes the resulting value as a
    /// frozen input.
    /// </summary>
    internal bool TryGetSubmittedImageLayout(
        Image image,
        in ImageSubresourceRange range,
        out ImageLayout layout)
    {
        layout = ImageLayout.Undefined;
        if (image.Handle == 0)
            return false;

        bool found = false;
        uint levelCount = Math.Max(range.LevelCount, 1u);
        uint layerCount = Math.Max(range.LayerCount, 1u);
        lock (_vulkanImageLayoutLock)
        {
            for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
            for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
            {
                uint mipLevel = range.BaseMipLevel + mipOffset;
                uint arrayLayer = range.BaseArrayLayer + layerOffset;
                if (!TryMergeSubmittedAspectNoLock(
                        image.Handle,
                        mipLevel,
                        arrayLayer,
                        range.AspectMask,
                        ImageAspectFlags.ColorBit,
                        ref found,
                        ref layout) ||
                    !TryMergeSubmittedAspectNoLock(
                        image.Handle,
                        mipLevel,
                        arrayLayer,
                        range.AspectMask,
                        ImageAspectFlags.DepthBit,
                        ref found,
                        ref layout) ||
                    !TryMergeSubmittedAspectNoLock(
                        image.Handle,
                        mipLevel,
                        arrayLayer,
                        range.AspectMask,
                        ImageAspectFlags.StencilBit,
                        ref found,
                        ref layout))
                {
                    layout = ImageLayout.Undefined;
                    return false;
                }
            }
        }

        return found;
    }

    private bool TryMergeSubmittedAspectNoLock(
        ulong imageHandle,
        uint mipLevel,
        uint arrayLayer,
        ImageAspectFlags requestedAspects,
        ImageAspectFlags aspect,
        ref bool found,
        ref ImageLayout layout)
    {
        if ((requestedAspects & aspect) == 0)
            return true;

        VulkanTrackedImageSubresource key = new(
            imageHandle,
            mipLevel,
            arrayLayer,
            aspect);
        if (!_trackedImageSubresourceStates.TryGetValue(
                key,
                out VulkanImageSubresourceState? state))
        {
            return false;
        }

        ImageLayout candidate = state.Submitted.Layout;
        if (!found)
        {
            layout = candidate;
            found = true;
            return true;
        }

        return layout == candidate;
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

    internal void AdvanceCompletedImageLayouts(
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
