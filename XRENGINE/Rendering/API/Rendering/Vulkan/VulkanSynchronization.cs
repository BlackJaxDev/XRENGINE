using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private EVulkanSynchronizationBackend _activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;

    private bool UsesSynchronization2
        => _activeSynchronizationBackend == EVulkanSynchronizationBackend.Sync2;

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
        [CallerMemberName] string? caller = null)
    {
        if ((srcStage & PipelineStageFlags.AllCommandsBit) != 0 ||
            (dstStage & PipelineStageFlags.AllCommandsBit) != 0)
        {
            Debug.VulkanWarning(
                $"[Vulkan][BarrierAudit] Broad AllCommandsBit barrier in {caller}. " +
                "Consider narrowing src/dst stages for performance.");
        }
    }

    private void InitializeSynchronizationBackend()
    {
        EVulkanSynchronizationBackend requestedBackend = Engine.Rendering.Settings.VulkanRobustnessSettings.SyncBackend;
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

    private Result SubmitToQueueTracked(Queue queue, ref SubmitInfo submitInfo, Fence fence)
    {
        Result result = UsesSynchronization2
            ? SubmitToQueueSync2(queue, ref submitInfo, fence)
            : Api!.QueueSubmit(queue, 1, ref submitInfo, fence);

        if (result == Result.Success)
            Engine.Rendering.Stats.RecordVulkanQueueSubmit();

        return result;
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

                return Api!.QueueSubmit2(queue, 1, &submitInfo2, fence);
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
        ImageMemoryBarrier* imageBarriers)
    {
        WarnBroadBarrierStages(srcStageMask, dstStageMask);

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

                Api!.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
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
    }
}