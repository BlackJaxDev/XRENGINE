using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int VulkanQueueOperationHistoryCapacity = 64;
    private EVulkanSynchronizationBackend _activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
    private readonly Dictionary<VulkanTrackedImageSubresource, ImageLayout> _trackedImageSubresourceLayouts = new();
    private readonly VulkanQueueOperationRecord[] _vulkanQueueOperationHistory = new VulkanQueueOperationRecord[VulkanQueueOperationHistoryCapacity];
    private long _vulkanQueueOperationSerial;

    private bool UsesSynchronization2
        => _activeSynchronizationBackend == EVulkanSynchronizationBackend.Sync2;

    private readonly record struct VulkanTrackedImageSubresource(
        ulong ImageHandle,
        uint MipLevel,
        uint ArrayLayer,
        ImageAspectFlags Aspect);

    internal readonly record struct VulkanQueueOperationRecord(
        ulong Serial,
        string Operation,
        ulong QueueHandle,
        Result Result,
        EVulkanDeviceState DeviceState,
        ulong SubmissionSerial,
        int ThreadId,
        string? Caller);

    private sealed class VulkanImageLayoutStateSnapshot(
        VulkanTrackedImageLayoutEntry[] trackedLayouts,
        VulkanPhysicalImageGroupLayoutEntry[] physicalGroups,
        ulong signature)
    {
        public VulkanTrackedImageLayoutEntry[] TrackedLayouts { get; } = trackedLayouts;
        public VulkanPhysicalImageGroupLayoutEntry[] PhysicalGroups { get; } = physicalGroups;
        public ulong Signature { get; } = signature;
    }

    private readonly record struct VulkanTrackedImageLayoutEntry(
        VulkanTrackedImageSubresource Key,
        ImageLayout Layout);

    private readonly record struct VulkanPhysicalImageGroupLayoutEntry(
        VulkanPhysicalImageGroup Group,
        VulkanPhysicalImageGroup.LayoutSnapshot Layout);

    /// <summary>
    /// Debug-only assertion that fires when <c>AllCommandsBit</c> is used in a barrier.
    /// Callers in hot paths should route through
    /// <see cref="CmdPipelineBarrierTracked"/> which uses the active synchronization
    /// backend; this assert catches newly-introduced broad masks before they ship.
    /// </summary>
    [Conditional("DEBUG")]
    private static void WarnBroadBarrierStages(
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage,
        string? caller = null)
    {
        if ((srcStage & PipelineStageFlags.AllCommandsBit) != 0 ||
            (dstStage & PipelineStageFlags.AllCommandsBit) != 0)
        {
            string site = string.IsNullOrEmpty(caller) ? "<unknown>" : caller;
            // Throttle per originating call site: a single over-broad barrier site
            // would otherwise emit hundreds of identical lines per second.
            Debug.VulkanWarningEvery(
                $"Vulkan.BarrierAudit.{site}",
                TimeSpan.FromSeconds(10),
                "[Vulkan][BarrierAudit] Broad AllCommandsBit barrier originating from {0}. Consider narrowing src/dst stages for performance.",
                site);
        }
    }

    private void InitializeSynchronizationBackend()
    {
        EVulkanSynchronizationBackend requestedBackend = RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.SyncBackend;
        if (requestedBackend == EVulkanSynchronizationBackend.Sync2 && SupportsSynchronization2)
        {
            _activeSynchronizationBackend = EVulkanSynchronizationBackend.Sync2;
        }
        else
        {
            if (requestedBackend == EVulkanSynchronizationBackend.Sync2 && !SupportsSynchronization2)
            {
                Debug.VulkanWarning(
                    "[Vulkan] SyncBackend requested Sync2, but synchronization2 is unavailable. Falling back to legacy submit/barrier path.");
            }

            _activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
        }

        Debug.Vulkan("[Vulkan] Synchronization backend initialized: {0}", _activeSynchronizationBackend);
    }

    private static PipelineStageFlags2 NormalizePipelineStages2(PipelineStageFlags stageMask)
        => (PipelineStageFlags2)(ulong)(stageMask == 0 ? PipelineStageFlags.AllCommandsBit : stageMask);

    private static AccessFlags2 NormalizeAccessFlags2(AccessFlags accessMask)
        => (AccessFlags2)(ulong)accessMask;

    private static PipelineStageFlags2 ResolveSignalStageMask2(uint commandBufferCount)
        => commandBufferCount > 0 ? PipelineStageFlags2.AllCommandsBit : PipelineStageFlags2.TopOfPipeBit;

    private static TimelineSemaphoreSubmitInfo* FindTimelineSemaphoreSubmitInfo(void* pNext)
    {
        BaseInStructure* current = (BaseInStructure*)pNext;
        while (current is not null)
        {
            if (current->SType == StructureType.TimelineSemaphoreSubmitInfo)
                return (TimelineSemaphoreSubmitInfo*)current;

            current = current->PNext;
        }

        return null;
    }

    private Result SubmitToQueueTracked(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        VulkanSubmissionDiagnosticContext diagnosticContext = default,
        [CallerMemberName] string? caller = null)
    {
        using VulkanQueueOperationLease queueOperation =
            VulkanQueueOperationLease.TryEnter(_oneTimeSubmitLock, _deviceStateMachine);
        if (!queueOperation.Acquired)
        {
            lock (_oneTimeSubmitLock)
                RecordVulkanQueueOperation("submit-rejected", queue, Result.ErrorDeviceLost, 0, caller);
            return Result.ErrorDeviceLost;
        }

        diagnosticContext = CompleteSubmissionDiagnosticContext(queue, ref submitInfo, fence, diagnosticContext, caller);
        RecordLastVulkanSubmissionDiagnosticContext(diagnosticContext);

        Result result = UsesSynchronization2
            ? SubmitToQueueSync2(queue, ref submitInfo, fence)
            : Api!.QueueSubmit(queue, 1, ref submitInfo, fence);

        RecordVulkanQueueOperation("submit", queue, result, diagnosticContext.SubmissionSerial, caller);
        if (result == Result.Success)
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueSubmit();
        else if (result == Result.ErrorDeviceLost)
        {
            RecordFirstFailingVulkanApi($"vkQueueSubmit:{caller ?? "<unknown>"}:{result}");
            MarkDeviceLost(
                $"QueueSubmit returned ErrorDeviceLost in {caller ?? "<unknown>"} " +
                $"(waits={submitInfo.WaitSemaphoreCount}, signals={submitInfo.SignalSemaphoreCount}, commandBuffers={submitInfo.CommandBufferCount}, fence=0x{fence.Handle:X})");
        }

        return result;
    }

    internal Result WaitForQueueIdleTracked(Queue queue, [CallerMemberName] string? caller = null)
    {
        using VulkanQueueOperationLease queueOperation =
            VulkanQueueOperationLease.TryEnter(_oneTimeSubmitLock, _deviceStateMachine);
        if (!queueOperation.Acquired)
        {
            lock (_oneTimeSubmitLock)
                RecordVulkanQueueOperation("wait-idle-rejected", queue, Result.ErrorDeviceLost, 0, caller);
            return Result.ErrorDeviceLost;
        }

        Result result = Api!.QueueWaitIdle(queue);
        RecordVulkanQueueOperation("wait-idle", queue, result, 0, caller);
        if (result == Result.ErrorDeviceLost)
        {
            RecordFirstFailingVulkanApi($"vkQueueWaitIdle:{caller ?? "<unknown>"}:{result}");
            MarkDeviceLost($"QueueWaitIdle returned ErrorDeviceLost in {caller ?? "<unknown>"}");
        }

        return result;
    }

    private bool TryPresentToQueueTracked(
        Queue queue,
        ref PresentInfoKHR presentInfo,
        out Result result,
        out string failureReason,
        [CallerMemberName] string? caller = null)
    {
        using VulkanQueueOperationLease queueOperation =
            VulkanQueueOperationLease.TryEnter(_oneTimeSubmitLock, _deviceStateMachine);
        if (!queueOperation.Acquired)
        {
            result = Result.ErrorDeviceLost;
            failureReason = "Vulkan device is not operational";
            lock (_oneTimeSubmitLock)
                RecordVulkanQueueOperation("present-rejected", queue, result, 0, caller);
            return false;
        }

        bool dispatched;
        if (_streamlineFrameGenerationSwapchainActive)
        {
            dispatched = NvidiaDlssManager.Native.TryQueueProxyPresent(
                this,
                queue,
                ref presentInfo,
                out result,
                out failureReason);
        }
        else
        {
            result = khrSwapChain!.QueuePresent(queue, ref presentInfo);
            failureReason = string.Empty;
            dispatched = true;
        }

        RecordVulkanQueueOperation("present", queue, result, 0, caller);
        if (result == Result.ErrorDeviceLost)
        {
            RecordFirstFailingVulkanApi($"vkQueuePresentKHR:{caller ?? "<unknown>"}:{result}");
            MarkDeviceLost($"QueuePresent returned ErrorDeviceLost in {caller ?? "<unknown>"}");
        }

        return dispatched;
    }

    private void RecordVulkanQueueOperation(
        string operation,
        Queue queue,
        Result result,
        ulong submissionSerial,
        string? caller)
    {
        long serial = Interlocked.Increment(ref _vulkanQueueOperationSerial);
        int index = unchecked((int)((serial - 1) % VulkanQueueOperationHistoryCapacity));
        _vulkanQueueOperationHistory[index] = new VulkanQueueOperationRecord(
            unchecked((ulong)serial),
            operation,
            unchecked((ulong)queue.Handle),
            result,
            DeviceState,
            submissionSerial,
            Environment.CurrentManagedThreadId,
            caller);
    }

    private string DescribeVulkanQueueOperationTail(int maxEntries = 8)
    {
        lock (_oneTimeSubmitLock)
        {
            long latestSerial = Volatile.Read(ref _vulkanQueueOperationSerial);
            if (latestSerial <= 0)
                return string.Empty;

            int available = (int)Math.Min(latestSerial, VulkanQueueOperationHistoryCapacity);
            int emitted = 0;
            StringBuilder builder = new("QueueOperationTail");
            for (long serial = latestSerial; serial > 0 && emitted < maxEntries && latestSerial - serial < available; serial--)
            {
                int index = unchecked((int)((serial - 1) % VulkanQueueOperationHistoryCapacity));
                VulkanQueueOperationRecord operation = _vulkanQueueOperationHistory[index];
                if (operation.Serial != unchecked((ulong)serial))
                    continue;

                builder
                    .Append(" [#").Append(operation.Serial)
                    .Append(' ').Append(operation.Operation)
                    .Append(" queue=0x").Append(operation.QueueHandle.ToString("X"))
                    .Append(" result=").Append(operation.Result)
                    .Append(" state=").Append(operation.DeviceState)
                    .Append(" submit=").Append(operation.SubmissionSerial)
                    .Append(" thread=").Append(operation.ThreadId)
                    .Append(" caller=").Append(operation.Caller ?? "<unknown>")
                    .Append(']');
                emitted++;
            }

            return emitted == 0 ? string.Empty : builder.ToString();
        }
    }

    private unsafe Result SubmitToQueueSync2(Queue queue, ref SubmitInfo submitInfo, Fence fence)
    {
        int waitCount = (int)submitInfo.WaitSemaphoreCount;
        int signalCount = (int)submitInfo.SignalSemaphoreCount;
        int commandBufferCount = (int)submitInfo.CommandBufferCount;

        TimelineSemaphoreSubmitInfo* timelineInfo = FindTimelineSemaphoreSubmitInfo(submitInfo.PNext);
        SemaphoreSubmitInfo[] waitInfosArray = waitCount > 0
            ? ArrayPool<SemaphoreSubmitInfo>.Shared.Rent(waitCount)
            : Array.Empty<SemaphoreSubmitInfo>();
        SemaphoreSubmitInfo[] signalInfosArray = signalCount > 0
            ? ArrayPool<SemaphoreSubmitInfo>.Shared.Rent(signalCount)
            : Array.Empty<SemaphoreSubmitInfo>();
        CommandBufferSubmitInfo[] commandBufferInfosArray = commandBufferCount > 0
            ? ArrayPool<CommandBufferSubmitInfo>.Shared.Rent(commandBufferCount)
            : Array.Empty<CommandBufferSubmitInfo>();

        try
        {
            for (int i = 0; i < waitCount; i++)
            {
                waitInfosArray[i] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = submitInfo.PWaitSemaphores[i],
                    Value = timelineInfo is not null && timelineInfo->PWaitSemaphoreValues is not null
                        ? timelineInfo->PWaitSemaphoreValues[i]
                        : 0UL,
                    StageMask = NormalizePipelineStages2(submitInfo.PWaitDstStageMask[i]),
                    DeviceIndex = 0,
                };
            }

            PipelineStageFlags2 signalStageMask = ResolveSignalStageMask2((uint)commandBufferCount);
            for (int i = 0; i < signalCount; i++)
            {
                signalInfosArray[i] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = submitInfo.PSignalSemaphores[i],
                    Value = timelineInfo is not null && timelineInfo->PSignalSemaphoreValues is not null
                        ? timelineInfo->PSignalSemaphoreValues[i]
                        : 0UL,
                    StageMask = signalStageMask,
                    DeviceIndex = 0,
                };
            }

            for (int i = 0; i < commandBufferCount; i++)
            {
                commandBufferInfosArray[i] = new CommandBufferSubmitInfo
                {
                    SType = StructureType.CommandBufferSubmitInfo,
                    CommandBuffer = submitInfo.PCommandBuffers[i],
                    DeviceMask = 0,
                };
            }

            fixed (SemaphoreSubmitInfo* waitInfosFixed = waitInfosArray)
            fixed (SemaphoreSubmitInfo* signalInfosFixed = signalInfosArray)
            fixed (CommandBufferSubmitInfo* commandBufferInfosFixed = commandBufferInfosArray)
            {
                SubmitInfo2 submitInfo2 = new()
                {
                    SType = StructureType.SubmitInfo2,
                    WaitSemaphoreInfoCount = (uint)waitCount,
                    PWaitSemaphoreInfos = waitCount > 0 ? waitInfosFixed : null,
                    CommandBufferInfoCount = (uint)commandBufferCount,
                    PCommandBufferInfos = commandBufferCount > 0 ? commandBufferInfosFixed : null,
                    SignalSemaphoreInfoCount = (uint)signalCount,
                    PSignalSemaphoreInfos = signalCount > 0 ? signalInfosFixed : null,
                };

                return QueueSubmit2Compat(queue, 1, &submitInfo2, fence);
            }
        }
        finally
        {
            if (waitCount > 0)
                ArrayPool<SemaphoreSubmitInfo>.Shared.Return(waitInfosArray, clearArray: true);
            if (signalCount > 0)
                ArrayPool<SemaphoreSubmitInfo>.Shared.Return(signalInfosArray, clearArray: true);
            if (commandBufferCount > 0)
                ArrayPool<CommandBufferSubmitInfo>.Shared.Return(commandBufferInfosArray, clearArray: true);
        }
    }

    private unsafe void CmdPipelineBarrierTracked(
        CommandBuffer commandBuffer,
        PipelineStageFlags srcStageMask,
        PipelineStageFlags dstStageMask,
        DependencyFlags dependencyFlags,
        uint memoryBarrierCount,
        MemoryBarrier* memoryBarriers,
        uint bufferBarrierCount,
        BufferMemoryBarrier* bufferBarriers,
        uint imageBarrierCount,
        ImageMemoryBarrier* imageBarriers,
        [CallerMemberName] string? caller = null)
    {
        WarnBroadBarrierStages(srcStageMask, dstStageMask, caller);
        RecordVulkanImageLayoutTransitionBreadcrumb(commandBuffer, imageBarrierCount, imageBarriers, caller);

        if (!UsesSynchronization2)
        {
            Api!.CmdPipelineBarrier(
                commandBuffer,
                srcStageMask,
                dstStageMask,
                dependencyFlags,
                memoryBarrierCount,
                memoryBarriers,
                bufferBarrierCount,
                bufferBarriers,
                imageBarrierCount,
                imageBarriers);
            TrackImageBarrierLayouts(imageBarrierCount, imageBarriers);
            return;
        }

        MemoryBarrier2[] memoryBarrierArray = memoryBarrierCount > 0
            ? ArrayPool<MemoryBarrier2>.Shared.Rent((int)memoryBarrierCount)
            : Array.Empty<MemoryBarrier2>();
        BufferMemoryBarrier2[] bufferBarrierArray = bufferBarrierCount > 0
            ? ArrayPool<BufferMemoryBarrier2>.Shared.Rent((int)bufferBarrierCount)
            : Array.Empty<BufferMemoryBarrier2>();
        ImageMemoryBarrier2[] imageBarrierArray = imageBarrierCount > 0
            ? ArrayPool<ImageMemoryBarrier2>.Shared.Rent((int)imageBarrierCount)
            : Array.Empty<ImageMemoryBarrier2>();

        try
        {
            PipelineStageFlags2 srcStages2 = NormalizePipelineStages2(srcStageMask);
            PipelineStageFlags2 dstStages2 = NormalizePipelineStages2(dstStageMask);

            for (int i = 0; i < memoryBarrierCount; i++)
            {
                memoryBarrierArray[i] = new MemoryBarrier2
                {
                    SType = StructureType.MemoryBarrier2,
                    SrcStageMask = srcStages2,
                    SrcAccessMask = NormalizeAccessFlags2(memoryBarriers[i].SrcAccessMask),
                    DstStageMask = dstStages2,
                    DstAccessMask = NormalizeAccessFlags2(memoryBarriers[i].DstAccessMask),
                };
            }

            for (int i = 0; i < bufferBarrierCount; i++)
            {
                bufferBarrierArray[i] = new BufferMemoryBarrier2
                {
                    SType = StructureType.BufferMemoryBarrier2,
                    SrcStageMask = srcStages2,
                    SrcAccessMask = NormalizeAccessFlags2(bufferBarriers[i].SrcAccessMask),
                    DstStageMask = dstStages2,
                    DstAccessMask = NormalizeAccessFlags2(bufferBarriers[i].DstAccessMask),
                    SrcQueueFamilyIndex = bufferBarriers[i].SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = bufferBarriers[i].DstQueueFamilyIndex,
                    Buffer = bufferBarriers[i].Buffer,
                    Offset = bufferBarriers[i].Offset,
                    Size = bufferBarriers[i].Size,
                };
            }

            for (int i = 0; i < imageBarrierCount; i++)
            {
                imageBarrierArray[i] = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcStageMask = srcStages2,
                    SrcAccessMask = NormalizeAccessFlags2(imageBarriers[i].SrcAccessMask),
                    DstStageMask = dstStages2,
                    DstAccessMask = NormalizeAccessFlags2(imageBarriers[i].DstAccessMask),
                    OldLayout = imageBarriers[i].OldLayout,
                    NewLayout = imageBarriers[i].NewLayout,
                    SrcQueueFamilyIndex = imageBarriers[i].SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = imageBarriers[i].DstQueueFamilyIndex,
                    Image = imageBarriers[i].Image,
                    SubresourceRange = imageBarriers[i].SubresourceRange,
                };
            }

            fixed (MemoryBarrier2* memoryBarrierInfos = memoryBarrierArray)
            fixed (BufferMemoryBarrier2* bufferBarrierInfos = bufferBarrierArray)
            fixed (ImageMemoryBarrier2* imageBarrierInfos = imageBarrierArray)
            {
                DependencyInfo dependencyInfo = new()
                {
                    SType = StructureType.DependencyInfo,
                    DependencyFlags = dependencyFlags,
                    MemoryBarrierCount = memoryBarrierCount,
                    PMemoryBarriers = memoryBarrierCount > 0 ? memoryBarrierInfos : null,
                    BufferMemoryBarrierCount = bufferBarrierCount,
                    PBufferMemoryBarriers = bufferBarrierCount > 0 ? bufferBarrierInfos : null,
                    ImageMemoryBarrierCount = imageBarrierCount,
                    PImageMemoryBarriers = imageBarrierCount > 0 ? imageBarrierInfos : null,
                };

                CmdPipelineBarrier2Compat(commandBuffer, &dependencyInfo);
            }
        }
        finally
        {
            if (memoryBarrierCount > 0)
                ArrayPool<MemoryBarrier2>.Shared.Return(memoryBarrierArray, clearArray: true);
            if (bufferBarrierCount > 0)
                ArrayPool<BufferMemoryBarrier2>.Shared.Return(bufferBarrierArray, clearArray: true);
            if (imageBarrierCount > 0)
                ArrayPool<ImageMemoryBarrier2>.Shared.Return(imageBarrierArray, clearArray: true);
        }

        TrackImageBarrierLayouts(imageBarrierCount, imageBarriers);
    }

    private void TrackImageBarrierLayouts(uint imageBarrierCount, ImageMemoryBarrier* imageBarriers)
    {
        if (imageBarrierCount == 0 || imageBarriers is null)
            return;

        for (uint i = 0; i < imageBarrierCount; i++)
        {
            ref ImageMemoryBarrier barrier = ref imageBarriers[i];
            TrackImageLayout(barrier.Image, barrier.SubresourceRange, barrier.NewLayout);
        }
    }

    private void TrackImageLayout(Image image, ImageSubresourceRange range, ImageLayout layout)
    {
        if (image.Handle == 0)
            return;

        uint levelCount = Math.Max(range.LevelCount, 1u);
        uint layerCount = Math.Max(range.LayerCount, 1u);
        for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
        {
            uint mip = range.BaseMipLevel + mipOffset;
            for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
            {
                uint layer = range.BaseArrayLayer + layerOffset;
                TrackImageAspectLayout(image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.ColorBit, layout);
                TrackImageAspectLayout(image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.DepthBit, layout);
                TrackImageAspectLayout(image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.StencilBit, layout);
            }
        }
    }

    private void ClearTrackedImageLayouts(Image image)
    {
        ulong imageHandle = image.Handle;
        if (imageHandle == 0 || _trackedImageSubresourceLayouts.Count == 0)
            return;

        VulkanTrackedImageSubresource[]? keysToRemove = null;
        int keyCount = 0;
        try
        {
            foreach (VulkanTrackedImageSubresource key in _trackedImageSubresourceLayouts.Keys)
            {
                if (key.ImageHandle != imageHandle)
                    continue;

                keysToRemove ??= ArrayPool<VulkanTrackedImageSubresource>.Shared.Rent(8);
                if (keyCount == keysToRemove.Length)
                {
                    VulkanTrackedImageSubresource[] expanded =
                        ArrayPool<VulkanTrackedImageSubresource>.Shared.Rent(keysToRemove.Length * 2);
                    Array.Copy(keysToRemove, expanded, keysToRemove.Length);
                    ArrayPool<VulkanTrackedImageSubresource>.Shared.Return(keysToRemove, clearArray: true);
                    keysToRemove = expanded;
                }

                keysToRemove[keyCount++] = key;
            }

            for (int i = 0; i < keyCount; i++)
                _trackedImageSubresourceLayouts.Remove(keysToRemove![i]);
        }
        finally
        {
            if (keysToRemove is not null)
                ArrayPool<VulkanTrackedImageSubresource>.Shared.Return(keysToRemove, clearArray: true);
        }
    }

    private int ClearAllTrackedImageLayouts()
    {
        int count = _trackedImageSubresourceLayouts.Count;
        if (count != 0)
            _trackedImageSubresourceLayouts.Clear();

        return count;
    }

    private void TrackImageAspectLayout(
        ulong imageHandle,
        uint mip,
        uint layer,
        ImageAspectFlags rangeAspect,
        ImageAspectFlags trackedAspect,
        ImageLayout layout)
    {
        if ((rangeAspect & trackedAspect) == 0)
            return;

        VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, trackedAspect);
        if (layout == ImageLayout.Undefined)
            _trackedImageSubresourceLayouts.Remove(key);
        else
            _trackedImageSubresourceLayouts[key] = layout;
    }

    private bool TryGetTrackedImageLayout(Image image, ImageSubresourceRange range, out ImageLayout layout)
    {
        layout = ImageLayout.Undefined;
        if (image.Handle == 0)
            return false;

        ImageLayout? common = null;
        uint levelCount = Math.Max(range.LevelCount, 1u);
        uint layerCount = Math.Max(range.LayerCount, 1u);
        for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
        {
            uint mip = range.BaseMipLevel + mipOffset;
            for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
            {
                uint layer = range.BaseArrayLayer + layerOffset;
                if (!TryMergeTrackedImageAspectLayout(image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.ColorBit, ref common) ||
                    !TryMergeTrackedImageAspectLayout(image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.DepthBit, ref common) ||
                    !TryMergeTrackedImageAspectLayout(image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.StencilBit, ref common))
                {
                    layout = ImageLayout.Undefined;
                    return false;
                }
            }
        }

        layout = common ?? ImageLayout.Undefined;
        return common.HasValue;
    }

    private bool TryMergeTrackedImageAspectLayout(
        ulong imageHandle,
        uint mip,
        uint layer,
        ImageAspectFlags rangeAspect,
        ImageAspectFlags trackedAspect,
        ref ImageLayout? common)
    {
        if ((rangeAspect & trackedAspect) == 0)
            return true;

        VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, trackedAspect);
        if (!_trackedImageSubresourceLayouts.TryGetValue(key, out ImageLayout tracked) ||
            tracked == ImageLayout.Undefined)
        {
            return false;
        }

        if (common.HasValue && common.Value != tracked)
            return false;

        common = tracked;
        return true;
    }

    private VulkanImageLayoutStateSnapshot CaptureImageLayoutStateSnapshot(ulong signature)
    {
        VulkanTrackedImageLayoutEntry[] trackedLayouts = _trackedImageSubresourceLayouts.Count == 0
            ? Array.Empty<VulkanTrackedImageLayoutEntry>()
            : new VulkanTrackedImageLayoutEntry[_trackedImageSubresourceLayouts.Count];
        int trackedIndex = 0;
        foreach (KeyValuePair<VulkanTrackedImageSubresource, ImageLayout> pair in _trackedImageSubresourceLayouts)
            trackedLayouts[trackedIndex++] = new VulkanTrackedImageLayoutEntry(pair.Key, pair.Value);

        int physicalGroupCount = 0;
        foreach (VulkanPhysicalImageGroup group in ResourceAllocator.EnumeratePhysicalGroups())
        {
            if (group.IsAllocated)
                physicalGroupCount++;
        }

        VulkanPhysicalImageGroupLayoutEntry[] physicalGroups = physicalGroupCount == 0
            ? Array.Empty<VulkanPhysicalImageGroupLayoutEntry>()
            : new VulkanPhysicalImageGroupLayoutEntry[physicalGroupCount];
        int physicalGroupIndex = 0;
        foreach (VulkanPhysicalImageGroup group in ResourceAllocator.EnumeratePhysicalGroups())
        {
            if (!group.IsAllocated)
                continue;

            physicalGroups[physicalGroupIndex++] = new VulkanPhysicalImageGroupLayoutEntry(
                group,
                group.CaptureLayoutSnapshot());
        }

        return new VulkanImageLayoutStateSnapshot(trackedLayouts, physicalGroups, signature);
    }

    private void CaptureCommandBufferVariantImageLayoutEndState(CommandBufferCacheVariant variant)
    {
        ulong signature = ComputeImageLayoutStateSignature();
        variant.RecordedImageLayoutEndSignature = signature;
        variant.RecordedImageLayoutEndState = CaptureImageLayoutStateSnapshot(signature);
    }

    private void RestoreRecordedImageLayoutEndState(CommandBufferCacheVariant variant)
    {
        VulkanImageLayoutStateSnapshot? snapshot = variant.RecordedImageLayoutEndState;
        if (snapshot is null)
            return;

        RestoreImageLayoutStateSnapshot(snapshot);
    }

    private void RestoreImageLayoutStateSnapshot(VulkanImageLayoutStateSnapshot snapshot)
    {
        _trackedImageSubresourceLayouts.Clear();

        VulkanTrackedImageLayoutEntry[] trackedLayouts = snapshot.TrackedLayouts;
        for (int i = 0; i < trackedLayouts.Length; i++)
        {
            VulkanTrackedImageLayoutEntry entry = trackedLayouts[i];
            _trackedImageSubresourceLayouts[entry.Key] = entry.Layout;
        }

        VulkanPhysicalImageGroupLayoutEntry[] physicalGroups = snapshot.PhysicalGroups;
        for (int i = 0; i < physicalGroups.Length; i++)
        {
            VulkanPhysicalImageGroupLayoutEntry entry = physicalGroups[i];
            if (entry.Group.IsAllocated)
                entry.Group.RestoreLayoutSnapshot(entry.Layout);
        }
    }

    private ulong ComputeImageLayoutStateSignature()
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(ResourceAllocatorIdentity);
        hash.Add(ResourcePlannerRevision);

        int physicalGroupCount = 0;
        foreach (VulkanPhysicalImageGroup group in ResourceAllocator.EnumeratePhysicalGroups())
        {
            if (!group.IsAllocated)
                continue;

            physicalGroupCount++;
            hash.Add(group.Image.Handle);
            hash.Add((int)group.Format);
            hash.Add((ulong)group.Usage);
            hash.Add(group.MipLevels);
            hash.Add(group.Template.Layers);
            VulkanPhysicalImageGroup.LayoutSnapshot layoutSnapshot = group.CaptureLayoutSnapshot();
            hash.Add((int)layoutSnapshot.LastKnownLayout);
            VulkanPhysicalImageGroup.SubresourceLayoutSnapshot[] subresources = layoutSnapshot.Subresources;
            hash.Add(subresources.Length);
            for (int i = 0; i < subresources.Length; i++)
            {
                VulkanPhysicalImageGroup.SubresourceLayoutSnapshot subresource = subresources[i];
                hash.Add(subresource.MipLevel);
                hash.Add(subresource.ArrayLayer);
                hash.Add((int)subresource.Layout);
            }
        }

        hash.Add(physicalGroupCount);
        hash.Add(_trackedImageSubresourceLayouts.Count);
        foreach (KeyValuePair<VulkanTrackedImageSubresource, ImageLayout> pair in _trackedImageSubresourceLayouts)
        {
            VulkanTrackedImageSubresource key = pair.Key;
            hash.Add(key.ImageHandle);
            hash.Add(key.MipLevel);
            hash.Add(key.ArrayLayer);
            hash.Add((ulong)key.Aspect);
            hash.Add((int)pair.Value);
        }

        return hash.ToHash();
    }
}
