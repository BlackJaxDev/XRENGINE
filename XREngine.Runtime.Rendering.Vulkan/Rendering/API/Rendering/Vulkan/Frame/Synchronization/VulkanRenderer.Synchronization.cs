using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Implements Vulkan queue synchronization, command-buffer-local image-state
/// tracking, queue-family ownership validation, and synchronization diagnostics.
/// </summary>
public unsafe partial class VulkanRenderer
{
    private readonly VulkanSynchronizationThreadWorkspace _synchronizationThreadWorkspace = new();

    /// <summary>
    /// Gets allocation-reducing synchronization scratch for the calling thread.
    /// </summary>
    private VulkanSynchronizationThreadState SynchronizationThreadContext
        => _synchronizationThreadWorkspace.Current;

    /// <summary>
    /// Releases synchronization scratch retained by the calling thread.
    /// </summary>
    private void ReleaseCurrentThreadSynchronizationScratch()
        => _synchronizationThreadWorkspace.ReleaseCurrentThread();

    private const int VulkanQueueOperationHistoryCapacity = 64;
    private EVulkanSynchronizationBackend _activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
    private readonly object _vulkanImageLayoutLock = new();
    private readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageSubresourceState> _trackedImageSubresourceStates = new();
    private readonly Dictionary<ulong, (ulong ResourceGeneration, EVulkanExternalImageOwnership Ownership)>
        _externalImageOwnershipByHandle = new();
    private readonly Dictionary<ulong, VulkanRecordedImageLayoutState> _recordedImageLayoutsByCommandBuffer = new();
    private readonly VulkanQueueOperationRecord[] _vulkanQueueOperationHistory = new VulkanQueueOperationRecord[VulkanQueueOperationHistoryCapacity];
    private long _vulkanQueueOperationSerial;

    /// <summary>
    /// Gets whether queue submission and barrier emission use Vulkan
    /// synchronization2 structures and entry points.
    /// </summary>
    private bool UsesSynchronization2
        => _activeSynchronizationBackend == EVulkanSynchronizationBackend.Sync2;

    private readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> _submissionImageStateScratch = new(64);
    private readonly List<VulkanQueueSemaphoreRequirement> _submissionQueueSemaphoreRequirements = new(8);

