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
    [ThreadStatic]
    private static bool t_excludeDesktopSwapchainBarriers;
    [ThreadStatic]
    private static SemaphoreSubmitInfo[]? t_submitWaitInfoScratch;
    [ThreadStatic]
    private static SemaphoreSubmitInfo[]? t_submitSignalInfoScratch;
    [ThreadStatic]
    private static CommandBufferSubmitInfo[]? t_submitCommandBufferInfoScratch;

    private readonly ref struct DesktopSwapchainBarrierExclusionScope
    {
        private readonly bool _previous;

        public DesktopSwapchainBarrierExclusionScope(bool exclude)
        {
            _previous = t_excludeDesktopSwapchainBarriers;
            if (exclude)
                t_excludeDesktopSwapchainBarriers = true;
        }

        public void Dispose()
            => t_excludeDesktopSwapchainBarriers = _previous;
    }

    private const int VulkanQueueOperationHistoryCapacity = 64;
    private EVulkanSynchronizationBackend _activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
    private readonly object _vulkanImageLayoutLock = new();
    private readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageSubresourceState> _trackedImageSubresourceStates = new();
    private readonly Dictionary<ulong, VulkanRecordedImageLayoutState> _recordedImageLayoutsByCommandBuffer = new();
    private readonly VulkanQueueOperationRecord[] _vulkanQueueOperationHistory = new VulkanQueueOperationRecord[VulkanQueueOperationHistoryCapacity];
    private long _vulkanQueueOperationSerial;

    private bool UsesSynchronization2
        => _activeSynchronizationBackend == EVulkanSynchronizationBackend.Sync2;

    private readonly record struct VulkanTrackedImageSubresource(
        ulong ImageHandle,
        uint MipLevel,
        uint ArrayLayer,
        ImageAspectFlags Aspect);

    internal enum EVulkanImageAccessIntent : byte
    {
        Undefined,
        Present,
        ColorAttachment,
        DepthStencilAttachment,
        SampledRead,
        DepthStencilRead,
        StorageReadWrite,
        TransferRead,
        TransferWrite,
    }

    internal readonly record struct VulkanImageAccessState(
        ImageLayout Layout,
        PipelineStageFlags2 StageMask,
        AccessFlags2 AccessMask,
        uint QueueFamilyIndex,
        ImageLayout ExpectedDescriptorLayout,
        ulong Serial,
        ulong ResourceGeneration)
    {
        public static VulkanImageAccessState Undefined => new(
            ImageLayout.Undefined,
            PipelineStageFlags2.TopOfPipeBit,
            AccessFlags2.None,
            Vk.QueueFamilyIgnored,
            ImageLayout.Undefined,
            0,
            0);
    }

    private sealed class VulkanImageSubresourceState
    {
        public VulkanImageAccessState Submitted = VulkanImageAccessState.Undefined;
        public VulkanImageAccessState Completed = VulkanImageAccessState.Undefined;
        public ulong GraphicsSequence;
        public ulong TransferSequence;
        public ulong OtherSequence;
    }

    private sealed class VulkanRecordedImageLayoutState
    {
        public readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> EntrySubresources = new(8);
        public readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> Subresources = new(32);
        public readonly List<KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState>> TouchedSubresources = new(32);
        public ulong RecordingGeneration;

        public void RefreshTouchedSubresources()
        {
            TouchedSubresources.Clear();
            TouchedSubresources.EnsureCapacity(Subresources.Count);
            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> subresource in Subresources)
                TouchedSubresources.Add(subresource);
        }
    }

    private readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> _submissionImageStateScratch = new(64);

    internal readonly record struct VulkanQueueOperationRecord(
        ulong Serial,
        string Operation,
        ulong QueueHandle,
        Result Result,
        EVulkanDeviceState DeviceState,
        ulong SubmissionSerial,
        int ThreadId,
        string? Caller);

    private sealed class VulkanImageLayoutStateSnapshot(ulong signature)
    {
        public ulong Signature { get; set; } = signature;
    }

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
        => SubmitToQueueTrackedCore(
            queue,
            ref submitInfo,
            fence,
            diagnosticContext,
            out _,
            out _,
            caller);

    private Result SubmitToQueueTrackedWithDisposition(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        VulkanSubmissionDiagnosticContext diagnosticContext,
        out bool queueDispatchAttempted,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage,
        [CallerMemberName] string? caller = null)
        => SubmitToQueueTrackedCore(
            queue,
            ref submitInfo,
            fence,
            diagnosticContext,
            out queueDispatchAttempted,
            out injectedFailureStage,
            caller);

    private Result SubmitToQueueTrackedCore(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        VulkanSubmissionDiagnosticContext diagnosticContext,
        out bool queueDispatchAttempted,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage,
        string? caller)
    {
        queueDispatchAttempted = false;
        injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.None;
        using VulkanQueueOperationLease queueOperation =
            VulkanQueueOperationLease.TryEnter(_oneTimeSubmitLock, _deviceStateMachine);
        if (!queueOperation.Acquired)
        {
            RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
                new FrameOutputWorkTelemetry(SubmissionRejections: 1));
            lock (_oneTimeSubmitLock)
                RecordVulkanQueueOperation("submit-rejected", queue, Result.ErrorDeviceLost, 0, caller);
            ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
            return Result.ErrorDeviceLost;
        }

        diagnosticContext = CompleteSubmissionDiagnosticContext(queue, ref submitInfo, fence, diagnosticContext, caller);
        RecordLastVulkanSubmissionDiagnosticContext(diagnosticContext);

        if (!ValidateOrderedCommandBufferImageStateContracts(ref submitInfo, out string imageStateFailure))
        {
            RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
                new FrameOutputWorkTelemetry(SubmissionRejections: 1));
            Debug.VulkanWarning(
                "[Vulkan.Layout] Rejected queue submission before vkQueueSubmit: caller={0} reason={1}",
                caller ?? "<unknown>",
                imageStateFailure);
            RecordVulkanQueueOperation(
                "submit-rejected-image-state",
                queue,
                Result.ErrorValidationFailedExt,
                diagnosticContext.SubmissionSerial,
                caller);
            ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
            return Result.ErrorValidationFailedExt;
        }

        if (!ValidateVulkanSubmissionResourceLifetimes(
                ref submitInfo,
                in diagnosticContext,
                out string lifetimeFailure,
                out injectedFailureStage))
        {
            if (injectedFailureStage != EOpenXrStrictSpsFaultInjectionStage.None)
            {
                RecordVulkanQueueOperation(
                    "submit-injected-before-dispatch",
                    queue,
                    Result.ErrorValidationFailedExt,
                    diagnosticContext.SubmissionSerial,
                    caller);
                ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
                return Result.ErrorValidationFailedExt;
            }

            RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
                new FrameOutputWorkTelemetry(SubmissionRejections: 1));
            Debug.VulkanWarning(
                "[Vulkan.ResourceLifetime] Rejected queue submission before vkQueueSubmit: caller={0} reason={1}",
                caller ?? "<unknown>",
                lifetimeFailure);
            RecordVulkanQueueOperation(
                "submit-rejected-resource-lifetime",
                queue,
                Result.ErrorValidationFailedExt,
                diagnosticContext.SubmissionSerial,
                caller);
            ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
            return Result.ErrorValidationFailedExt;
        }

        Result result;
        try
        {
            if (diagnosticContext.OpenXrStrictSpsFaultInjectionStage ==
                EOpenXrStrictSpsFaultInjectionStage.Submit)
            {
                injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.Submit;
                RecordVulkanQueueOperation(
                    "submit-injected-before-dispatch",
                    queue,
                    Result.ErrorValidationFailedExt,
                    diagnosticContext.SubmissionSerial,
                    caller);
                ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
                return Result.ErrorValidationFailedExt;
            }

            queueDispatchAttempted = true;
            result = UsesSynchronization2
                ? SubmitToQueueSync2(queue, ref submitInfo, fence)
                : Api!.QueueSubmit(queue, 1, ref submitInfo, fence);

            RecordVulkanQueueOperation("submit", queue, result, diagnosticContext.SubmissionSerial, caller);
            if (result == Result.Success)
            {
                ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: true);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueSubmit();
                VulkanLifetimeSubmission lifetimeSubmission =
                    RecordSuccessfulVulkanSubmissionLifetime(queue, ref submitInfo, fence, diagnosticContext);
                PublishRecordedImageLayouts(ref submitInfo, lifetimeSubmission);
                AdvanceCompletedImageLayouts();
            }
            else if (result == Result.ErrorDeviceLost)
            {
                ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
                RecordFirstFailingVulkanApi($"vkQueueSubmit:{caller ?? "<unknown>"}:{result}");
                MarkDeviceLost(
                    $"QueueSubmit returned ErrorDeviceLost in {caller ?? "<unknown>"} " +
                    $"(waits={submitInfo.WaitSemaphoreCount}, signals={submitInfo.SignalSemaphoreCount}, commandBuffers={submitInfo.CommandBufferCount}, fence=0x{fence.Handle:X})");
            }
            else
            {
                ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
            }
        }
        finally
        {
            ReleaseVulkanSubmissionResourceLifetimePins(ref submitInfo);
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
        if (result == Result.Success)
        {
            NotifyVulkanQueueIdle(queue);
        }
        else if (result == Result.ErrorDeviceLost)
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
        SemaphoreSubmitInfo[] waitInfosArray = EnsureThreadScratchCapacity(
            ref t_submitWaitInfoScratch,
            waitCount);
        SemaphoreSubmitInfo[] signalInfosArray = EnsureThreadScratchCapacity(
            ref t_submitSignalInfoScratch,
            signalCount);
        CommandBufferSubmitInfo[] commandBufferInfosArray = EnsureThreadScratchCapacity(
            ref t_submitCommandBufferInfoScratch,
            commandBufferCount);

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

    private static T[] EnsureThreadScratchCapacity<T>(ref T[]? scratch, int requiredCount)
        where T : struct
    {
        if (requiredCount == 0)
            return Array.Empty<T>();
        if (scratch is null || scratch.Length < requiredCount)
            scratch = new T[Math.Max(requiredCount, 4)];
        return scratch;
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
        if (t_excludeDesktopSwapchainBarriers && imageBarrierCount > 0)
        {
            uint retainedBarrierCount = 0;
            for (uint readIndex = 0; readIndex < imageBarrierCount; readIndex++)
            {
                ImageMemoryBarrier barrier = imageBarriers[readIndex];
                if (IsDesktopSwapchainImage(barrier.Image))
                    continue;

                if (retainedBarrierCount != readIndex)
                    imageBarriers[retainedBarrierCount] = barrier;
                retainedBarrierCount++;
            }

            imageBarrierCount = retainedBarrierCount;
        }

        for (int i = 0; i < bufferBarrierCount; i++)
        {
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                bufferBarriers[i].Buffer.Handle,
                "PipelineBarrier.Buffer");
        }
        for (int i = 0; i < imageBarrierCount; i++)
        {
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Image,
                imageBarriers[i].Image.Handle,
                "PipelineBarrier.Image");
            ValidateRecordedImageBarrierOldLayout(commandBuffer, in imageBarriers[i], caller);
        }

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
            RecordImageBarrierLayouts(
                commandBuffer,
                dstStageMask,
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
                VulkanImageAccessState sourceState;
                if (imageBarriers[i].OldLayout == ImageLayout.Undefined ||
                    !TryGetRecordedImageAccessState(
                        commandBuffer,
                        imageBarriers[i].Image,
                        imageBarriers[i].SubresourceRange,
                        out sourceState))
                {
                    sourceState = ResolveVulkanImageAccessState(
                        imageBarriers[i].OldLayout,
                        imageBarriers[i].SubresourceRange.AspectMask,
                        imageBarriers[i].SrcQueueFamilyIndex);
                }
                VulkanImageAccessState destinationState = ResolveVulkanImageAccessState(
                    imageBarriers[i].NewLayout,
                    imageBarriers[i].SubresourceRange.AspectMask,
                    imageBarriers[i].DstQueueFamilyIndex);
                imageBarrierArray[i] = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcStageMask = sourceState.StageMask | srcStages2,
                    SrcAccessMask = sourceState.AccessMask | NormalizeAccessFlags2(imageBarriers[i].SrcAccessMask),
                    DstStageMask = destinationState.StageMask | dstStages2,
                    DstAccessMask = destinationState.AccessMask | NormalizeAccessFlags2(imageBarriers[i].DstAccessMask),
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

        RecordImageBarrierLayouts(
            commandBuffer,
            dstStageMask,
            imageBarrierCount,
            imageBarriers);
    }

    private void RecordImageBarrierLayouts(
        CommandBuffer commandBuffer,
        PipelineStageFlags dstStageMask,
        uint imageBarrierCount,
        ImageMemoryBarrier* imageBarriers)
    {
        if (imageBarrierCount == 0 || imageBarriers is null)
            return;

        for (uint i = 0; i < imageBarrierCount; i++)
        {
            ref ImageMemoryBarrier barrier = ref imageBarriers[i];
            RecordImageAccess(
                commandBuffer,
                barrier.Image,
                barrier.SubresourceRange,
                barrier.NewLayout,
                dstStageMask,
                barrier.DstAccessMask,
                barrier.DstQueueFamilyIndex);
        }
    }

    [Conditional("DEBUG")]
    private void ValidateRecordedImageBarrierOldLayout(
        CommandBuffer commandBuffer,
        in ImageMemoryBarrier barrier,
        string? caller)
    {
        if (barrier.OldLayout == ImageLayout.Undefined ||
            commandBuffer.Handle == 0 ||
            barrier.Image.Handle == 0 ||
            !TryGetRecordedImageLayout(
                commandBuffer,
                barrier.Image,
                barrier.SubresourceRange,
                out ImageLayout recordedOldLayout) ||
            recordedOldLayout == barrier.OldLayout)
        {
            return;
        }

        Debug.VulkanWarningEvery(
            $"Vulkan.ImageBarrier.ExplicitOldLayoutMismatch.{barrier.Image.Handle}.{caller}",
            TimeSpan.FromSeconds(2),
            "[Vulkan.Layout] Explicit image barrier oldLayout differs from the command-buffer entry state; preserving the caller contract. caller={0} commandBuffer=0x{1:X} image=0x{2:X} explicit={3} tracked={4} mip={5}+{6} layer={7}+{8} aspect={9}.",
            caller ?? "<unknown>",
            unchecked((ulong)commandBuffer.Handle),
            barrier.Image.Handle,
            barrier.OldLayout,
            recordedOldLayout,
            barrier.SubresourceRange.BaseMipLevel,
            barrier.SubresourceRange.LevelCount,
            barrier.SubresourceRange.BaseArrayLayer,
            barrier.SubresourceRange.LayerCount,
            barrier.SubresourceRange.AspectMask);
    }

    private void RecordImageAccess(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        ImageLayout layout,
        PipelineStageFlags stageMask,
        AccessFlags accessMask,
        uint queueFamilyIndex)
    {
        if (commandBuffer.Handle == 0 || image.Handle == 0)
            return;

        if (TryRecordImageAccessDelta(
                commandBuffer,
                image,
                range,
                layout,
                stageMask,
                accessMask,
                queueFamilyIndex))
        {
            return;
        }

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        ulong resourceGeneration = GetCurrentVulkanResourceGeneration(ObjectType.Image, image.Handle);
        lock (_vulkanImageLayoutLock)
        {
            if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(
                    commandBufferHandle,
                    out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer),
                };
                _recordedImageLayoutsByCommandBuffer[commandBufferHandle] = recorded;
            }

            uint levelCount = Math.Max(range.LevelCount, 1u);
            uint layerCount = Math.Max(range.LayerCount, 1u);
            for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
            {
                uint mip = range.BaseMipLevel + mipOffset;
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                {
                    uint layer = range.BaseArrayLayer + layerOffset;
                    RecordImageAspectState(recorded, image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.ColorBit, layout, stageMask, accessMask, queueFamilyIndex, resourceGeneration);
                    RecordImageAspectState(recorded, image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.DepthBit, layout, stageMask, accessMask, queueFamilyIndex, resourceGeneration);
                    RecordImageAspectState(recorded, image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.StencilBit, layout, stageMask, accessMask, queueFamilyIndex, resourceGeneration);
                }
            }
        }
    }

    private bool FlushCommandBufferImageAccessBatch(
        CommandBuffer commandBuffer,
        VulkanCommandBufferTrackingBatch batch)
    {
        if (commandBuffer.Handle == 0 || batch.PublishedImageDeltaCount >= batch.ImageAccessDeltas.Count)
            return false;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        bool contended = !Monitor.TryEnter(_vulkanImageLayoutLock);
        if (contended)
            Monitor.Enter(_vulkanImageLayoutLock);
        try
        {
            if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(
                    commandBufferHandle,
                    out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = batch.RecordingGeneration,
                };
                _recordedImageLayoutsByCommandBuffer[commandBufferHandle] = recorded;
            }

            for (int deltaIndex = batch.PublishedImageDeltaCount; deltaIndex < batch.ImageAccessDeltas.Count; deltaIndex++)
            {
                VulkanImageAccessRangeDelta delta = batch.ImageAccessDeltas[deltaIndex];
                ImageSubresourceRange range = delta.Range;
                uint levelCount = Math.Max(range.LevelCount, 1u);
                uint layerCount = Math.Max(range.LayerCount, 1u);
                for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
                {
                    uint mip = range.BaseMipLevel + mipOffset;
                    for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                    {
                        uint layer = range.BaseArrayLayer + layerOffset;
                        RecordImageAspectState(recorded, delta.ImageHandle, mip, layer, range.AspectMask, ImageAspectFlags.ColorBit,
                            delta.State.Layout, (PipelineStageFlags)delta.State.StageMask, (AccessFlags)delta.State.AccessMask,
                            delta.State.QueueFamilyIndex, delta.State.ResourceGeneration);
                        RecordImageAspectState(recorded, delta.ImageHandle, mip, layer, range.AspectMask, ImageAspectFlags.DepthBit,
                            delta.State.Layout, (PipelineStageFlags)delta.State.StageMask, (AccessFlags)delta.State.AccessMask,
                            delta.State.QueueFamilyIndex, delta.State.ResourceGeneration);
                        RecordImageAspectState(recorded, delta.ImageHandle, mip, layer, range.AspectMask, ImageAspectFlags.StencilBit,
                            delta.State.Layout, (PipelineStageFlags)delta.State.StageMask, (AccessFlags)delta.State.AccessMask,
                            delta.State.QueueFamilyIndex, delta.State.ResourceGeneration);
                    }
                }
            }

            recorded.RefreshTouchedSubresources();
        }
        finally
        {
            Monitor.Exit(_vulkanImageLayoutLock);
        }

        batch.PublishedImageDeltaCount = batch.ImageAccessDeltas.Count;
        return contended;
    }

    private void ClearTrackedImageLayouts(Image image)
    {
        ulong imageHandle = image.Handle;
        if (imageHandle == 0)
            return;

        lock (_vulkanImageLayoutLock)
        {
            RemoveImageKeys(_trackedImageSubresourceStates, imageHandle);
            foreach (VulkanRecordedImageLayoutState recorded in _recordedImageLayoutsByCommandBuffer.Values)
            {
                RemoveImageKeys(recorded.EntrySubresources, imageHandle);
                RemoveImageKeys(recorded.Subresources, imageHandle);
            }
        }
    }

    private int ClearAllTrackedImageLayouts()
    {
        lock (_vulkanImageLayoutLock)
        {
            int count = _trackedImageSubresourceStates.Count;
            _trackedImageSubresourceStates.Clear();
            _recordedImageLayoutsByCommandBuffer.Clear();
            return count;
        }
    }

    private static void RemoveImageKeys<TValue>(
        Dictionary<VulkanTrackedImageSubresource, TValue> states,
        ulong imageHandle)
    {
        if (states.Count == 0)
            return;

        VulkanTrackedImageSubresource[] keys = ArrayPool<VulkanTrackedImageSubresource>.Shared.Rent(states.Count);
        int count = 0;
        try
        {
            foreach (VulkanTrackedImageSubresource key in states.Keys)
            {
                if (key.ImageHandle == imageHandle)
                    keys[count++] = key;
            }

            for (int i = 0; i < count; i++)
                states.Remove(keys[i]);
        }
        finally
        {
            ArrayPool<VulkanTrackedImageSubresource>.Shared.Return(keys, clearArray: true);
        }
    }

    private void RecordImageAspectState(
        VulkanRecordedImageLayoutState recorded,
        ulong imageHandle,
        uint mip,
        uint layer,
        ImageAspectFlags rangeAspect,
        ImageAspectFlags trackedAspect,
        ImageLayout layout,
        PipelineStageFlags stageMask,
        AccessFlags accessMask,
        uint queueFamilyIndex,
        ulong resourceGeneration)
    {
        if ((rangeAspect & trackedAspect) == 0)
            return;

        VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, trackedAspect);
        uint resolvedQueueFamily = queueFamilyIndex;
        if (resolvedQueueFamily == Vk.QueueFamilyIgnored)
        {
            if (recorded.Subresources.TryGetValue(key, out VulkanImageAccessState priorRecorded))
                resolvedQueueFamily = priorRecorded.QueueFamilyIndex;
            else if (_trackedImageSubresourceStates.TryGetValue(key, out VulkanImageSubresourceState? priorSubmitted))
                resolvedQueueFamily = priorSubmitted.Submitted.QueueFamilyIndex;
        }

        ulong serial = unchecked((ulong)Interlocked.Increment(ref _vulkanImageLayoutTransitionSerial));
        VulkanImageAccessState layoutState = ResolveVulkanImageAccessState(
            layout,
            trackedAspect,
            resolvedQueueFamily,
            serial,
            resourceGeneration);
        PipelineStageFlags2 recordedStages = stageMask == 0
            ? layoutState.StageMask
            : NormalizePipelineStages2(stageMask);
        recorded.Subresources[key] = layoutState with
        {
            StageMask = recordedStages,
            AccessMask = NormalizeAccessFlags2(accessMask),
        };
    }

    private bool TryGetTrackedImageLayout(Image image, ImageSubresourceRange range, out ImageLayout layout)
    {
        layout = ImageLayout.Undefined;
        if (image.Handle == 0)
            return false;

        lock (_vulkanImageLayoutLock)
            return TryGetImageLayout_NoLock(null, image, range, completed: false, out layout);
    }

    private bool TryGetRecordedImageLayout(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        out ImageLayout layout)
    {
        layout = ImageLayout.Undefined;
        if (commandBuffer.Handle == 0 || image.Handle == 0)
            return false;

        if (TryGetPendingImageAccessState(commandBuffer, image, range, out VulkanImageAccessState pending))
        {
            layout = pending.Layout;
            return true;
        }

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        lock (_vulkanImageLayoutLock)
        {
            _recordedImageLayoutsByCommandBuffer.TryGetValue(handle, out VulkanRecordedImageLayoutState? recorded);
            return TryGetImageLayout_NoLock(recorded, image, range, completed: false, out layout);
        }
    }

    private bool TryGetRecordedImageAccessState(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        out VulkanImageAccessState state)
    {
        state = VulkanImageAccessState.Undefined;
        if (commandBuffer.Handle == 0 || image.Handle == 0)
            return false;

        if (TryGetPendingImageAccessState(commandBuffer, image, range, out state))
            return true;

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        lock (_vulkanImageLayoutLock)
        {
            _recordedImageLayoutsByCommandBuffer.TryGetValue(handle, out VulkanRecordedImageLayoutState? recorded);
            return TryGetImageAccessState_NoLock(recorded, image, range, completed: false, out state);
        }
    }

    private bool TryGetImageAccessState_NoLock(
        VulkanRecordedImageLayoutState? recorded,
        Image image,
        ImageSubresourceRange range,
        bool completed,
        out VulkanImageAccessState state)
    {
        VulkanImageAccessState? combined = null;
        uint levelCount = Math.Max(range.LevelCount, 1u);
        uint layerCount = Math.Max(range.LayerCount, 1u);
        for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
        {
            uint mip = range.BaseMipLevel + mipOffset;
            for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
            {
                uint layer = range.BaseArrayLayer + layerOffset;
                if (!TryMergeImageAspectAccessState_NoLock(recorded, image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.ColorBit, completed, ref combined) ||
                    !TryMergeImageAspectAccessState_NoLock(recorded, image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.DepthBit, completed, ref combined) ||
                    !TryMergeImageAspectAccessState_NoLock(recorded, image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.StencilBit, completed, ref combined))
                {
                    state = VulkanImageAccessState.Undefined;
                    return false;
                }
            }
        }

        state = combined ?? VulkanImageAccessState.Undefined;
        return combined.HasValue;
    }

    private bool TryMergeImageAspectAccessState_NoLock(
        VulkanRecordedImageLayoutState? recorded,
        ulong imageHandle,
        uint mip,
        uint layer,
        ImageAspectFlags rangeAspect,
        ImageAspectFlags trackedAspect,
        bool completed,
        ref VulkanImageAccessState? combined)
    {
        if ((rangeAspect & trackedAspect) == 0)
            return true;

        VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, trackedAspect);
        VulkanImageAccessState current;
        if (recorded is not null && recorded.Subresources.TryGetValue(key, out VulkanImageAccessState recordedState))
        {
            current = recordedState;
        }
        else if (recorded is not null && recorded.EntrySubresources.TryGetValue(key, out VulkanImageAccessState entryState))
        {
            current = entryState;
        }
        else if (_trackedImageSubresourceStates.TryGetValue(key, out VulkanImageSubresourceState? submittedState))
        {
            current = completed ? submittedState.Completed : submittedState.Submitted;
        }
        else
        {
            return false;
        }

        if (current.Layout == ImageLayout.Undefined)
            return false;
        if (!combined.HasValue)
        {
            combined = current;
            return true;
        }

        VulkanImageAccessState prior = combined.Value;
        if (prior.Layout != current.Layout ||
            (prior.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
             current.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
             prior.QueueFamilyIndex != current.QueueFamilyIndex))
        {
            return false;
        }

        combined = prior with
        {
            StageMask = prior.StageMask | current.StageMask,
            AccessMask = prior.AccessMask | current.AccessMask,
            QueueFamilyIndex = prior.QueueFamilyIndex != Vk.QueueFamilyIgnored
                ? prior.QueueFamilyIndex
                : current.QueueFamilyIndex,
            ExpectedDescriptorLayout = prior.ExpectedDescriptorLayout == current.ExpectedDescriptorLayout
                ? prior.ExpectedDescriptorLayout
                : ImageLayout.Undefined,
            Serial = Math.Max(prior.Serial, current.Serial),
        };
        return true;
    }

    private bool TryGetImageLayout_NoLock(
        VulkanRecordedImageLayoutState? recorded,
        Image image,
        ImageSubresourceRange range,
        bool completed,
        out ImageLayout layout)
    {
        ImageLayout? common = null;
        uint levelCount = Math.Max(range.LevelCount, 1u);
        uint layerCount = Math.Max(range.LayerCount, 1u);
        for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
        {
            uint mip = range.BaseMipLevel + mipOffset;
            for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
            {
                uint layer = range.BaseArrayLayer + layerOffset;
                if (!TryMergeImageAspectState_NoLock(recorded, image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.ColorBit, completed, ref common) ||
                    !TryMergeImageAspectState_NoLock(recorded, image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.DepthBit, completed, ref common) ||
                    !TryMergeImageAspectState_NoLock(recorded, image.Handle, mip, layer, range.AspectMask, ImageAspectFlags.StencilBit, completed, ref common))
                {
                    layout = ImageLayout.Undefined;
                    return false;
                }
            }
        }

        layout = common ?? ImageLayout.Undefined;
        return common.HasValue;
    }

    private bool TryMergeImageAspectState_NoLock(
        VulkanRecordedImageLayoutState? recorded,
        ulong imageHandle,
        uint mip,
        uint layer,
        ImageAspectFlags rangeAspect,
        ImageAspectFlags trackedAspect,
        bool completed,
        ref ImageLayout? common)
    {
        if ((rangeAspect & trackedAspect) == 0)
            return true;

        VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, trackedAspect);
        VulkanImageAccessState state;
        if (recorded is not null && recorded.Subresources.TryGetValue(key, out VulkanImageAccessState recordedState))
        {
            state = recordedState;
        }
        else if (recorded is not null && recorded.EntrySubresources.TryGetValue(key, out VulkanImageAccessState entryState))
        {
            state = entryState;
        }
        else if (_trackedImageSubresourceStates.TryGetValue(key, out VulkanImageSubresourceState? submittedState))
        {
            state = completed ? submittedState.Completed : submittedState.Submitted;
        }
        else
        {
            return false;
        }

        if (state.Layout == ImageLayout.Undefined)
            return false;
        if (common.HasValue && common.Value != state.Layout)
            return false;

        common = state.Layout;
        return true;
    }

    private void ResetRecordedImageLayoutState(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        lock (_vulkanImageLayoutLock)
        {
            if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(handle, out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState();
                _recordedImageLayoutsByCommandBuffer[handle] = recorded;
            }

            recorded.Subresources.Clear();
            recorded.EntrySubresources.Clear();
            recorded.TouchedSubresources.Clear();
            recorded.RecordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer);
        }
    }

    private void SeedRecordedImageLayoutState(
        CommandBuffer commandBuffer,
        CommandBuffer predecessor)
    {
        if (commandBuffer.Handle == 0 || predecessor.Handle == 0)
            return;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        ulong predecessorHandle = unchecked((ulong)predecessor.Handle);
        lock (_vulkanImageLayoutLock)
        {
            if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(
                    predecessorHandle,
                    out VulkanRecordedImageLayoutState? predecessorState))
            {
                return;
            }

            if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(
                    commandBufferHandle,
                    out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer),
                };
                _recordedImageLayoutsByCommandBuffer[commandBufferHandle] = recorded;
            }

            recorded.EntrySubresources.Clear();
            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in predecessorState.TouchedSubresources)
                recorded.EntrySubresources[pair.Key] = pair.Value;
        }
    }

    private bool ValidateOrderedCommandBufferImageStateContracts(
        ref SubmitInfo submitInfo,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (submitInfo.CommandBufferCount == 0 || submitInfo.PCommandBuffers is null)
            return true;

        lock (_vulkanImageLayoutLock)
        {
            _submissionImageStateScratch.Clear();
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong handle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (handle == 0 ||
                    !_recordedImageLayoutsByCommandBuffer.TryGetValue(handle, out VulkanRecordedImageLayoutState? recorded))
                {
                    continue;
                }

                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in recorded.EntrySubresources)
                {
                    VulkanImageAccessState actual;
                    if (!_submissionImageStateScratch.TryGetValue(pair.Key, out actual))
                    {
                        if (!_trackedImageSubresourceStates.TryGetValue(pair.Key, out VulkanImageSubresourceState? submitted))
                        {
                            failureReason =
                                $"commandBuffer[{commandIndex}]=0x{handle:X} requires missing entry state for image=0x{pair.Key.ImageHandle:X} " +
                                $"mip={pair.Key.MipLevel} layer={pair.Key.ArrayLayer} aspect={pair.Key.Aspect}";
                            return false;
                        }
                        actual = submitted.Submitted;
                    }

                    VulkanImageAccessState expected = pair.Value;
                    if (actual.Layout != expected.Layout ||
                        (actual.ResourceGeneration != 0 &&
                         expected.ResourceGeneration != 0 &&
                         actual.ResourceGeneration != expected.ResourceGeneration) ||
                        (actual.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                         expected.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                         actual.QueueFamilyIndex != expected.QueueFamilyIndex))
                    {
                        failureReason =
                            $"commandBuffer[{commandIndex}]=0x{handle:X} entry-state mismatch for image=0x{pair.Key.ImageHandle:X} " +
                            $"mip={pair.Key.MipLevel} layer={pair.Key.ArrayLayer} aspect={pair.Key.Aspect} " +
                            $"expected={expected.Layout}/queue={expected.QueueFamilyIndex}/generation={expected.ResourceGeneration} " +
                            $"actual={actual.Layout}/queue={actual.QueueFamilyIndex}/generation={actual.ResourceGeneration}";
                        return false;
                    }
                }

                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in recorded.TouchedSubresources)
                    _submissionImageStateScratch[pair.Key] = pair.Value;
            }
        }

        return true;
    }

    private void ReleaseRecordedImageLayoutState(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        lock (_vulkanImageLayoutLock)
            _recordedImageLayoutsByCommandBuffer.Remove(unchecked((ulong)commandBuffer.Handle));
    }

    private void MergeRecordedImageLayoutStates(
        CommandBuffer primary,
        ReadOnlySpan<CommandBuffer> secondaries)
    {
        if (primary.Handle == 0 || secondaries.IsEmpty)
            return;

        ulong primaryHandle = unchecked((ulong)primary.Handle);
        if (_commandBufferTrackingBatches.TryGetValue(primaryHandle, out VulkanCommandBufferTrackingBatch? primaryBatch))
            FlushCommandBufferImageAccessBatch(primary, primaryBatch);
        for (int i = 0; i < secondaries.Length; i++)
        {
            ulong secondaryHandle = unchecked((ulong)secondaries[i].Handle);
            if (_commandBufferTrackingBatches.TryGetValue(secondaryHandle, out VulkanCommandBufferTrackingBatch? secondaryBatch))
                FlushCommandBufferImageAccessBatch(secondaries[i], secondaryBatch);
        }
        lock (_vulkanImageLayoutLock)
        {
            if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(primaryHandle, out VulkanRecordedImageLayoutState? primaryState))
            {
                primaryState = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = ResolveCommandBufferRecordingGeneration(primary),
                };
                _recordedImageLayoutsByCommandBuffer[primaryHandle] = primaryState;
            }

            for (int i = 0; i < secondaries.Length; i++)
            {
                ulong secondaryHandle = unchecked((ulong)secondaries[i].Handle);
                if (secondaryHandle == 0 ||
                    !_recordedImageLayoutsByCommandBuffer.TryGetValue(secondaryHandle, out VulkanRecordedImageLayoutState? secondaryState))
                {
                    continue;
                }

                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in secondaryState.TouchedSubresources)
                    primaryState.Subresources[pair.Key] = pair.Value;
            }


            primaryState.RefreshTouchedSubresources();
        }
    }

    private void PublishRecordedImageLayouts(
        ref SubmitInfo submitInfo,
        in VulkanLifetimeSubmission submission)
    {
        if (submitInfo.CommandBufferCount == 0 || submitInfo.PCommandBuffers is null)
            return;

        lock (_vulkanImageLayoutLock)
        {
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong commandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (commandBufferHandle == 0 ||
                    !_recordedImageLayoutsByCommandBuffer.TryGetValue(commandBufferHandle, out VulkanRecordedImageLayoutState? recorded))
                {
                    continue;
                }

                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in recorded.TouchedSubresources)
                {
                    if (!_trackedImageSubresourceStates.TryGetValue(pair.Key, out VulkanImageSubresourceState? state))
                    {
                        state = new VulkanImageSubresourceState();
                        _trackedImageSubresourceStates[pair.Key] = state;
                    }

                    state.Submitted = pair.Value;
                    switch (submission.QueueDomain)
                    {
                        case EVulkanLifetimeQueueDomain.Graphics:
                            state.GraphicsSequence = Math.Max(state.GraphicsSequence, submission.QueueSequence);
                            break;
                        case EVulkanLifetimeQueueDomain.Transfer:
                            state.TransferSequence = Math.Max(state.TransferSequence, submission.QueueSequence);
                            break;
                        default:
                            state.OtherSequence = Math.Max(state.OtherSequence, submission.QueueSequence);
                            break;
                    }
                }
            }
        }
    }

    private void AdvanceCompletedImageLayouts()
    {
        ulong completedGraphics;
        ulong completedTransfer;
        ulong completedOther;
        lock (_vulkanResourceLifetimeLock)
        {
            completedGraphics = _vulkanCompletedGraphicsSequence;
            completedTransfer = _vulkanCompletedTransferSequence;
            completedOther = _vulkanCompletedOtherSequence;
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

    private void CaptureCommandBufferVariantImageLayoutEndState(CommandBufferCacheVariant variant)
    {
        ulong signature = ComputeImageLayoutStateSignature(variant.PrimaryCommandBuffer);
        variant.RecordedImageLayoutEndSignature = signature;
        if (variant.RecordedImageLayoutEndState is { } snapshot)
            snapshot.Signature = signature;
        else
            variant.RecordedImageLayoutEndState = new VulkanImageLayoutStateSnapshot(signature);
    }

    private void RestoreRecordedImageLayoutEndState(CommandBufferCacheVariant variant)
    {
        VulkanImageLayoutStateSnapshot? snapshot = variant.RecordedImageLayoutEndState;
        if (snapshot is null)
            return;

        // A cached command buffer retains its own recorded overlay. Reuse must not
        // publish that overlay into submitted state before vkQueueSubmit succeeds.
        _ = snapshot.Signature;
    }

    private void RestoreImageLayoutStateSnapshot(VulkanImageLayoutStateSnapshot snapshot)
    {
        // Kept as a migration seam for existing cache-variant call sites. Recorded
        // state is command-buffer-local and is published only after a successful
        // queue submission, so restoring a snapshot is intentionally a no-op.
        _ = snapshot.Signature;
    }

    private ulong ComputeImageLayoutStateSignature(CommandBuffer commandBuffer = default)
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
            group.AppendLayoutSignature(ref hash);
        }

        hash.Add(physicalGroupCount);
        lock (_vulkanImageLayoutLock)
        {
            VulkanRecordedImageLayoutState? recorded = null;
            if (commandBuffer.Handle != 0)
            {
                _recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)commandBuffer.Handle),
                    out recorded);
            }

            if (recorded is not null)
            {
                hash.Add(recorded.Subresources.Count);
                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in recorded.Subresources)
                    AddImageAccessStateSignature(ref hash, pair.Key, pair.Value);
            }
            else
            {
                hash.Add(_trackedImageSubresourceStates.Count);
                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageSubresourceState> pair in _trackedImageSubresourceStates)
                    AddImageAccessStateSignature(ref hash, pair.Key, pair.Value.Submitted);
            }
        }

        return hash.ToHash();
    }

    private static void AddImageAccessStateSignature(
        ref FrameOpSignatureHasher hash,
        VulkanTrackedImageSubresource key,
        VulkanImageAccessState state)
    {
        hash.Add(key.ImageHandle);
        hash.Add(key.MipLevel);
        hash.Add(key.ArrayLayer);
        hash.Add((ulong)key.Aspect);
        hash.Add((int)state.Layout);
        hash.Add((ulong)state.StageMask);
        hash.Add((ulong)state.AccessMask);
        hash.Add(state.QueueFamilyIndex);
        hash.Add((int)state.ExpectedDescriptorLayout);
    }
}