    /// <summary>
    /// Debug-only assertion that fires when <c>AllCommandsBit</c> is used in a barrier.
    /// Callers in hot paths should route through
    /// <see cref="CmdPipelineBarrierTracked"/> which uses the active synchronization
    /// backend; this assert catches newly-introduced broad masks before they ship.
    /// </summary>
    /// <param name="srcStage">The source-stage mask being audited.</param>
    /// <param name="dstStage">The destination-stage mask being audited.</param>
    /// <param name="caller">The recording site used in the diagnostic.</param>
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
            // One shared throttle key avoids formatting a per-site key on this
            // command-recording path. The emitted message still identifies the site.
            Debug.VulkanWarningEvery(
                "Vulkan.BarrierAudit",
                TimeSpan.FromSeconds(10),
                "[Vulkan][BarrierAudit] Broad AllCommandsBit barrier originating from {0}. Consider narrowing src/dst stages for performance.",
                site);
        }
    }

    /// <summary>
    /// Selects the synchronization backend requested by renderer settings,
    /// falling back to legacy synchronization when synchronization2 is unavailable.
    /// </summary>
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

    /// <summary>
    /// Converts a legacy pipeline-stage mask to its synchronization2 equivalent,
    /// using all commands when the legacy mask is empty.
    /// </summary>
    private static PipelineStageFlags2 NormalizePipelineStages2(PipelineStageFlags stageMask)
        => (PipelineStageFlags2)(ulong)(stageMask == 0 ? PipelineStageFlags.AllCommandsBit : stageMask);

    /// <summary>
    /// Converts a legacy access mask to its synchronization2 equivalent.
    /// </summary>
    private static AccessFlags2 NormalizeAccessFlags2(AccessFlags accessMask)
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
    /// Produces the state published by the command-buffer tracker. Layouts
    /// whose Vulkan access domain is unambiguous own their stage/access tuple;
    /// a caller-provided barrier scope must not manufacture contradictory
    /// state such as shader-read layout plus color-attachment writes.
    /// <see cref="ImageLayout.General"/> retains the explicit scope because it
    /// deliberately supports multiple access domains.
    /// </summary>
    /// <returns>
    /// A canonical recorded state, narrowed to the requested scope only when that
    /// scope is compatible with the image layout.
    /// </returns>
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

        // Keep precise scopes when they agree with the semantic layout, but do
        // not publish contradictory tuples such as ShaderReadOnlyOptimal paired
        // with color-attachment writes. Those tuples cannot describe a real
        // post-execution state and make otherwise stable primaries reject reuse.
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

    /// <summary>
    /// Selects the synchronization2 signal stage for a submission based on
    /// whether it contains command buffers.
    /// </summary>
    private static PipelineStageFlags2 ResolveSignalStageMask2(
        uint commandBufferCount)
    => commandBufferCount > 0 ? PipelineStageFlags2.AllCommandsBit : PipelineStageFlags2.TopOfPipeBit;

    /// <summary>
    /// Searches a Vulkan <c>pNext</c> chain for timeline-semaphore submission
    /// metadata.
    /// </summary>
    /// <returns>The matching structure pointer, or <see langword="null"/>.</returns>
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

    /// <summary>
    /// Validates, submits, publishes, and diagnoses one queue submission.
    /// </summary>
    /// <returns>The Vulkan queue-submit result.</returns>
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

    /// <summary>
    /// Submits tracked queue work for target drivers that do not construct a
    /// renderer-private diagnostic context.
    /// </summary>
    internal Result SubmitToQueueTracked(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        string? caller)
        => SubmitToQueueTracked(
            queue,
            ref submitInfo,
            fence,
            default,
            caller);

    /// <summary>
    /// Submits tracked queue work while reporting whether native dispatch was
    /// attempted and whether fault injection rejected the submission.
    /// </summary>
    /// <returns>The Vulkan queue-submit or validation result.</returns>
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

    /// <summary>
    /// Performs the common submission transaction: device-state admission,
    /// image and lifetime validation, native dispatch, state publication, and
    /// failure cleanup.
    /// </summary>
    /// <returns>The final submission result.</returns>
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
        if (!TryAdmitVulkanDeviceOperation("vkQueueSubmit", out _))
        {
            RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
                new FrameOutputWorkTelemetry(SubmissionRejections: 1));
            lock (_oneTimeSubmitLock)
                RecordVulkanQueueOperation("submit-rejected", queue, Result.ErrorDeviceLost, 0, caller);
            ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
            return Result.ErrorDeviceLost;
        }

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

        using (VulkanCpuStageScope preparationStage = new(EVulkanCpuStage.SubmissionPreparation))
        {
            using VulkanCpuStageScope diagnosticsStage = new(EVulkanCpuStage.SubmissionDiagnostics);
            diagnosticContext = CompleteSubmissionDiagnosticContext(
                queue, ref submitInfo, fence, diagnosticContext, caller);
        }

        RecordLastVulkanSubmissionDiagnosticContext(diagnosticContext);

        bool imageStateValid;
        string imageStateFailure;
        using (VulkanCpuStageScope preparationStage = new(EVulkanCpuStage.SubmissionPreparation))
        {
            using VulkanCpuStageScope imageStateStage =
                new(EVulkanCpuStage.SubmissionImageStateValidation);
            imageStateValid = ValidateOrderedCommandBufferImageStateContracts(
                queue,
                ref submitInfo,
                out imageStateFailure);
        }

        if (!imageStateValid)
        {
            RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
                new FrameOutputWorkTelemetry(SubmissionRejections: 1));
            Debug.VulkanWarning(
                "[Vulkan.Layout] Rejected queue submission before vkQueueSubmit: caller={0} reason={1}",
                caller ?? "<unknown>",
                imageStateFailure);
            Debug.WriteAuxiliaryLog(
                "profiler-vulkan-submission-rejections.log",
                $"kind=image-state caller={caller ?? "<unknown>"} reason={imageStateFailure}");
            RecordVulkanQueueOperation(
                "submit-rejected-image-state",
                queue,
                Result.ErrorValidationFailedExt,
                diagnosticContext.SubmissionSerial,
                caller);
            ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
            return Result.ErrorValidationFailedExt;
        }

        bool resourceLifetimesValid;
        string lifetimeFailure;
        using (VulkanCpuStageScope preparationStage = new(EVulkanCpuStage.SubmissionPreparation))
        {
            using VulkanCpuStageScope resourceLifetimeStage =
                new(EVulkanCpuStage.SubmissionResourceLifetimeValidation);
            resourceLifetimesValid = ValidateVulkanSubmissionResourceLifetimes(
                ref submitInfo,
                in diagnosticContext,
                out lifetimeFailure,
                out injectedFailureStage);
        }

        if (!resourceLifetimesValid)
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
            Debug.WriteAuxiliaryLog(
                "profiler-vulkan-submission-rejections.log",
                $"kind=resource-lifetime caller={caller ?? "<unknown>"} reason={lifetimeFailure}");
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
            using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.QueueSubmit))
            {
                result = UsesSynchronization2
                    ? SubmitToQueueSync2(queue, ref submitInfo, fence)
                    : Api!.QueueSubmit(queue, 1, ref submitInfo, fence);
            }

            RecordVulkanQueueOperation("submit", queue, result, diagnosticContext.SubmissionSerial, caller);
            if (result == Result.Success)
            {
                ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: true);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueSubmit();
                using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.SubmissionPublication))
                {
                    VulkanLifetimeSubmission lifetimeSubmission =
                        RecordSuccessfulVulkanSubmissionLifetime(queue, ref submitInfo, fence, diagnosticContext);
                    PublishRecordedImageLayouts(
                        queue,
                        ref submitInfo,
                        lifetimeSubmission);
                    AdvanceCompletedImageLayouts();
                }
            }
            else if (result == Result.ErrorDeviceLost)
            {
                ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
                MarkDeviceLost(
                    $"vkQueueSubmit:{caller ?? "<unknown>"}:{result}; " +
                    $"QueueSubmit returned ErrorDeviceLost in {caller ?? "<unknown>"} " +
                    $"(waits={submitInfo.WaitSemaphoreCount}, signals={submitInfo.SignalSemaphoreCount}, commandBuffers={submitInfo.CommandBufferCount}, fence=0x{fence.Handle:X})",
                    "vkQueueSubmit",
                    result);
            }
            else
            {
                ResolveSubmissionMarkers(ref submitInfo, submissionSucceeded: false);
            }
        }
        finally
        {
            using VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.SubmissionPublication);
            ReleaseVulkanSubmissionResourceLifetimePins(ref submitInfo);
        }

        return result;
    }

    /// <summary>
    /// Waits for a queue to become idle through the device-state gate and records
    /// the operation for diagnostics and lifetime tracking.
    /// </summary>
    /// <returns>The Vulkan queue-idle result.</returns>
    internal Result WaitForQueueIdleTracked(
        Queue queue,
        [CallerMemberName] string? caller = null)
    {
        if (!TryAdmitVulkanDeviceOperation("vkQueueWaitIdle", out _))
        {
            lock (_oneTimeSubmitLock)
                RecordVulkanQueueOperation("wait-idle-rejected", queue, Result.ErrorDeviceLost, 0, caller);
            return Result.ErrorDeviceLost;
        }

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
            MarkDeviceLost(
                $"vkQueueWaitIdle:{caller ?? "<unknown>"}:{result}; " +
                $"QueueWaitIdle returned ErrorDeviceLost in {caller ?? "<unknown>"}",
                "vkQueueWaitIdle",
                result);
        }

        return result;
    }

    /// <summary>
    /// Presents through either the native swapchain or the Streamline proxy while
    /// applying device-state admission and queue-operation diagnostics.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when presentation was dispatched; otherwise
    /// <see langword="false"/>.
    /// </returns>
    private bool TryPresentToQueueTracked(
        Queue queue,
        ref PresentInfoKHR presentInfo,
        out Result result,
        out string failureReason,
        [CallerMemberName] string? caller = null)
    {
        if (!TryAdmitVulkanDeviceOperation("vkQueuePresentKHR", out failureReason))
        {
            result = Result.ErrorDeviceLost;
            lock (_oneTimeSubmitLock)
                RecordVulkanQueueOperation("present-rejected", queue, result, 0, caller);
            return false;
        }

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
            MarkDeviceLost(
                $"vkQueuePresentKHR:{caller ?? "<unknown>"}:{result}; " +
                $"QueuePresent returned ErrorDeviceLost in {caller ?? "<unknown>"}",
                "vkQueuePresentKHR",
                result);
        }

        return dispatched;
    }

    /// <summary>
    /// Appends a queue operation to the fixed-size diagnostic history ring.
    /// </summary>
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

    /// <summary>
    /// Formats the newest entries in the queue-operation diagnostic ring.
    /// </summary>
    /// <returns>An empty string when no queue operations have been recorded.</returns>
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

    /// <summary>
    /// Translates a legacy submit description to synchronization2 structures and
    /// dispatches it through the compatibility entry point.
    /// </summary>
    /// <returns>The synchronization2 queue-submit result.</returns>
    private unsafe Result SubmitToQueueSync2(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence)
    {
        int waitCount = (int)submitInfo.WaitSemaphoreCount;
        int signalCount = (int)submitInfo.SignalSemaphoreCount;
        int commandBufferCount = (int)submitInfo.CommandBufferCount;

        TimelineSemaphoreSubmitInfo* timelineInfo = FindTimelineSemaphoreSubmitInfo(submitInfo.PNext);
        SemaphoreSubmitInfo[] waitInfosArray = EnsureThreadScratchCapacity(
            ref SynchronizationThreadContext.SubmitWaitInfoScratch,
            waitCount);
        SemaphoreSubmitInfo[] signalInfosArray = EnsureThreadScratchCapacity(
            ref SynchronizationThreadContext.SubmitSignalInfoScratch,
            signalCount);
        CommandBufferSubmitInfo[] commandBufferInfosArray = EnsureThreadScratchCapacity(
            ref SynchronizationThreadContext.SubmitCommandBufferInfoScratch,
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

    /// <summary>
    /// Ensures a per-thread reusable array can hold the requested number of
    /// unmanaged submit structures.
    /// </summary>
    /// <returns>The existing or newly allocated scratch array.</returns>
    private static T[] EnsureThreadScratchCapacity<T>(
        ref T[]? scratch,
        int requiredCount)
        where T : struct
    {
        if (requiredCount == 0)
            return Array.Empty<T>();
        if (scratch is null || scratch.Length < requiredCount)
            scratch = new T[Math.Max(requiredCount, 4)];
        return scratch;
    }

    /// <summary>
    /// Records a pipeline barrier through the active synchronization backend and
    /// mirrors all image transitions into command-buffer-local tracking state.
    /// </summary>
    /// <remarks>
    /// This is the synchronization subsystem's barrier entry point. It also
    /// filters excluded desktop-swapchain barriers, tracks referenced resources,
    /// validates explicit old layouts, and records diagnostic breadcrumbs.
    /// </remarks>
    internal unsafe void CmdPipelineBarrierTracked(
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
        if (SynchronizationThreadContext.ExcludeDesktopSwapchainBarriers && imageBarrierCount > 0)
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
                srcStageMask,
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
                imageBarrierArray[i] = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    // Preserve the explicit barrier contract. Inferring stages from the
                    // image layout can introduce stages unsupported by the command
                    // buffer's queue family (for example shader stages on a transfer-only
                    // queue) and strengthen access masks beyond the caller's intent.
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

        RecordImageBarrierLayouts(
            commandBuffer,
            srcStageMask,
            dstStageMask,
            imageBarrierCount,
            imageBarriers);
    }

    /// <summary>
    /// Publishes the destination state and queue-ownership contract of every
    /// image barrier into the command buffer's local synchronization journal.
    /// </summary>
    private void RecordImageBarrierLayouts(
        CommandBuffer commandBuffer,
        PipelineStageFlags srcStageMask,
        PipelineStageFlags dstStageMask,
        uint imageBarrierCount,
        ImageMemoryBarrier* imageBarriers)
    {
        if (imageBarrierCount == 0 || imageBarriers is null)
            return;

        for (uint i = 0; i < imageBarrierCount; i++)
        {
            ref ImageMemoryBarrier barrier = ref imageBarriers[i];
            RecordQueueOwnershipTransferRequirement(
                commandBuffer,
                in barrier,
                srcStageMask,
                dstStageMask);
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

    /// <summary>
    /// Records an explicit queue-family ownership transfer described by an image
    /// barrier, ignoring barriers that do not change queue ownership.
    /// </summary>
    private void RecordQueueOwnershipTransferRequirement(
        CommandBuffer commandBuffer,
        in ImageMemoryBarrier barrier,
        PipelineStageFlags srcStageMask,
        PipelineStageFlags dstStageMask)
    {
        if (commandBuffer.Handle == 0 ||
            barrier.Image.Handle == 0 ||
            barrier.SrcQueueFamilyIndex == Vk.QueueFamilyIgnored ||
            barrier.DstQueueFamilyIndex == Vk.QueueFamilyIgnored ||
            barrier.SrcQueueFamilyIndex == barrier.DstQueueFamilyIndex)
        {
            return;
        }

        VulkanQueueOwnershipTransferRequirement requirement = new(
            barrier.Image.Handle,
            barrier.SubresourceRange,
            barrier.OldLayout,
            barrier.NewLayout,
            barrier.SrcQueueFamilyIndex,
            barrier.DstQueueFamilyIndex,
            NormalizePipelineStages2(srcStageMask),
            NormalizeAccessFlags2(barrier.SrcAccessMask),
            NormalizePipelineStages2(dstStageMask),
            NormalizeAccessFlags2(barrier.DstAccessMask),
            GetCurrentVulkanResourceGeneration(
                ObjectType.Image,
                barrier.Image.Handle));
        if (TryRecordQueueOwnershipTransferRequirement(
                commandBuffer,
                in requirement))
        {
            return;
        }

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        lock (_vulkanImageLayoutLock)
        {
            if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(
                    commandBufferHandle,
                    out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration =
                        ResolveCommandBufferRecordingGeneration(commandBuffer),
                };
                _recordedImageLayoutsByCommandBuffer[commandBufferHandle] =
                    recorded;
            }

            recorded.QueueOwnershipTransfers.Add(requirement);
        }
    }

    /// <summary>
    /// Reports when a barrier's explicit old layout disagrees with the layout
    /// already recorded for the command buffer without altering the caller's
    /// barrier contract.
    /// </summary>
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

    /// <summary>
    /// Records an image-range access transition in the command-buffer-local
    /// journal, falling back to the global journal when no recording batch exists.
    /// </summary>
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

    /// <summary>
    /// Flushes unpublished image-access and ownership-transfer deltas from a
    /// command-buffer tracking batch into the renderer's recorded-state table.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when acquiring the global image-state lock was
    /// contended; otherwise <see langword="false"/>.
    /// </returns>
    private bool FlushCommandBufferImageAccessBatch(
        CommandBuffer commandBuffer,
        VulkanCommandBufferTrackingBatch batch)
    {
        if (commandBuffer.Handle == 0 ||
            (batch.PublishedImageDeltaCount >= batch.ImageAccessDeltas.Count &&
             batch.PublishedQueueOwnershipTransferCount >=
                 batch.QueueOwnershipTransfers.Count))
        {
            return false;
        }

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

            for (int transferIndex =
                     batch.PublishedQueueOwnershipTransferCount;
                 transferIndex < batch.QueueOwnershipTransfers.Count;
                 transferIndex++)
            {
                recorded.QueueOwnershipTransfers.Add(
                    batch.QueueOwnershipTransfers[transferIndex]);
            }

            recorded.RefreshTouchedSubresources();
        }
        finally
        {
            Monitor.Exit(_vulkanImageLayoutLock);
        }

        batch.PublishedImageDeltaCount = batch.ImageAccessDeltas.Count;
        batch.PublishedQueueOwnershipTransferCount =
            batch.QueueOwnershipTransfers.Count;
        return contended;
    }

    /// <summary>
    /// Removes every tracked and recorded subresource state associated with an
    /// image handle, including external-ownership metadata.
    /// </summary>
    internal void ClearTrackedImageLayouts(Image image)
    {
        ulong imageHandle = image.Handle;
        if (imageHandle == 0)
            return;

        lock (_vulkanImageLayoutLock)
        {
            RemoveImageKeys(_trackedImageSubresourceStates, imageHandle);
            _externalImageOwnershipByHandle.Remove(imageHandle);
            foreach (VulkanRecordedImageLayoutState recorded in _recordedImageLayoutsByCommandBuffer.Values)
            {
                RemoveImageKeys(recorded.EntrySubresources, imageHandle);
                RemoveImageKeys(recorded.SecondaryDescriptorRequirements, imageHandle);
                RemoveImageKeys(recorded.Subresources, imageHandle);
            }
        }
    }

    /// <summary>
    /// Clears global and command-buffer-local image synchronization state during
    /// physical resource destruction or renderer teardown.
    /// </summary>
    /// <returns>The number of globally tracked subresources that were removed.</returns>
    private int ClearAllTrackedImageLayouts()
    {
        lock (_vulkanImageLayoutLock)
        {
            int count = _trackedImageSubresourceStates.Count;
            _trackedImageSubresourceStates.Clear();
            _externalImageOwnershipByHandle.Clear();
            _recordedImageLayoutsByCommandBuffer.Clear();
            return count;
        }
    }

    /// <summary>
    /// Removes every dictionary entry whose subresource key references the given
    /// image handle while reusing pooled key storage.
    /// </summary>
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

    /// <summary>
    /// Records one aspect of an image subresource, capturing its immutable entry
    /// contract before publishing the new command-buffer-local state.
    /// </summary>
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
        if (!recorded.Subresources.ContainsKey(key) &&
            !recorded.EntrySubresources.ContainsKey(key))
        {
            if (_trackedImageSubresourceStates.TryGetValue(
                    key,
                    out VulkanImageSubresourceState? submittedState))
            {
                recorded.EntrySubresources[key] = submittedState.Submitted;
            }
            else
            {
                // A command buffer recorded against an untracked image normally uses
                // Undefined as its first oldLayout. Submit it once so the exact entry
                // contract is published, then record a reusable variant on the next
                // frame instead of assuming that first-use transition is replay-safe.
                recorded.EntryStateIncomplete = true;
                if (!recorded.EntryStateFailure.RequiresRecording)
                {
                    recorded.EntryStateFailure = new VulkanImageEntryStateMismatch(
                        EVulkanPrimaryEntryStateMismatch.MissingSubmittedState,
                        imageHandle,
                        mip,
                        layer,
                        trackedAspect,
                        VulkanImageAccessState.Undefined,
                        VulkanImageAccessState.Undefined);
                }
            }
        }

        uint resolvedQueueFamily = queueFamilyIndex;
        if (resolvedQueueFamily == Vk.QueueFamilyIgnored)
        {
            if (recorded.Subresources.TryGetValue(key, out VulkanImageAccessState priorRecorded))
                resolvedQueueFamily = priorRecorded.QueueFamilyIndex;
            else if (_trackedImageSubresourceStates.TryGetValue(key, out VulkanImageSubresourceState? priorSubmitted))
                resolvedQueueFamily = priorSubmitted.Submitted.QueueFamilyIndex;
        }

        ulong serial = unchecked((ulong)Interlocked.Increment(ref _vulkanImageLayoutTransitionSerial));
        EVulkanExternalImageOwnership externalOwnership =
            ResolveRecordedExternalImageOwnership_NoLock(
                recorded,
                key,
                resourceGeneration);
        VulkanImageAccessState layoutState = ResolveRecordedVulkanImageAccessState(
            layout,
            trackedAspect,
            stageMask,
            accessMask,
            resolvedQueueFamily,
            serial,
            resourceGeneration) with
        {
            ExternalOwnership = externalOwnership,
        };
        recorded.Subresources[key] = layoutState;
    }

    /// <summary>
    /// Resolves external ownership for a recorded subresource from the newest
    /// command-buffer, submitted, or image-wide ownership state.
    /// </summary>
    /// <remarks>The caller must hold <c>_vulkanImageLayoutLock</c>.</remarks>
    private EVulkanExternalImageOwnership ResolveRecordedExternalImageOwnership_NoLock(
        VulkanRecordedImageLayoutState recorded,
        VulkanTrackedImageSubresource key,
        ulong resourceGeneration)
    {
        if (recorded.Subresources.TryGetValue(
                key,
                out VulkanImageAccessState recordedState))
        {
            return recordedState.ExternalOwnership;
        }

        if (_trackedImageSubresourceStates.TryGetValue(
                key,
                out VulkanImageSubresourceState? submittedState))
        {
            return submittedState.Submitted.ExternalOwnership;
        }

        return _externalImageOwnershipByHandle.TryGetValue(
                key.ImageHandle,
                out var externalState) &&
            (externalState.ResourceGeneration == 0 ||
             resourceGeneration == 0 ||
             externalState.ResourceGeneration == resourceGeneration)
                ? externalState.Ownership
                : EVulkanExternalImageOwnership.EngineOwned;
    }

    /// <summary>
    /// Resolves the submitted external ownership for the first tracked aspect in
    /// an image range, respecting Vulkan resource generations.
    /// </summary>
    private EVulkanExternalImageOwnership ResolveTrackedExternalImageOwnership(
        Image image,
        ImageSubresourceRange range,
        ulong resourceGeneration)
    {
        ImageAspectFlags aspect =
            (range.AspectMask & ImageAspectFlags.ColorBit) != 0
                ? ImageAspectFlags.ColorBit
                : (range.AspectMask & ImageAspectFlags.DepthBit) != 0
                    ? ImageAspectFlags.DepthBit
                    : ImageAspectFlags.StencilBit;
        VulkanTrackedImageSubresource key = new(
            image.Handle,
            range.BaseMipLevel,
            range.BaseArrayLayer,
            aspect);
        lock (_vulkanImageLayoutLock)
        {
            if (_trackedImageSubresourceStates.TryGetValue(
                    key,
                    out VulkanImageSubresourceState? state))
            {
                return state.Submitted.ExternalOwnership;
            }

            return _externalImageOwnershipByHandle.TryGetValue(
                    image.Handle,
                    out var externalState) &&
                (externalState.ResourceGeneration == 0 ||
                 resourceGeneration == 0 ||
                 externalState.ResourceGeneration == resourceGeneration)
                    ? externalState.Ownership
                    : EVulkanExternalImageOwnership.EngineOwned;
        }
    }

    /// <summary>
    /// Marks an OpenXR runtime image as acquired by the engine in both image-wide
    /// and existing per-subresource ownership state.
    /// </summary>
    private void PublishOpenXrExternalImageAcquireState(
        Image image,
        ImageSubresourceRange range)
    {
        if (image.Handle == 0)
            throw new InvalidOperationException(
                "An OpenXR acquire cannot publish a null Vulkan image.");

        ulong resourceGeneration = GetCurrentVulkanResourceGeneration(
            ObjectType.Image,
            image.Handle);
        lock (_vulkanImageLayoutLock)
        {
            _externalImageOwnershipByHandle[image.Handle] = (
                resourceGeneration,
                EVulkanExternalImageOwnership.OpenXrRuntimeAcquired);

            VisitTrackedImageSubresources(
                image.Handle,
                range,
                static (state, ownership) =>
                {
                    state.Submitted = state.Submitted with
                    {
                        ExternalOwnership = ownership,
                    };
                    state.Completed = state.Completed with
                    {
                        ExternalOwnership = ownership,
                    };
                },
                EVulkanExternalImageOwnership.OpenXrRuntimeAcquired);
        }
    }

    /// <summary>
    /// Records that an OpenXR image will return to runtime ownership when the
    /// containing command buffer completes.
    /// </summary>
    private void RecordOpenXrExternalImageReleasePending(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range)
    {
        if (TryRecordExternalImageOwnershipDelta(
                commandBuffer,
                image,
                range,
                EVulkanExternalImageOwnership.OpenXrRuntimeReleasePending))
        {
            return;
        }

        // A partial frame can legitimately record only engine-owned offscreen
        // work. The runtime image then has no new access delta, but its immutable
        // entry state is still the correct basis for the ordered ownership
        // publication. Seed that unchanged state into the local journal instead
        // of treating the absence of a new barrier as a recording failure.
        if (TryGetRecordedImageAccessState(
                commandBuffer,
                image,
                range,
                out VulkanImageAccessState entryState) &&
            entryState.Layout != ImageLayout.Undefined)
        {
            RecordImageAccess(
                commandBuffer,
                image,
                range,
                entryState.Layout,
                (PipelineStageFlags)(ulong)entryState.StageMask,
                (AccessFlags)(ulong)entryState.AccessMask,
                entryState.QueueFamilyIndex);
            if (TryRecordExternalImageOwnershipDelta(
                    commandBuffer,
                    image,
                    range,
                    EVulkanExternalImageOwnership.OpenXrRuntimeReleasePending))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"OpenXR command buffer 0x{commandBuffer.Handle:X} did not record or inherit a final " +
            $"state for runtime-owned image 0x{image.Handle:X}.");
    }

    /// <summary>
    /// Visits all currently tracked aspects, mip levels, and layers covered by an
    /// image range while the caller holds the image-layout lock.
    /// </summary>
    private void VisitTrackedImageSubresources(
        ulong imageHandle,
        ImageSubresourceRange range,
        Action<VulkanImageSubresourceState, EVulkanExternalImageOwnership> visitor,
        EVulkanExternalImageOwnership ownership)
    {
        uint levelCount = Math.Max(range.LevelCount, 1u);
        uint layerCount = Math.Max(range.LayerCount, 1u);
        for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
        {
            uint mip = range.BaseMipLevel + mipOffset;
            for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
            {
                uint layer = range.BaseArrayLayer + layerOffset;
                VisitAspect(ImageAspectFlags.ColorBit);
                VisitAspect(ImageAspectFlags.DepthBit);
                VisitAspect(ImageAspectFlags.StencilBit);

                void VisitAspect(ImageAspectFlags aspect)
                {
                    if ((range.AspectMask & aspect) == 0)
                        return;

                    VulkanTrackedImageSubresource key = new(
                        imageHandle,
                        mip,
                        layer,
                        aspect);
                    if (_trackedImageSubresourceStates.TryGetValue(
                            key,
                            out VulkanImageSubresourceState? state))
                    {
                        visitor(state, ownership);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Attempts to resolve one common submitted layout across an image range.
    /// </summary>
    private bool TryGetTrackedImageLayout(
        Image image,
        ImageSubresourceRange range,
        out ImageLayout layout)
    {
        layout = ImageLayout.Undefined;
        if (image.Handle == 0)
            return false;

        lock (_vulkanImageLayoutLock)
            return TryGetImageLayout_NoLock(null, image, range, completed: false, out layout);
    }

    /// <summary>
    /// Attempts to resolve one common layout from pending batch state, the
    /// command-buffer-local overlay, or submitted global state.
    /// </summary>
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

    /// <summary>
    /// Attempts to resolve a merged access state for an image range as observed
    /// by a command buffer at its current recording point.
    /// </summary>
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

    /// <summary>
    /// Merges access state across every requested subresource and aspect.
    /// </summary>
    /// <remarks>The caller must hold <c>_vulkanImageLayoutLock</c>.</remarks>
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

    /// <summary>
    /// Merges one aspect's recorded, entry, submitted, or completed access state
    /// into an aggregate range state.
    /// </summary>
    /// <remarks>The caller must hold <c>_vulkanImageLayoutLock</c>.</remarks>
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

    /// <summary>
    /// Resolves a common layout across every requested subresource and aspect.
    /// </summary>
    /// <remarks>The caller must hold <c>_vulkanImageLayoutLock</c>.</remarks>
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

    /// <summary>
    /// Merges one aspect's layout into a range-wide common-layout candidate.
    /// </summary>
    /// <remarks>The caller must hold <c>_vulkanImageLayoutLock</c>.</remarks>
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

    /// <summary>
    /// Resets the command-buffer-local image journal for a new recording
    /// generation while retaining its table allocation.
    /// </summary>
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
            recorded.SecondaryDescriptorRequirements.Clear();
            recorded.SecondaryDescriptorPayloadGenerations.Clear();
            recorded.TouchedSubresources.Clear();
            recorded.QueueOwnershipTransfers.Clear();
            recorded.EntryStateIncomplete = false;
            recorded.EntryStateFailure = default;
            recorded.RecordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer);
        }
    }

    /// <summary>
    /// Seeds a command buffer's entry contract from the final touched state of an
    /// ordered predecessor command buffer.
    /// </summary>
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
            recorded.SecondaryDescriptorRequirements.Clear();
            recorded.SecondaryDescriptorPayloadGenerations.Clear();
            recorded.QueueOwnershipTransfers.Clear();
            recorded.EntryStateIncomplete = predecessorState.EntryStateIncomplete;
            recorded.EntryStateFailure = predecessorState.EntryStateFailure;
            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in predecessorState.TouchedSubresources)
                recorded.EntrySubresources[pair.Key] = pair.Value;
        }
    }

    /// <summary>
    /// Validates command buffers in submission order against submitted image
    /// state and accumulates each command buffer's output for the next one.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when all entry and queue-ownership contracts are
    /// satisfied; otherwise <see langword="false"/>.
    /// </returns>
    private bool ValidateOrderedCommandBufferImageStateContracts(
        Queue queue,
        ref SubmitInfo submitInfo,
        out string failureReason)
    {
        failureReason = string.Empty;
        _submissionQueueSemaphoreRequirements.Clear();
        if (submitInfo.CommandBufferCount == 0 || submitInfo.PCommandBuffers is null)
            return true;

        uint submissionQueueFamilyIndex =
            ResolveVulkanQueueFamilyIndex(queue);
        ulong completedGraphicsSequence;
        ulong completedTransferSequence;
        ulong completedOtherSequence;
        lock (_resourceLifetimeTracker.SyncRoot)
        {
            completedGraphicsSequence =
                _resourceLifetimeTracker.CompletedGraphicsSequence;
            completedTransferSequence =
                _resourceLifetimeTracker.CompletedTransferSequence;
            completedOtherSequence =
                _resourceLifetimeTracker.CompletedOtherSequence;
        }

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
                            RecordPrimaryImageEntryStateMismatch(
                                new VulkanImageEntryStateMismatch(
                                    EVulkanPrimaryEntryStateMismatch.MissingSubmittedState,
                                    pair.Key.ImageHandle,
                                    pair.Key.MipLevel,
                                    pair.Key.ArrayLayer,
                                    pair.Key.Aspect,
                                    pair.Value,
                                    VulkanImageAccessState.Undefined));
                            failureReason =
                                $"commandBuffer[{commandIndex}]=0x{handle:X} requires missing entry state for image=0x{pair.Key.ImageHandle:X} " +
                                $"mip={pair.Key.MipLevel} layer={pair.Key.ArrayLayer} aspect={pair.Key.Aspect}";
                            return false;
                        }
                        actual = submitted.Submitted;
                    }

                    VulkanImageAccessState expected = pair.Value;
                    if (_trackedImageSubresourceStates.TryGetValue(
                            pair.Key,
                            out VulkanImageSubresourceState? trackedState) &&
                        trackedState.PendingQueueOwnershipRelease is
                            VulkanPendingQueueOwnershipRelease pendingRelease &&
                        !HasPairedQueueOwnershipAcquire(
                            recorded,
                            pair.Key,
                            submissionQueueFamilyIndex,
                            in pendingRelease))
                    {
                        failureReason =
                            $"commandBuffer[{commandIndex}]=0x{handle:X} accesses image=0x{pair.Key.ImageHandle:X} " +
                            $"mip={pair.Key.MipLevel} layer={pair.Key.ArrayLayer} aspect={pair.Key.Aspect} while queue ownership " +
                            $"release {pendingRelease.Requirement.SourceQueueFamilyIndex}->{pendingRelease.Requirement.DestinationQueueFamilyIndex} is pending without a paired acquire";
                        return false;
                    }

                    EVulkanPrimaryEntryStateMismatch mismatch =
                        VulkanImageEntryStateContract.Compare(actual, expected);
                    if (mismatch != EVulkanPrimaryEntryStateMismatch.None)
                    {
                        RecordPrimaryImageEntryStateMismatch(
                            new VulkanImageEntryStateMismatch(
                                mismatch,
                                pair.Key.ImageHandle,
                                pair.Key.MipLevel,
                                pair.Key.ArrayLayer,
                                pair.Key.Aspect,
                                expected,
                                actual));
                        failureReason =
                            $"commandBuffer[{commandIndex}]=0x{handle:X} entry-state mismatch for image=0x{pair.Key.ImageHandle:X} " +
                            $"mip={pair.Key.MipLevel} layer={pair.Key.ArrayLayer} aspect={pair.Key.Aspect} kind={mismatch} " +
                            $"expected={expected} actual={actual}";
                        return false;
                    }
                }

                if (!ValidateQueueOwnershipTransferRequirements(
                        recorded,
                        submissionQueueFamilyIndex,
                         ref submitInfo,
                         commandIndex,
                         handle,
                         completedGraphicsSequence,
                         completedTransferSequence,
                         completedOtherSequence,
                         out failureReason))
                {
                    return false;
                }

                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in recorded.TouchedSubresources)
                    _submissionImageStateScratch[pair.Key] = pair.Value;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates every queue-family ownership transfer recorded by one command
    /// buffer against the queue receiving the submission.
    /// </summary>
    private bool ValidateQueueOwnershipTransferRequirements(
        VulkanRecordedImageLayoutState recorded,
        uint submissionQueueFamilyIndex,
        ref SubmitInfo submitInfo,
        int commandIndex,
        ulong commandBufferHandle,
        ulong completedGraphicsSequence,
        ulong completedTransferSequence,
        ulong completedOtherSequence,
        out string failureReason)
    {
        failureReason = string.Empty;
        for (int transferIndex = 0;
             transferIndex < recorded.QueueOwnershipTransfers.Count;
             transferIndex++)
        {
            VulkanQueueOwnershipTransferRequirement requirement =
                recorded.QueueOwnershipTransfers[transferIndex];
            EVulkanQueueOwnershipTransferRole role =
                requirement.ResolveRole(submissionQueueFamilyIndex);
            if (role == EVulkanQueueOwnershipTransferRole.Invalid)
            {
                failureReason =
                    $"commandBuffer[{commandIndex}]=0x{commandBufferHandle:X} records queue ownership " +
                    $"{requirement.SourceQueueFamilyIndex}->{requirement.DestinationQueueFamilyIndex}, but submits to queue family {submissionQueueFamilyIndex}";
                return false;
            }

            uint levelCount = Math.Max(requirement.Range.LevelCount, 1u);
            uint layerCount = Math.Max(requirement.Range.LayerCount, 1u);
            for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
            {
                uint mipLevel =
                    requirement.Range.BaseMipLevel + mipOffset;
                for (uint layerOffset = 0;
                     layerOffset < layerCount;
                     layerOffset++)
                {
                    uint arrayLayer =
                        requirement.Range.BaseArrayLayer + layerOffset;
                    if (!ValidateQueueOwnershipTransferAspect(
                            in requirement,
                            role,
                            mipLevel,
                             arrayLayer,
                             ImageAspectFlags.ColorBit,
                             ref submitInfo,
                             completedGraphicsSequence,
                             completedTransferSequence,
                             completedOtherSequence,
                             out failureReason) ||
                        !ValidateQueueOwnershipTransferAspect(
                            in requirement,
                            role,
                            mipLevel,
                             arrayLayer,
                             ImageAspectFlags.DepthBit,
                             ref submitInfo,
                             completedGraphicsSequence,
                             completedTransferSequence,
                             completedOtherSequence,
                             out failureReason) ||
                        !ValidateQueueOwnershipTransferAspect(
                            in requirement,
                            role,
                            mipLevel,
                             arrayLayer,
                             ImageAspectFlags.StencilBit,
                             ref submitInfo,
                             completedGraphicsSequence,
                             completedTransferSequence,
                             completedOtherSequence,
                             out failureReason))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Validates one image aspect's release or acquire half of a queue-family
    /// ownership transfer, including the required timeline wait dependency.
    /// </summary>
    private bool ValidateQueueOwnershipTransferAspect(
        in VulkanQueueOwnershipTransferRequirement requirement,
        EVulkanQueueOwnershipTransferRole role,
        uint mipLevel,
        uint arrayLayer,
        ImageAspectFlags aspect,
        ref SubmitInfo submitInfo,
        ulong completedGraphicsSequence,
        ulong completedTransferSequence,
        ulong completedOtherSequence,
        out string failureReason)
    {
        failureReason = string.Empty;
        if ((requirement.Range.AspectMask & aspect) == 0)
            return true;

        VulkanTrackedImageSubresource key = new(
            requirement.ImageHandle,
            mipLevel,
            arrayLayer,
            aspect);
        _trackedImageSubresourceStates.TryGetValue(
            key,
            out VulkanImageSubresourceState? trackedState);

        if (role == EVulkanQueueOwnershipTransferRole.Release)
        {
            if (trackedState?.PendingQueueOwnershipRelease is not null)
            {
                failureReason =
                    $"image=0x{key.ImageHandle:X} mip={mipLevel} layer={arrayLayer} aspect={aspect} already has a pending queue-ownership release";
                return false;
            }

            if (trackedState is not null &&
                trackedState.Submitted.QueueFamilyIndex !=
                    Vk.QueueFamilyIgnored &&
                trackedState.Submitted.QueueFamilyIndex !=
                    requirement.SourceQueueFamilyIndex)
            {
                failureReason =
                    $"queue-ownership release for image=0x{key.ImageHandle:X} expects source family {requirement.SourceQueueFamilyIndex}, " +
                    $"but submitted ownership is {trackedState.Submitted.QueueFamilyIndex}";
                return false;
            }

            return true;
        }

        if (trackedState?.PendingQueueOwnershipRelease is not
            VulkanPendingQueueOwnershipRelease pendingRelease)
        {
            failureReason =
                $"queue-ownership acquire for image=0x{key.ImageHandle:X} mip={mipLevel} layer={arrayLayer} aspect={aspect} " +
                $"has no submitted release from family {requirement.SourceQueueFamilyIndex}";
            return false;
        }
        if (!pendingRelease.Requirement.IsPairedWith(
                in requirement,
                key.ImageHandle,
                key.MipLevel,
                key.ArrayLayer,
                key.Aspect))
        {
            failureReason =
                $"queue-ownership acquire for image=0x{key.ImageHandle:X} does not match its submitted release; " +
                $"release={pendingRelease.Requirement.SourceQueueFamilyIndex}->{pendingRelease.Requirement.DestinationQueueFamilyIndex} " +
                $"{pendingRelease.Requirement.OldLayout}->{pendingRelease.Requirement.NewLayout}, " +
                $"acquire={requirement.SourceQueueFamilyIndex}->{requirement.DestinationQueueFamilyIndex} " +
                $"{requirement.OldLayout}->{requirement.NewLayout}";
            return false;
        }

        VulkanLifetimeSubmission releaseSubmission =
            pendingRelease.Submission;
        if (IsVulkanSubmissionCompleted(
                in releaseSubmission,
                completedGraphicsSequence,
                completedTransferSequence,
                completedOtherSequence))
            return true;

        VulkanQueueSemaphoreRequirement semaphoreRequirement = new(
            releaseSubmission.TimelineSemaphoreHandle,
            releaseSubmission.TimelineValue,
            requirement.DestinationStageMask,
            requirement.SourceQueueFamilyIndex,
            requirement.DestinationQueueFamilyIndex);
        if (!semaphoreRequirement.IsValid)
        {
            failureReason =
                $"queue-ownership acquire for image=0x{key.ImageHandle:X} depends on an incomplete source submission that published no timeline semaphore";
            return false;
        }

        if (!_submissionQueueSemaphoreRequirements.Contains(
                semaphoreRequirement))
        {
            _submissionQueueSemaphoreRequirements.Add(
                semaphoreRequirement);
        }
        if (SubmissionSatisfiesQueueSemaphoreRequirement(
                ref submitInfo,
                in semaphoreRequirement))
        {
            return true;
        }

        failureReason =
            $"queue-ownership acquire for image=0x{key.ImageHandle:X} requires timeline semaphore " +
            $"0x{semaphoreRequirement.SemaphoreHandle:X} value {semaphoreRequirement.Value} at stages {semaphoreRequirement.WaitStageMask}";
        return false;
    }

    /// <summary>
    /// Determines whether a lifetime-tracked submission has completed in its
    /// queue domain.
    /// </summary>
    private static bool IsVulkanSubmissionCompleted(
        in VulkanLifetimeSubmission submission,
        ulong completedGraphicsSequence,
        ulong completedTransferSequence,
        ulong completedOtherSequence)
    {
        if (submission.QueueSequence == 0ul)
            return false;

        return submission.QueueDomain switch
        {
            EVulkanLifetimeQueueDomain.Graphics =>
                submission.QueueSequence <= completedGraphicsSequence,
            EVulkanLifetimeQueueDomain.Transfer =>
                submission.QueueSequence <= completedTransferSequence,
            _ => submission.QueueSequence <= completedOtherSequence,
        };
    }

    /// <summary>
    /// Determines whether a command buffer records the acquire paired with a
    /// pending queue-ownership release for a specific subresource.
    /// </summary>
    private static bool HasPairedQueueOwnershipAcquire(
        VulkanRecordedImageLayoutState recorded,
        VulkanTrackedImageSubresource key,
        uint submissionQueueFamilyIndex,
        in VulkanPendingQueueOwnershipRelease pendingRelease)
    {
        for (int transferIndex = 0;
             transferIndex < recorded.QueueOwnershipTransfers.Count;
             transferIndex++)
        {
            VulkanQueueOwnershipTransferRequirement requirement =
                recorded.QueueOwnershipTransfers[transferIndex];
            if (requirement.ResolveRole(submissionQueueFamilyIndex) ==
                    EVulkanQueueOwnershipTransferRole.Acquire &&
                pendingRelease.Requirement.IsPairedWith(
                    in requirement,
                    key.ImageHandle,
                    key.MipLevel,
                    key.ArrayLayer,
                    key.Aspect))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether the submission's timeline waits satisfy a queue-family
    /// ownership acquire dependency.
    /// </summary>
    private static bool SubmissionSatisfiesQueueSemaphoreRequirement(
        ref SubmitInfo submitInfo,
        in VulkanQueueSemaphoreRequirement requirement)
    {
        TimelineSemaphoreSubmitInfo* timelineInfo =
            FindTimelineSemaphoreSubmitInfo(submitInfo.PNext);
        if (timelineInfo is null ||
            timelineInfo->PWaitSemaphoreValues is null ||
            submitInfo.PWaitSemaphores is null ||
            submitInfo.PWaitDstStageMask is null)
        {
            return false;
        }

        uint waitValueCount =
            timelineInfo->WaitSemaphoreValueCount;
        for (uint waitIndex = 0;
             waitIndex < submitInfo.WaitSemaphoreCount &&
             waitIndex < waitValueCount;
             waitIndex++)
        {
            if (requirement.IsSatisfiedBy(
                    submitInfo.PWaitSemaphores[waitIndex].Handle,
                    timelineInfo->PWaitSemaphoreValues[waitIndex],
                    NormalizePipelineStages2(
                        submitInfo.PWaitDstStageMask[waitIndex])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a native queue handle to the renderer's configured Vulkan queue
    /// family index.
    /// </summary>
    /// <returns><see cref="Vk.QueueFamilyIgnored"/> for an unknown queue.</returns>
    private uint ResolveVulkanQueueFamilyIndex(Queue queue)
    {
        QueueFamilyIndices families = FamilyQueueIndices;
        if (queue.Handle == graphicsQueue.Handle ||
            queue.Handle == secondaryGraphicsQueue.Handle)
        {
            return families.GraphicsFamilyIndex ??
                   Vk.QueueFamilyIgnored;
        }
        if (queue.Handle == computeQueue.Handle)
        {
            return families.ComputeFamilyIndex ??
                   families.GraphicsFamilyIndex ??
                   Vk.QueueFamilyIgnored;
        }
        if (queue.Handle == transferQueue.Handle)
        {
            return families.TransferFamilyIndex ??
                   families.GraphicsFamilyIndex ??
                   Vk.QueueFamilyIgnored;
        }
        if (queue.Handle == presentQueue.Handle)
        {
            return families.PresentFamilyIndex ??
                   families.GraphicsFamilyIndex ??
                   Vk.QueueFamilyIgnored;
        }

        return Vk.QueueFamilyIgnored;
    }

    /// <summary>
    /// Removes the command-buffer-local image journal for a retired command
    /// buffer.
    /// </summary>
    private void ReleaseRecordedImageLayoutState(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        lock (_vulkanImageLayoutLock)
            _recordedImageLayoutsByCommandBuffer.Remove(unchecked((ulong)commandBuffer.Handle));
    }

    /// <summary>
    /// Merges secondary command-buffer entry and final image states into the
    /// primary command buffer that executes them.
    /// </summary>
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

                if (secondaryState.EntryStateIncomplete)
                {
                    primaryState.EntryStateIncomplete = true;
                    if (!primaryState.EntryStateFailure.RequiresRecording)
                    {
                        primaryState.EntryStateFailure =
                            secondaryState.EntryStateFailure.RequiresRecording
                                ? secondaryState.EntryStateFailure
                                : new VulkanImageEntryStateMismatch(
                                    EVulkanPrimaryEntryStateMismatch.IncompleteSnapshot,
                                    0,
                                    0,
                                    0,
                                    ImageAspectFlags.None,
                                    VulkanImageAccessState.Undefined,
                                    VulkanImageAccessState.Undefined);
                    }
                }
                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in secondaryState.EntrySubresources)
                {
                    if (primaryState.Subresources.TryGetValue(pair.Key, out VulkanImageAccessState priorPrimaryState))
                    {
                        EVulkanPrimaryEntryStateMismatch mismatch =
                            VulkanImageEntryStateContract.Compare(
                                priorPrimaryState,
                                pair.Value);
                        if (mismatch != EVulkanPrimaryEntryStateMismatch.None)
                        {
                            primaryState.EntryStateIncomplete = true;
                            if (!primaryState.EntryStateFailure.RequiresRecording)
                            {
                                primaryState.EntryStateFailure =
                                    new VulkanImageEntryStateMismatch(
                                        mismatch,
                                        pair.Key.ImageHandle,
                                        pair.Key.MipLevel,
                                        pair.Key.ArrayLayer,
                                        pair.Key.Aspect,
                                        pair.Value,
                                        priorPrimaryState);
                            }
                        }
                        continue;
                    }

                    if (primaryState.EntrySubresources.TryGetValue(
                            pair.Key,
                            out VulkanImageAccessState existingEntryState))
                    {
                        EVulkanPrimaryEntryStateMismatch mismatch =
                            VulkanImageEntryStateContract.Compare(
                                existingEntryState,
                                pair.Value);
                        if (mismatch != EVulkanPrimaryEntryStateMismatch.None)
                        {
                            primaryState.EntryStateIncomplete = true;
                            if (!primaryState.EntryStateFailure.RequiresRecording)
                            {
                                primaryState.EntryStateFailure =
                                    new VulkanImageEntryStateMismatch(
                                        mismatch,
                                        pair.Key.ImageHandle,
                                        pair.Key.MipLevel,
                                        pair.Key.ArrayLayer,
                                        pair.Key.Aspect,
                                        pair.Value,
                                        existingEntryState);
                            }
                        }
                    }
                    else
                    {
                        primaryState.EntrySubresources[pair.Key] = pair.Value;
                    }
                }

                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in secondaryState.TouchedSubresources)
                {
                    primaryState.Subresources[pair.Key] = pair.Value;
                }
                primaryState.QueueOwnershipTransfers.AddRange(
                    secondaryState.QueueOwnershipTransfers);
            }


            primaryState.RefreshTouchedSubresources();
        }
    }

    /// <summary>
    /// Emits the image barriers required by descriptors baked into a secondary
    /// command buffer. This must run on the primary before its rendering scope
    /// begins; a secondary can declare its entry layout but cannot perform the
    /// external transition that establishes it.
    /// </summary>
    /// <param name="primary">
    /// The primary command buffer that must establish descriptor layouts.
    /// </param>
    /// <param name="secondary">
    /// The secondary command buffer declaring descriptor entry requirements.
    /// </param>
    private void TransitionSecondaryDescriptorImagesForExecution(
        CommandBuffer primary,
        CommandBuffer secondary)
    {
        if (primary.Handle == 0 || secondary.Handle == 0)
            return;

        CommandBufferRecordingScratch scratch = _commandBufferRecordingScratch.Value!;
        Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> requirements =
            scratch.SecondaryDescriptorImageRequirementMap;
        requirements.Clear();
        try
        {
            lock (_vulkanImageLayoutLock)
            {
                if (_recordedImageLayoutsByCommandBuffer.TryGetValue(
                        unchecked((ulong)secondary.Handle),
                        out VulkanRecordedImageLayoutState? secondaryState))
                {
                    requirements.EnsureCapacity(
                        secondaryState.SecondaryDescriptorRequirements.Count);
                    foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> requirement in
                             secondaryState.SecondaryDescriptorRequirements)
                    {
                        MergeSecondaryDescriptorImageRequirement(
                            requirements,
                            requirement.Key,
                            requirement.Value,
                            secondary);
                    }
                }
            }

            TransitionSecondaryDescriptorImageRequirementsForExecution(
                primary,
                requirements);
        }
        finally
        {
            requirements.Clear();
        }
    }

    /// <summary>
    /// Determines whether a secondary's frozen descriptor-image requirements were
    /// captured from the exact descriptor payloads that remain published for this
    /// submission. Update-after-bind can retain a descriptor-set handle while its
    /// image/view/layout payload changes, so handle identity alone is insufficient.
    /// </summary>
    private bool HasCurrentSecondaryDescriptorPayloadRequirements(CommandBuffer secondary)
    {
        if (secondary.Handle == 0)
            return false;

        ulong secondaryHandle = unchecked((ulong)secondary.Handle);
        lock (_resourceLifetimeTracker.SyncRoot)
        {
            lock (_vulkanImageLayoutLock)
            {
                if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(
                        secondaryHandle,
                        out VulkanRecordedImageLayoutState? recorded) ||
                    recorded.SecondaryDescriptorPayloadGenerations.Count == 0)
                {
                    return false;
                }

                foreach (KeyValuePair<ulong, ulong> payload in
                         recorded.SecondaryDescriptorPayloadGenerations)
                {
                    if (!_resourceLifetimeTracker.PublishedDescriptorSets.TryGetValue(
                            payload.Key,
                            out VulkanPublishedDescriptorSetSnapshot? current) ||
                        current.Generation != payload.Value)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    /// <summary>
    /// Reads the publication generation of one descriptor set while preserving
    /// the lifetime tracker's synchronization boundary. Prepared secondary keys
    /// use this instead of a renderer-wide descriptor generation.
    /// </summary>
    private bool TryGetPublishedDescriptorSetGeneration(
        DescriptorSet descriptorSet,
        out ulong generation)
    {
        generation = 0UL;
        if (descriptorSet.Handle == 0)
            return false;

        lock (_resourceLifetimeTracker.SyncRoot)
        {
            if (!_resourceLifetimeTracker.PublishedDescriptorSets.TryGetValue(
                    descriptorSet.Handle,
                    out VulkanPublishedDescriptorSetSnapshot? snapshot) ||
                snapshot.Generation == 0UL)
            {
                return false;
            }

            generation = snapshot.Generation;
            return true;
        }
    }

    /// <summary>
    /// Establishes the union of descriptor image entry requirements for a
    /// scheduled secondary-command-buffer run. Material textures are commonly
    /// shared by many mesh packets, so collecting them under one layout lock
    /// avoids repeating the same state lookup for every cached packet.
    /// </summary>
    private void TransitionSecondaryDescriptorImagesForExecution(
        CommandBuffer primary,
        CommandBuffer[] secondaryBuffers,
        int secondaryCount)
    {
        if (primary.Handle == 0 || secondaryCount <= 0)
            return;

        CommandBufferRecordingScratch scratch = _commandBufferRecordingScratch.Value!;
        Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> requirements =
            scratch.SecondaryDescriptorImageRequirementMap;
        requirements.Clear();
        try
        {
            lock (_vulkanImageLayoutLock)
            {
                for (int secondaryIndex = 0;
                     secondaryIndex < secondaryCount;
                     secondaryIndex++)
                {
                    CommandBuffer secondary = secondaryBuffers[secondaryIndex];
                    if (secondary.Handle == 0 ||
                        !_recordedImageLayoutsByCommandBuffer.TryGetValue(
                            unchecked((ulong)secondary.Handle),
                            out VulkanRecordedImageLayoutState? secondaryState))
                    {
                        continue;
                    }

                    requirements.EnsureCapacity(
                        requirements.Count +
                        secondaryState.SecondaryDescriptorRequirements.Count);
                    foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> requirement in
                             secondaryState.SecondaryDescriptorRequirements)
                    {
                        MergeSecondaryDescriptorImageRequirement(
                            requirements,
                            requirement.Key,
                            requirement.Value,
                            secondary);
                    }
                }
            }

            TransitionSecondaryDescriptorImageRequirementsForExecution(
                primary,
                requirements);
        }
        finally
        {
            requirements.Clear();
        }
    }

    private static void MergeSecondaryDescriptorImageRequirement(
        Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> requirements,
        in VulkanTrackedImageSubresource key,
        in VulkanImageAccessState requiredState,
        CommandBuffer secondary)
    {
        if (!requirements.TryGetValue(key, out VulkanImageAccessState existing))
        {
            requirements.Add(key, requiredState);
            return;
        }

        bool queueFamiliesConflict =
            existing.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
            requiredState.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
            existing.QueueFamilyIndex != requiredState.QueueFamilyIndex;
        bool resourceGenerationsConflict =
            existing.ResourceGeneration != 0 &&
            requiredState.ResourceGeneration != 0 &&
            existing.ResourceGeneration != requiredState.ResourceGeneration;
        if (existing.Layout != requiredState.Layout ||
            queueFamiliesConflict ||
            resourceGenerationsConflict ||
            existing.ExpectedDescriptorLayout != requiredState.ExpectedDescriptorLayout ||
            existing.ExternalOwnership != requiredState.ExternalOwnership)
        {
            throw new InvalidOperationException(
                $"Secondary command buffer 0x{secondary.Handle:X} publishes an incompatible descriptor entry requirement for image 0x{key.ImageHandle:X}. " +
                $"existing={existing.Layout}/queue={existing.QueueFamilyIndex}/generation={existing.ResourceGeneration}/descriptor={existing.ExpectedDescriptorLayout}/ownership={existing.ExternalOwnership}; " +
                $"required={requiredState.Layout}/queue={requiredState.QueueFamilyIndex}/generation={requiredState.ResourceGeneration}/descriptor={requiredState.ExpectedDescriptorLayout}/ownership={requiredState.ExternalOwnership}.");
        }

        requirements[key] = existing with
        {
            StageMask = existing.StageMask | requiredState.StageMask,
            AccessMask = existing.AccessMask | requiredState.AccessMask,
            QueueFamilyIndex = existing.QueueFamilyIndex != Vk.QueueFamilyIgnored
                ? existing.QueueFamilyIndex
                : requiredState.QueueFamilyIndex,
            Serial = Math.Max(existing.Serial, requiredState.Serial),
            ResourceGeneration = existing.ResourceGeneration != 0
                ? existing.ResourceGeneration
                : requiredState.ResourceGeneration,
        };
    }

    private void TransitionSecondaryDescriptorImageRequirementsForExecution(
        CommandBuffer primary,
        Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> requirements)
    {

        if (requirements.Count == 0)
            return;

        ImageMemoryBarrier[] barriers = ArrayPool<ImageMemoryBarrier>.Shared.Rent(requirements.Count);
        int barrierCount = 0;
        PipelineStageFlags sourceStages = PipelineStageFlags.None;
        PipelineStageFlags destinationStages = PipelineStageFlags.None;
        try
        {
            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> requirement in
                     requirements)
            {
                VulkanTrackedImageSubresource key = requirement.Key;
                VulkanImageAccessState requiredState = requirement.Value;
                Image image = new(key.ImageHandle);
                ImageSubresourceRange range = new()
                {
                    AspectMask = key.Aspect,
                    BaseMipLevel = key.MipLevel,
                    LevelCount = 1,
                    BaseArrayLayer = key.ArrayLayer,
                    LayerCount = 1,
                };

                ulong currentGeneration = GetCurrentVulkanResourceGeneration(ObjectType.Image, key.ImageHandle);
                if (requiredState.ResourceGeneration != 0 &&
                    currentGeneration != requiredState.ResourceGeneration)
                {
                    throw new InvalidOperationException(
                        $"Secondary command-buffer run requires image 0x{key.ImageHandle:X} " +
                        $"generation {requiredState.ResourceGeneration}, but generation {currentGeneration} is published.");
                }

                VulkanImageAccessState priorState;
                if (!TryGetRecordedImageAccessState(primary, image, range, out priorState))
                {
                    if (currentGeneration == 0)
                        continue;

                    priorState = VulkanImageAccessState.Undefined with
                    {
                        ResourceGeneration = currentGeneration,
                    };
                }

                EVulkanPrimaryEntryStateMismatch mismatch =
                    VulkanImageEntryStateContract.Compare(
                        priorState,
                        requiredState);
                if (mismatch == EVulkanPrimaryEntryStateMismatch.None)
                    continue;
                if (mismatch is
                    EVulkanPrimaryEntryStateMismatch.ResourceGeneration or
                    EVulkanPrimaryEntryStateMismatch.QueueFamily)
                {
                    throw new InvalidOperationException(
                        $"Secondary command-buffer run cannot establish image 0x{key.ImageHandle:X} " +
                        $"entry state because {mismatch} differs. " +
                        $"expected={requiredState.Layout}/queue={requiredState.QueueFamilyIndex}/generation={requiredState.ResourceGeneration} " +
                        $"actual={priorState.Layout}/queue={priorState.QueueFamilyIndex}/generation={priorState.ResourceGeneration}.");
                }

                uint queueFamilyIndex = priorState.QueueFamilyIndex;
                barriers[barrierCount++] = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = (AccessFlags)(ulong)priorState.AccessMask,
                    DstAccessMask = (AccessFlags)(ulong)requiredState.AccessMask,
                    OldLayout = priorState.Layout,
                    NewLayout = requiredState.Layout,
                    SrcQueueFamilyIndex = queueFamilyIndex,
                    DstQueueFamilyIndex = queueFamilyIndex,
                    Image = image,
                    SubresourceRange = range,
                };
                sourceStages |= (PipelineStageFlags)(ulong)priorState.StageMask;
                destinationStages |= (PipelineStageFlags)(ulong)requiredState.StageMask;
            }

            if (barrierCount == 0)
                return;

            fixed (ImageMemoryBarrier* barrierPtr = barriers)
            {
                CmdPipelineBarrierTracked(
                    primary,
                    sourceStages,
                    destinationStages,
                    DependencyFlags.None,
                    0,
                    null,
                    0,
                    null,
                    (uint)barrierCount,
                    barrierPtr,
                    nameof(TransitionSecondaryDescriptorImagesForExecution));
            }
        }
        finally
        {
            ArrayPool<ImageMemoryBarrier>.Shared.Return(barriers, clearArray: true);
        }
    }

    /// <summary>
    /// Finds the most recent queue-ownership transfer covering a subresource and
    /// resolves the submitting queue's role in that transfer.
    /// </summary>
    private static bool TryResolveQueueOwnershipTransfer(
        VulkanRecordedImageLayoutState recorded,
        VulkanTrackedImageSubresource key,
        uint submissionQueueFamilyIndex,
        out VulkanQueueOwnershipTransferRequirement requirement,
        out EVulkanQueueOwnershipTransferRole role)
    {
        for (int transferIndex =
                 recorded.QueueOwnershipTransfers.Count - 1;
             transferIndex >= 0;
             transferIndex--)
        {
            VulkanQueueOwnershipTransferRequirement candidate =
                recorded.QueueOwnershipTransfers[transferIndex];
            EVulkanQueueOwnershipTransferRole candidateRole =
                candidate.ResolveRole(submissionQueueFamilyIndex);
            if (candidateRole ==
                    EVulkanQueueOwnershipTransferRole.Invalid ||
                !candidate.Contains(
                    key.ImageHandle,
                    key.MipLevel,
                    key.ArrayLayer,
                    key.Aspect))
            {
                continue;
            }

            requirement = candidate;
            role = candidateRole;
            return true;
        }

        requirement = default;
        role = EVulkanQueueOwnershipTransferRole.Invalid;
        return false;
    }

    /// <summary>
    /// Publishes successfully submitted command-buffer image states into the
    /// global submitted-state table and records completion sequences.
    /// </summary>
    private void PublishRecordedImageLayouts(
        Queue queue,
        ref SubmitInfo submitInfo,
        in VulkanLifetimeSubmission submission)
    {
        if (submitInfo.CommandBufferCount == 0 || submitInfo.PCommandBuffers is null)
            return;

        uint submissionQueueFamilyIndex =
            ResolveVulkanQueueFamilyIndex(queue);
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
                    ulong currentGeneration = GetCurrentVulkanResourceGeneration(
                        ObjectType.Image,
                        pair.Key.ImageHandle);
                    if (pair.Value.ResourceGeneration != 0 &&
                        currentGeneration != pair.Value.ResourceGeneration)
                    {
                        // The numeric VkImage handle was recycled after this submission
                        // was queued. Its layout belongs to the retired generation and
                        // must not repopulate state cleared for the replacement image.
                        continue;
                    }

                    if (!_trackedImageSubresourceStates.TryGetValue(pair.Key, out VulkanImageSubresourceState? state))
                    {
                        state = new VulkanImageSubresourceState();
                        _trackedImageSubresourceStates[pair.Key] = state;
                    }

                    VulkanImageAccessState publishedState = pair.Value;
                    if (TryResolveQueueOwnershipTransfer(
                            recorded,
                            pair.Key,
                            submissionQueueFamilyIndex,
                            out VulkanQueueOwnershipTransferRequirement
                                ownershipRequirement,
                            out EVulkanQueueOwnershipTransferRole
                                ownershipRole))
                    {
                        if (ownershipRole ==
                            EVulkanQueueOwnershipTransferRole.Release)
                        {
                            publishedState = publishedState with
                            {
                                QueueFamilyIndex =
                                    ownershipRequirement
                                        .SourceQueueFamilyIndex,
                            };
                            state.PendingQueueOwnershipRelease =
                                new VulkanPendingQueueOwnershipRelease(
                                    ownershipRequirement,
                                    submission);
                        }
                        else
                        {
                            state.PendingQueueOwnershipRelease = null;
                        }
                    }

                    state.Submitted = publishedState;
                    if (publishedState.ExternalOwnership !=
                        EVulkanExternalImageOwnership.EngineOwned)
                    {
                        _externalImageOwnershipByHandle[pair.Key.ImageHandle] = (
                            publishedState.ResourceGeneration,
                            publishedState.ExternalOwnership);
                    }
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

    /// <summary>
    /// Advances submitted image states to completed state once every queue-domain
    /// sequence associated with the subresource has completed.
    /// </summary>
    private void AdvanceCompletedImageLayouts()
    {
        ulong completedGraphics;
        ulong completedTransfer;
        ulong completedOther;
        lock (_resourceLifetimeTracker.SyncRoot)
        {
            completedGraphics = _resourceLifetimeTracker.CompletedGraphicsSequence;
            completedTransfer = _resourceLifetimeTracker.CompletedTransferSequence;
            completedOther = _resourceLifetimeTracker.CompletedOtherSequence;
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

    /// <summary>
    /// Captures the recorded image-layout end-state signature for a cached command
    /// buffer variant.
    /// </summary>
    private void CaptureCommandBufferVariantImageLayoutEndState(
        PrimaryCommandArtifactOwner variant)
    {
        ulong signature = ComputeImageLayoutStateSignature(variant.PrimaryCommandBuffer);
        variant.RecordedImageLayoutEndSignature = signature;
        if (variant.RecordedImageLayoutEndState is { } snapshot)
            snapshot.Signature = signature;
        else
            variant.RecordedImageLayoutEndState = new VulkanImageLayoutStateSnapshot(signature);

    }

    /// <summary>
    /// Preserves the migration seam for cached variants without publishing their
    /// command-buffer-local state before a successful submission.
    /// </summary>
    private void RestoreRecordedImageLayoutEndState(
        PrimaryCommandArtifactOwner variant)
    {
        VulkanImageLayoutStateSnapshot? snapshot = variant.RecordedImageLayoutEndState;
        if (snapshot is null)
            return;

        // A cached command buffer retains its own recorded overlay. Reuse must not
        // publish that overlay into submitted state before vkQueueSubmit succeeds.
        _ = snapshot.Signature;
    }

    /// <summary>
    /// Retains the legacy snapshot restoration seam; publication is intentionally
    /// deferred until queue submission succeeds.
    /// </summary>
    private void RestoreImageLayoutStateSnapshot(
        VulkanImageLayoutStateSnapshot snapshot)
    {
        // Kept as a migration seam for existing cache-variant call sites. Recorded
        // state is command-buffer-local and is published only after a successful
        // queue submission, so restoring a snapshot is intentionally a no-op.
        _ = snapshot.Signature;
    }

    /// <summary>
    /// Checks only the image subresources consumed by a recorded command buffer.
    /// Global layout state also contains unrelated streaming uploads, histories,
    /// and other output variants; changes to those resources must not invalidate
    /// an otherwise compatible cached primary.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a consumed image entry state is no longer
    /// compatible with global submitted state.
    /// </returns>
    private bool HasRecordedImageEntryStateMismatch(CommandBuffer commandBuffer)
        => TryGetRecordedImageEntryStateMismatch(
            commandBuffer,
            out _);

    /// <summary>
    /// Determines whether a command buffer captured a complete image entry-state
    /// contract suitable for reuse.
    /// </summary>
    private bool HasCompleteRecordedImageEntrySnapshot(
        CommandBuffer commandBuffer,
        out VulkanImageEntryStateMismatch failure)
    {
        failure = default;
        if (commandBuffer.Handle == 0)
        {
            failure = new VulkanImageEntryStateMismatch(
                EVulkanPrimaryEntryStateMismatch.MissingCommandBufferState,
                0,
                0,
                0,
                ImageAspectFlags.None,
                VulkanImageAccessState.Undefined,
                VulkanImageAccessState.Undefined);
            return false;
        }

        lock (_vulkanImageLayoutLock)
        {
            if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)commandBuffer.Handle),
                    out VulkanRecordedImageLayoutState? recorded))
            {
                failure = new VulkanImageEntryStateMismatch(
                    EVulkanPrimaryEntryStateMismatch.MissingCommandBufferState,
                    0,
                    0,
                    0,
                    ImageAspectFlags.None,
                    VulkanImageAccessState.Undefined,
                    VulkanImageAccessState.Undefined);
                return false;
            }

            if (!recorded.EntryStateIncomplete)
                return true;

            failure = recorded.EntryStateFailure.RequiresRecording
                ? recorded.EntryStateFailure
                : new VulkanImageEntryStateMismatch(
                    EVulkanPrimaryEntryStateMismatch.IncompleteSnapshot,
                    0,
                    0,
                    0,
                    ImageAspectFlags.None,
                    VulkanImageAccessState.Undefined,
                    VulkanImageAccessState.Undefined);
            return false;
        }
    }

    /// <summary>
    /// Finds the first missing, incomplete, or incompatible image entry state for
    /// a recorded command buffer.
    /// </summary>
    private bool TryGetRecordedImageEntryStateMismatch(
        CommandBuffer commandBuffer,
        out VulkanImageEntryStateMismatch mismatch,
        bool includeIncompleteState = true)
    {
        mismatch = default;
        if (commandBuffer.Handle == 0)
        {
            mismatch = new VulkanImageEntryStateMismatch(
                EVulkanPrimaryEntryStateMismatch.MissingCommandBufferState,
                0,
                0,
                0,
                ImageAspectFlags.None,
                VulkanImageAccessState.Undefined,
                VulkanImageAccessState.Undefined);
            return true;
        }

        lock (_vulkanImageLayoutLock)
        {
            if (!_recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)commandBuffer.Handle),
                    out VulkanRecordedImageLayoutState? recorded))
            {
                mismatch = new VulkanImageEntryStateMismatch(
                    EVulkanPrimaryEntryStateMismatch.MissingCommandBufferState,
                    0,
                    0,
                    0,
                    ImageAspectFlags.None,
                    VulkanImageAccessState.Undefined,
                    VulkanImageAccessState.Undefined);
                return true;
            }

            if (includeIncompleteState && recorded.EntryStateIncomplete)
            {
                mismatch = recorded.EntryStateFailure.RequiresRecording
                    ? recorded.EntryStateFailure
                    : new VulkanImageEntryStateMismatch(
                        EVulkanPrimaryEntryStateMismatch.IncompleteSnapshot,
                        0,
                        0,
                        0,
                        ImageAspectFlags.None,
                        VulkanImageAccessState.Undefined,
                        VulkanImageAccessState.Undefined);
                return true;
            }

            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in recorded.EntrySubresources)
            {
                if (!_trackedImageSubresourceStates.TryGetValue(
                        pair.Key,
                        out VulkanImageSubresourceState? submittedState))
                {
                    mismatch = new VulkanImageEntryStateMismatch(
                        EVulkanPrimaryEntryStateMismatch.MissingSubmittedState,
                        pair.Key.ImageHandle,
                        pair.Key.MipLevel,
                        pair.Key.ArrayLayer,
                        pair.Key.Aspect,
                        pair.Value,
                        VulkanImageAccessState.Undefined);
                    return true;
                }

                EVulkanPrimaryEntryStateMismatch kind =
                    VulkanImageEntryStateContract.Compare(
                        submittedState.Submitted,
                        pair.Value);
                if (kind == EVulkanPrimaryEntryStateMismatch.None)
                    continue;

                mismatch = new VulkanImageEntryStateMismatch(
                    kind,
                    pair.Key.ImageHandle,
                    pair.Key.MipLevel,
                    pair.Key.ArrayLayer,
                    pair.Key.Aspect,
                    pair.Value,
                    submittedState.Submitted);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Publishes an actionable primary-command entry-state mismatch to renderer
    /// telemetry.
    /// </summary>
    private static void RecordPrimaryImageEntryStateMismatch(
        in VulkanImageEntryStateMismatch mismatch)
    {
        if (!mismatch.RequiresRecording)
            return;

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPrimaryEntryStateMismatch(
            mismatch.Kind,
            mismatch.ImageHandle,
            mismatch.MipLevel,
            mismatch.ArrayLayer,
            (int)mismatch.Aspect,
            (int)mismatch.Expected.Layout,
            (ulong)mismatch.Expected.StageMask,
            (ulong)mismatch.Expected.AccessMask,
            (int)mismatch.Expected.ExpectedDescriptorLayout,
            mismatch.Expected.QueueFamilyIndex,
            mismatch.Expected.ResourceGeneration,
            (int)mismatch.Actual.Layout,
            (ulong)mismatch.Actual.StageMask,
            (ulong)mismatch.Actual.AccessMask,
            (int)mismatch.Actual.ExpectedDescriptorLayout,
            mismatch.Actual.QueueFamilyIndex,
            mismatch.Actual.ResourceGeneration);
    }

    /// <summary>
    /// Determines whether an actual image state satisfies a recorded entry-state
    /// contract.
    /// </summary>
    private static bool AreRecordedImageEntryStatesCompatible(
        in VulkanImageAccessState actual,
        in VulkanImageAccessState expected)
        => VulkanImageEntryStateContract.Compare(actual, expected) ==
           EVulkanPrimaryEntryStateMismatch.None;

    /// <summary>
    /// Computes a stable signature for physical-image allocation state plus the
    /// selected command buffer's recorded overlay or global submitted state.
    /// </summary>
    private ulong ComputeImageLayoutStateSignature(
        CommandBuffer commandBuffer = default)
    {
        ResourcePlannerRuntimeState plannerState = CaptureResourcePlannerRuntimeState();
        VulkanResourceAllocator allocator = plannerState.ResourceAllocator;
        FrameOpSignatureHasher hash = new();
        hash.Add(RuntimeHelpers.GetHashCode(allocator));
        hash.Add(plannerState.ResourcePlannerRevision);

        int physicalGroupCount = 0;
        foreach (VulkanPhysicalImageGroup group in allocator.EnumeratePhysicalGroups())
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

    /// <summary>
    /// Appends one tracked subresource and its compatibility-relevant access state
    /// to a frame-operation signature hash.
    /// </summary>
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
        hash.Add((byte)state.ExternalOwnership);
    }
}
