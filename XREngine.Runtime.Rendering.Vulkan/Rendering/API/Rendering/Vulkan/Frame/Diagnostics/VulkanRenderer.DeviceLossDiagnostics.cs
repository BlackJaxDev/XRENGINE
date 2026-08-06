using System;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int VulkanCrashBreadcrumbCapacity = 64;
    private const int VulkanDeviceAddressRangeCapacity = 512;
    private const int VulkanDeviceAddressBindingEventCapacity = 128;
    private const int VulkanNvCheckpointMarkerCapacity = 256;
    private const int VulkanCommandDiagnosticMarkerCapacity = 512;
    private const int VulkanImageLayoutTransitionCapacity = 128;


    /// <summary>
    /// Gets the immutable record published by the thread that won the device-loss
    /// transition. Later fallout must not overwrite the original failure evidence.
    /// </summary>
    private VulkanDeviceLossRecord? FirstDeviceLossRecord
        => Volatile.Read(ref _frameTelemetry._firstDeviceLossRecord);

    /// <summary>
    /// Creates a diagnostic context for a swapchain submission.
    /// </summary>
    /// <param name="submissionKind">The kind of submission being created.</param>
    /// <param name="imageIndex">The index of the swapchain image being submitted.</param>
    /// <param name="frameNumber">The number of the frame being submitted.</param>
    /// <param name="waitTimelineValue">The timeline value to wait on before executing the submission.</param>
    /// <param name="signalTimelineValue">The timeline value to signal after executing the submission.</param>
    /// <param name="commandBufferDirtyGeneration">The generation of the command buffer being submitted.</param>
    /// <param name="frameOpsSignature">The signature of the frame operations associated with this submission.</param>
    /// <param name="plannerRevision">The revision of the planner used for this submission.</param>
    /// <param name="frameOpContextId">The context ID of the frame operation.</param>
    /// <param name="resourceGeneration">The generation of the resources used in this submission.</param>
    /// <param name="descriptorGeneration">The generation of the descriptors used in this submission.</param>
    /// <returns>The created Vulkan submission diagnostic context.</returns>
    private VulkanSubmissionDiagnosticContext CreateSwapchainSubmissionDiagnosticContext(
        string submissionKind,
        uint imageIndex,
        ulong frameNumber,
        int desktopFrameSlot,
        ulong waitTimelineValue,
        ulong signalTimelineValue,
        long commandBufferDirtyGeneration,
        ulong frameOpsSignature,
        ulong plannerRevision,
        ulong frameOpContextId,
        ulong resourceGeneration,
        ulong descriptorGeneration)
        => new()
        {
            SubmissionKind = submissionKind,
            FrameOpKind = "MainViewport",
            OutputTargetName = "Swapchain",
            OutputWidth = OutputRuntime.Desktop.Extent.Width,
            OutputHeight = OutputRuntime.Desktop.Extent.Height,
            InternalWidth = OutputRuntime.Desktop.Extent.Width,
            InternalHeight = OutputRuntime.Desktop.Extent.Height,
            FrameId = frameNumber,
            FrameSlot = desktopFrameSlot,
            SwapchainImageIndex = imageIndex,
            CommandBufferDirtyGeneration = commandBufferDirtyGeneration,
            FrameOpsSignature = frameOpsSignature,
            PlannerRevision = plannerRevision,
            FrameOpContextId = frameOpContextId,
            ResourceGeneration = resourceGeneration,
            DescriptorGeneration = descriptorGeneration,
            WaitTimelineValue = waitTimelineValue,
            SignalTimelineValue = signalTimelineValue,
        };

    private VulkanSubmissionDiagnosticContext
        CreateFrameTargetSubmissionDiagnosticContext(
            string submissionKind,
            string outputTargetName,
            in VulkanFrameTargetLease lease,
            ulong frameNumber,
            ulong signalTimelineValue)
        => new()
        {
            SubmissionKind = submissionKind,
            FrameOpKind = "MainViewport",
            OutputTargetName = outputTargetName,
            OutputWidth = lease.Target.Extent.Width,
            OutputHeight = lease.Target.Extent.Height,
            InternalWidth = lease.Target.Extent.Width,
            InternalHeight = lease.Target.Extent.Height,
            FrameId = frameNumber,
            FrameSlot = unchecked((int)Math.Min(
                lease.Target.FrameSlotIndex,
                int.MaxValue)),
            SwapchainImageIndex = lease.ImageIndex,
            CommandBufferDirtyGeneration =
                SnapshotCommandBufferDirtyGeneration(),
            ResourceGeneration = lease.Target.TargetGeneration,
            WaitTimelineValue = 0,
            SignalTimelineValue = signalTimelineValue,
        };

    /// <summary>
    /// Creates a Vulkan submission diagnostic context for an OpenXR submission.
    /// </summary>
    /// <param name="submissionKind">The kind of submission (e.g., "OpenXR").</param>
    /// <param name="frameOpKind">The kind of frame operation.</param>
    /// <param name="openXrViewIndex">The index of the OpenXR view.</param>
    /// <param name="openXrImageIndex">The index of the OpenXR image.</param>
    /// <param name="frameDataSlotIndex">The index of the frame data slot.</param>
    /// <param name="extent">The extent (width and height) of the output.</param>
    /// <param name="frameOpsSignature">The signature of the frame operations.</param>
    /// <param name="plannerRevision">The revision of the planner used for this submission.</param>
    /// <param name="frameOpContextId">The context ID of the frame operation.</param>
    /// <param name="resourceGeneration">The generation of the resources used in this submission.</param>
    /// <param name="descriptorGeneration">The generation of the descriptors used in this submission.</param>
    /// <returns>The created Vulkan submission diagnostic context.</returns>
    private VulkanSubmissionDiagnosticContext CreateOpenXrSubmissionDiagnosticContext(
        string submissionKind,
        string frameOpKind,
        uint openXrViewIndex,
        uint openXrImageIndex,
        uint frameDataSlotIndex,
        Extent2D extent,
        ulong frameOpsSignature,
        ulong plannerRevision,
        ulong frameOpContextId,
        ulong resourceGeneration,
        ulong descriptorGeneration)
        => new()
        {
            SubmissionKind = submissionKind,
            FrameOpKind = frameOpKind,
            OutputTargetName = ResolveOpenXrExternalSwapchainTargetName(openXrViewIndex),
            OutputWidth = extent.Width,
            OutputHeight = extent.Height,
            InternalWidth = extent.Width,
            InternalHeight = extent.Height,
            FrameId = AcceptedDesktopFrameAttemptCount,
            FrameSlot = unchecked((int)Math.Min(frameDataSlotIndex, int.MaxValue)),
            SwapchainImageIndex = openXrImageIndex,
            CommandBufferDirtyGeneration = SnapshotCommandBufferDirtyGeneration(),
            FrameOpsSignature = frameOpsSignature,
            PlannerRevision = plannerRevision,
            FrameOpContextId = frameOpContextId,
            ResourceGeneration = resourceGeneration,
            DescriptorGeneration = descriptorGeneration,
        };

    /// <summary>
    /// Creates a Vulkan submission diagnostic context for an OpenXR batch submission.
    /// </summary>
    /// <param name="submissionKind">The kind of submission (e.g., "OpenXR").</param>
    /// <param name="frameOpKind">The kind of frame operation.</param>
    /// <param name="firstRecorded">The first recorded OpenXR eye command buffer.</param>
    /// <param name="secondRecorded">The second recorded OpenXR eye command buffer.</param>
    /// <param name="extent">The extent (width and height) of the output.</param>
    /// <returns>The created Vulkan submission diagnostic context.</returns>
    private VulkanSubmissionDiagnosticContext CreateOpenXrBatchSubmissionDiagnosticContext(
        string submissionKind,
        string frameOpKind,
        in OpenXrRecordedEyeCommandBuffer firstRecorded,
        in OpenXrRecordedEyeCommandBuffer secondRecorded,
        Extent2D extent)
        => new()
        {
            SubmissionKind = submissionKind,
            FrameOpKind = frameOpKind,
            OutputTargetName = "OpenXRBatch",
            OutputWidth = extent.Width,
            OutputHeight = extent.Height,
            InternalWidth = extent.Width,
            InternalHeight = extent.Height,
            FrameId = AcceptedDesktopFrameAttemptCount,
            FrameSlot = unchecked((int)Math.Min(firstRecorded.FrameDataSlotIndex, int.MaxValue)),
            SwapchainImageIndex = firstRecorded.OpenXrImageIndex,
            CommandBufferDirtyGeneration = SnapshotCommandBufferDirtyGeneration(),
            FrameOpsSignature = firstRecorded.FrameOpsSignature,
            PlannerRevision = firstRecorded.PlannerRevision,
            FrameOpContextId = firstRecorded.FrameOpContextId,
            ResourceGeneration = firstRecorded.ResourceGeneration,
            DescriptorGeneration = firstRecorded.DescriptorGeneration,
        };

    /// <summary>
    /// Creates a Vulkan submission diagnostic context for an OpenXR publish batch submission.
    /// </summary>
    /// <param name="submissionKind">The kind of submission (e.g., "OpenXR").</param>
    /// <param name="frameOpKind">The kind of frame operation.</param>
    /// <param name="recorded">The recorded OpenXR eye command buffer.</param>
    /// <param name="extent">The extent (width and height) of the output.</param>
    /// <param name="outputTargetName">The name of the output target.</param>
    /// <returns>The created Vulkan submission diagnostic context.</returns>
    private VulkanSubmissionDiagnosticContext CreateOpenXrPublishBatchSubmissionDiagnosticContext(
        string submissionKind,
        string frameOpKind,
        in OpenXrRecordedEyeCommandBuffer recorded,
        Extent2D extent,
        string outputTargetName)
        => new()
        {
            SubmissionKind = submissionKind,
            FrameOpKind = frameOpKind,
            OutputTargetName = outputTargetName,
            OutputWidth = extent.Width,
            OutputHeight = extent.Height,
            InternalWidth = extent.Width,
            InternalHeight = extent.Height,
            FrameId = AcceptedDesktopFrameAttemptCount,
            FrameSlot = unchecked((int)Math.Min(recorded.FrameDataSlotIndex, int.MaxValue)),
            SwapchainImageIndex = recorded.OpenXrImageIndex,
            CommandBufferDirtyGeneration = SnapshotCommandBufferDirtyGeneration(),
            FrameOpsSignature = recorded.FrameOpsSignature,
            PlannerRevision = recorded.PlannerRevision,
            FrameOpContextId = recorded.FrameOpContextId,
            ResourceGeneration = recorded.ResourceGeneration,
            DescriptorGeneration = recorded.DescriptorGeneration,
        };

    /// <summary>
    /// Completes the Vulkan submission diagnostic context after a submission has been made.
    /// </summary>
    /// <param name="queue">The Vulkan queue used for the submission.</param>
    /// <param name="submitInfo">The Vulkan submit info structure.</param>
    /// <param name="fence">The Vulkan fence associated with the submission.</param>
    /// <param name="context">The Vulkan submission diagnostic context to complete.</param>
    /// <param name="caller">The caller information for diagnostic purposes.</param>
    /// <returns>The completed Vulkan submission diagnostic context.</returns>
    private VulkanSubmissionDiagnosticContext CompleteSubmissionDiagnosticContext(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        VulkanSubmissionDiagnosticContext context,
        string? caller)
    {
        TimelineSemaphoreSubmitInfo* timelineInfo = FindTimelineSemaphoreSubmitInfo(submitInfo.PNext);
        ulong waitTimelineValue = context.WaitTimelineValue;
        ulong signalTimelineValue = context.SignalTimelineValue;
        if (timelineInfo is not null)
        {
            if (waitTimelineValue == 0 &&
                timelineInfo->WaitSemaphoreValueCount > 0 &&
                timelineInfo->PWaitSemaphoreValues is not null)
            {
                waitTimelineValue = timelineInfo->PWaitSemaphoreValues[0];
            }

            if (signalTimelineValue == 0 &&
                timelineInfo->SignalSemaphoreValueCount > 0 &&
                timelineInfo->PSignalSemaphoreValues is not null)
            {
                signalTimelineValue = timelineInfo->PSignalSemaphoreValues[0];
            }
        }

        ulong firstCommandBufferHandle = 0;
        if (submitInfo.CommandBufferCount > 0 && submitInfo.PCommandBuffers is not null)
            firstCommandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[0].Handle);

        uint outputWidth = context.OutputWidth != 0 ? context.OutputWidth : OutputRuntime.Desktop.Extent.Width;
        uint outputHeight = context.OutputHeight != 0 ? context.OutputHeight : OutputRuntime.Desktop.Extent.Height;
        uint internalWidth = context.InternalWidth != 0 ? context.InternalWidth : outputWidth;
        uint internalHeight = context.InternalHeight != 0 ? context.InternalHeight : outputHeight;
        VulkanCommandDiagnosticMarker commandMarker = SnapshotVulkanCommandDiagnosticMarker(ref submitInfo);
        DesktopFrameActivitySnapshot desktopFrame = CaptureDesktopFrameDiagnosticState();

        return context with
        {
            SubmissionSerial = unchecked((ulong)Interlocked.Increment(ref _frameTelemetry._vulkanSubmissionSerial)),
            SubmissionKind = string.IsNullOrWhiteSpace(context.SubmissionKind)
                ? caller ?? "<unknown>"
                : context.SubmissionKind,
            FrameOpKind = string.IsNullOrWhiteSpace(context.FrameOpKind)
                ? "<unknown>"
                : context.FrameOpKind,
            OutputTargetName = string.IsNullOrWhiteSpace(context.OutputTargetName)
                ? "<unknown>"
                : context.OutputTargetName,
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
            InternalWidth = internalWidth,
            InternalHeight = internalHeight,
            FrameId = context.FrameId != 0 ? context.FrameId : desktopFrame.FrameNumber,
            FrameSlot = context.FrameSlot ?? desktopFrame.FrameSlot,
            CommandBufferDirtyGeneration = context.CommandBufferDirtyGeneration != 0
                ? context.CommandBufferDirtyGeneration
                : SnapshotCommandBufferDirtyGeneration(),
            WaitTimelineValue = waitTimelineValue,
            SignalTimelineValue = signalTimelineValue,
            QueueKind = ResolveVulkanQueueKind(queue),
            Caller = caller ?? "<unknown>",
            WaitSemaphoreCount = submitInfo.WaitSemaphoreCount,
            SignalSemaphoreCount = submitInfo.SignalSemaphoreCount,
            CommandBufferCount = submitInfo.CommandBufferCount,
            FirstCommandBufferHandle = firstCommandBufferHandle,
            FenceHandle = unchecked((ulong)fence.Handle),
            LastCommandMarkerSerial = commandMarker.Serial,
            LastCommandMarkerGeneration = commandMarker.CommandBufferRecordingGeneration,
            LastCommandMarkerKind = commandMarker.OpKind,
            LastCommandMarkerPassIndex = commandMarker.PassIndex,
            LastCommandMarkerBatchIndex = commandMarker.BatchIndex,
            ImageLayoutTransitionSerial = unchecked((ulong)Math.Max(Volatile.Read(ref _frameTelemetry._vulkanImageLayoutTransitionSerial), 0L)),
            DescriptorTableGeneration = unchecked((ulong)Math.Max(Volatile.Read(ref _frameTelemetry._vulkanDescriptorTableGeneration), 0L)),
            FirstFailingApi = Volatile.Read(ref _frameTelemetry._firstFailingVulkanApi),
        };
    }

    /// <summary>
    /// Resolves the kind of Vulkan queue (e.g., Graphics, Transfer, Present) based on the provided queue handle.
    /// </summary>
    /// <param name="queue">The Vulkan queue to resolve.</param>
    /// <returns>The kind of the Vulkan queue as a string.</returns>
    private string ResolveVulkanQueueKind(Queue queue)
    {
        if (queue.Handle == _deviceContext.GraphicsQueue.Handle)
            return "Graphics";
        if (queue.Handle == _deviceContext.TransferQueue.Handle)
            return "Transfer";
        if (queue.Handle == _deviceContext.PresentQueue.Handle)
            return "Present";
        return "Unknown";
    }

    /// <summary>
    /// Records the last Vulkan submission diagnostic context for later analysis, including crash breadcrumbs if enabled.
    /// </summary>
    /// <param name="context">The Vulkan submission diagnostic context to record.</param>
    private void RecordLastVulkanSubmissionDiagnosticContext(VulkanSubmissionDiagnosticContext context)
    {
        _deviceContext.RecordSubmissionDiagnostics(context);
        if (_frameTelemetry._diagnosticOptions.EnableCrashBreadcrumbs)
            RecordVulkanCrashBreadcrumb(context);
    }

    /// <summary>
    /// Snapshots the last recorded Vulkan submission diagnostic context for analysis.
    /// </summary>
    /// <returns>The last recorded Vulkan submission diagnostic context.</returns>
    private VulkanSubmissionDiagnosticContext SnapshotLastVulkanSubmissionDiagnosticContext()
        => _deviceContext.SnapshotSubmissionDiagnostics();

    /// <summary>
    /// Builds a detailed device lost reason string that includes the last Vulkan submission diagnostic context, crash breadcrumbs, image layout transitions, queue operations, validation summary, and fault diagnostics.
    /// </summary>
    /// <param name="reason">The base reason for the device loss.</param>
    /// <returns>A detailed device lost reason string including diagnostic context and breadcrumbs.</returns>
    private string BuildDeviceLostReasonWithSubmissionContext(string? reason)
    {
        ImportValidationDeviceAddressBindings();
        string baseReason = string.IsNullOrWhiteSpace(reason) ? "<unknown>" : reason.Trim();
        VulkanSubmissionDiagnosticContext context = SnapshotLastVulkanSubmissionDiagnosticContext();
        StringBuilder builder = new(baseReason);

        if (!context.IsEmpty)
            builder.Append("; ").Append(DescribeVulkanSubmissionDiagnosticContext(context));

        VulkanDeviceLossRecord? firstLoss = FirstDeviceLossRecord;
        if (firstLoss is not null)
        {
            builder.Append("; FirstLoss operation=").Append(firstLoss.Operation)
                .Append(" result=").Append(firstLoss.Result)
                .Append(" observedUtc=").Append(firstLoss.ObservedAtUtc.ToString("O"));
            if (!firstLoss.Submission.IsEmpty)
                builder.Append(" firstLossSubmission=")
                    .Append(DescribeVulkanSubmissionDiagnosticContext(firstLoss.Submission));

            VulkanResourceLifetimeSnapshot lifetime = firstLoss.ResourceLifetime;
            builder.Append(" firstLossLifetime=live/").Append(lifetime.LiveResourceCount)
                .Append(" recorded/").Append(lifetime.RecordedResourceCount)
                .Append(" submitted/").Append(lifetime.SubmittedResourceCount)
                .Append(" inFlight/").Append(lifetime.InFlightSubmissionCount)
                .Append(" pendingRetirement/").Append(lifetime.PendingRetirementCount)
                .Append(" sequences=gfx:").Append(lifetime.LastGraphicsSequence)
                .Append('/').Append(lifetime.CompletedGraphicsSequence)
                .Append(" transfer:").Append(lifetime.LastTransferSequence)
                .Append('/').Append(lifetime.CompletedTransferSequence);
        }

        string breadcrumbs = DescribeVulkanCrashBreadcrumbTail();
        if (!string.IsNullOrWhiteSpace(breadcrumbs))
            builder.Append("; ").Append(breadcrumbs);

        string imageTransitions = DescribeVulkanImageLayoutTransitionTail();
        if (!string.IsNullOrWhiteSpace(imageTransitions))
            builder.Append("; ").Append(imageTransitions);

        string queueOperations = DescribeVulkanQueueOperationTail();
        if (!string.IsNullOrWhiteSpace(queueOperations))
            builder.Append("; ").Append(queueOperations);

        builder.Append("; _deviceContext.State=").Append(_deviceContext.State)
            .Append(" FalloutErrors=").Append(_deviceContext.DeviceFaultFacility.DeviceLossFalloutCount);

        string validationSummary = DescribeVulkanValidationSummary();
        if (!string.IsNullOrWhiteSpace(validationSummary))
            builder.Append("; ").Append(validationSummary);

        string? firstFailingApi = Volatile.Read(ref _frameTelemetry._firstFailingVulkanApi);
        if (!string.IsNullOrWhiteSpace(firstFailingApi))
            builder.Append("; FirstFailingApi=").Append(firstFailingApi);

        string faultSummary = DescribeVulkanFaultDiagnosticsAfterDeviceLoss();
        if (!string.IsNullOrWhiteSpace(faultSummary))
            builder.Append("; ").Append(faultSummary);

        return builder.ToString();
    }

    /// <summary>
    /// Records a Vulkan crash breadcrumb for the given submission diagnostic context.
    /// </summary>
    /// <param name="context">The Vulkan submission diagnostic context to record as a crash breadcrumb.</param>
    /// <remarks>
    /// Crash breadcrumbs are used to capture the state of Vulkan submissions leading up to a device loss,
    /// aiding in post-mortem analysis and debugging.
    /// </remarks>
    private void RecordVulkanCrashBreadcrumb(VulkanSubmissionDiagnosticContext context)
    {
        long serial = Interlocked.Increment(ref _frameTelemetry._vulkanCrashBreadcrumbSerial);
        int index = unchecked((int)((serial - 1) % VulkanCrashBreadcrumbCapacity));
        _frameTelemetry._vulkanCrashBreadcrumbs[index] = new()
        {
            Serial = unchecked((ulong)serial),
            SubmissionKind = context.SubmissionKind,
            FrameOpKind = context.FrameOpKind,
            OutputTargetName = context.OutputTargetName,
            FrameId = context.FrameId,
            FrameSlot = context.FrameSlot,
            SwapchainImageIndex = context.SwapchainImageIndex,
            CommandBufferDirtyGeneration = context.CommandBufferDirtyGeneration,
            FrameOpsSignature = context.FrameOpsSignature,
            PlannerRevision = context.PlannerRevision,
            FrameOpContextId = context.FrameOpContextId,
            ResourceGeneration = context.ResourceGeneration,
            DescriptorGeneration = context.DescriptorGeneration,
            QueueKind = context.QueueKind,
            CommandBufferCount = context.CommandBufferCount,
            FirstCommandBufferHandle = context.FirstCommandBufferHandle,
            FenceHandle = context.FenceHandle,
            WaitTimelineValue = context.WaitTimelineValue,
            SignalTimelineValue = context.SignalTimelineValue,
            Caller = context.Caller,
            LastCommandMarkerSerial = context.LastCommandMarkerSerial,
            LastCommandMarkerGeneration = context.LastCommandMarkerGeneration,
            LastCommandMarkerKind = context.LastCommandMarkerKind,
            LastCommandMarkerPassIndex = context.LastCommandMarkerPassIndex,
            LastCommandMarkerBatchIndex = context.LastCommandMarkerBatchIndex,
            ImageLayoutTransitionSerial = context.ImageLayoutTransitionSerial,
            DescriptorTableGeneration = context.DescriptorTableGeneration,
            FirstFailingApi = context.FirstFailingApi,
        };
    }

    /// <summary>
    /// Describes the tail of the Vulkan crash breadcrumb trail, providing a summary of the most recent submissions leading up to a device loss.
    /// </summary>
    /// <param name="maxEntries">The maximum number of recent crash breadcrumbs to include in the description.</param>
    /// <returns>A string describing the tail of the Vulkan crash breadcrumb trail.</returns>
    private string DescribeVulkanCrashBreadcrumbTail(int maxEntries = 8)
    {
        if (!_frameTelemetry._diagnosticOptions.EnableCrashBreadcrumbs)
            return string.Empty;

        lock (_frameTelemetry._vulkanSubmissionDiagnosticsLock)
        {
            long latestSerial = Volatile.Read(ref _frameTelemetry._vulkanCrashBreadcrumbSerial);
            if (latestSerial <= 0)
                return string.Empty;

            int count = (int)Math.Min(latestSerial, VulkanCrashBreadcrumbCapacity);
            int emitted = 0;
            StringBuilder builder = new();
            builder.Append("BreadcrumbTail");

            for (long serial = latestSerial; serial > 0 && emitted < maxEntries && emitted < count; serial--)
            {
                int index = unchecked((int)((serial - 1) % VulkanCrashBreadcrumbCapacity));
                VulkanCrashBreadcrumb breadcrumb = _frameTelemetry._vulkanCrashBreadcrumbs[index];
                if (breadcrumb.Serial != unchecked((ulong)serial))
                    continue;

                builder
                    .Append(" [#")
                    .Append(breadcrumb.Serial)
                    .Append(' ')
                    .Append(breadcrumb.SubmissionKind ?? "<unknown>")
                    .Append(" frameOp=")
                    .Append(breadcrumb.FrameOpKind ?? "<unknown>")
                    .Append(" target=")
                    .Append(breadcrumb.OutputTargetName ?? "<unknown>")
                    .Append(" frame=")
                    .Append(breadcrumb.FrameId)
                    .Append(" slot=")
                    .Append(breadcrumb.FrameSlot?.ToString() ?? "<unknown>")
                    .Append(" image=")
                    .Append(breadcrumb.SwapchainImageIndex?.ToString() ?? "<unknown>")
                    .Append(" queue=")
                    .Append(breadcrumb.QueueKind ?? "<unknown>")
                    .Append(" cmdGen=")
                    .Append(breadcrumb.CommandBufferDirtyGeneration)
                    .Append(" frameOps=0x")
                    .Append(breadcrumb.FrameOpsSignature.ToString("X16"))
                    .Append(" planner=0x")
                    .Append(breadcrumb.PlannerRevision.ToString("X16"))
                    .Append(" contextId=")
                    .Append(breadcrumb.FrameOpContextId)
                    .Append(" resourceGen=")
                    .Append(breadcrumb.ResourceGeneration)
                    .Append(" descriptorGen=")
                    .Append(breadcrumb.DescriptorGeneration)
                    .Append(" cmds=")
                    .Append(breadcrumb.CommandBufferCount)
                    .Append(" firstCmd=0x")
                    .Append(breadcrumb.FirstCommandBufferHandle.ToString("X"))
                    .Append(" fence=0x")
                    .Append(breadcrumb.FenceHandle.ToString("X"))
                    .Append(" timeline=(")
                    .Append(breadcrumb.WaitTimelineValue)
                    .Append("->")
                    .Append(breadcrumb.SignalTimelineValue)
                    .Append(") opMarker=#")
                    .Append(breadcrumb.LastCommandMarkerSerial)
                    .Append("/recordGen=")
                    .Append(breadcrumb.LastCommandMarkerGeneration)
                    .Append(':')
                    .Append(breadcrumb.LastCommandMarkerKind ?? "<unknown>")
                    .Append(" pass=")
                    .Append(breadcrumb.LastCommandMarkerPassIndex)
                    .Append(" batch=")
                    .Append(breadcrumb.LastCommandMarkerBatchIndex)
                    .Append(" layoutSerial=")
                    .Append(breadcrumb.ImageLayoutTransitionSerial)
                    .Append(" descriptorGen=")
                    .Append(breadcrumb.DescriptorTableGeneration)
                    .Append(" firstApi=")
                    .Append(breadcrumb.FirstFailingApi ?? "<none>")
                    .Append(" caller=")
                    .Append(breadcrumb.Caller ?? "<unknown>")
                    .Append(']');
                emitted++;
            }

            return emitted == 0 ? string.Empty : builder.ToString();
        }
    }

    /// <summary>
    /// Describes the most recent Vulkan image layout transitions, up to the specified maximum number of entries.
    /// </summary>
    /// <param name="maxEntries">The maximum number of recent image layout transitions to include in the description.</param>
    /// <returns>A string describing the most recent Vulkan image layout transitions.</returns>
    private string DescribeVulkanImageLayoutTransitionTail(int maxEntries = 8)
    {
        if (!_frameTelemetry._diagnosticOptions.EnableCrashBreadcrumbs)
            return string.Empty;

        lock (_frameTelemetry._vulkanSubmissionDiagnosticsLock)
        {
            long latestSerial = Volatile.Read(ref _frameTelemetry._vulkanImageLayoutTransitionSerial);
            if (latestSerial <= 0)
                return string.Empty;

            int available = (int)Math.Min(latestSerial, VulkanImageLayoutTransitionCapacity);
            int emitted = 0;
            StringBuilder builder = new("ImageLayoutTail");
            for (long serial = latestSerial; serial > 0 && emitted < maxEntries && latestSerial - serial < available; serial--)
            {
                int index = unchecked((int)((serial - 1) % VulkanImageLayoutTransitionCapacity));
                VulkanImageLayoutTransitionBreadcrumb transition = _frameTelemetry._vulkanImageLayoutTransitions[index];
                if (transition.Serial != unchecked((ulong)serial))
                    continue;

                builder
                    .Append(" [#").Append(transition.Serial)
                    .Append(" cmd=0x").Append(transition.CommandBufferHandle.ToString("X"))
                    .Append(" image=0x").Append(transition.ImageHandle.ToString("X"))
                    .Append(" aspect=").Append(transition.AspectMask)
                    .Append(" mip=").Append(transition.BaseMipLevel).Append('+').Append(transition.LevelCount)
                    .Append(" layer=").Append(transition.BaseArrayLayer).Append('+').Append(transition.LayerCount)
                    .Append(' ').Append(transition.OldLayout).Append("->").Append(transition.NewLayout)
                    .Append(" queue=").Append(transition.SourceQueueFamily).Append("->").Append(transition.DestinationQueueFamily)
                    .Append(" caller=").Append(transition.Caller ?? "<unknown>")
                    .Append(']');
                emitted++;
            }

            return emitted == 0 ? string.Empty : builder.ToString();
        }
    }

    /// <summary>
    /// Describes Vulkan fault diagnostics after a device loss, including device fault summary, NV checkpoint summary, and device address binding summary.
    /// </summary>
    /// <returns>A string containing the Vulkan fault diagnostics after device loss.</returns>
    private string DescribeVulkanFaultDiagnosticsAfterDeviceLoss()
    {
        StringBuilder builder = new();
        AppendDeviceFaultSummary(builder);
        AppendNvCheckpointSummary(builder);
        AppendDeviceAddressBindingSummary(builder);
        return builder.ToString();
    }

    /// <summary>
    /// Appends a summary of the device address bindings to the specified StringBuilder.
    /// </summary>
    /// <param name="builder">The StringBuilder to append the summary to.</param>
    private void AppendDeviceFaultSummary(StringBuilder builder)
        => AppendDeviceFaultSummaryDetailed(builder);

    /// <summary>
    /// Appends a summary of the NV checkpoint to the specified StringBuilder.
    /// </summary>
    /// <param name="builder">The StringBuilder to append the summary to.</param>
    private void AppendNvCheckpointSummary(StringBuilder builder)
        => AppendNvCheckpointSummaryDetailed(builder);

    /// <summary>
    /// Appends a fault section with the specified value to the StringBuilder.
    /// </summary>
    /// <param name="builder">The StringBuilder to append the fault section to.</param>
    /// <param name="value">The value of the fault section to append.</param>
    private static void AppendFaultSection(StringBuilder builder, string value)
    {
        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(value);
    }

    /// <summary>
    /// Describes the Vulkan submission diagnostic context as a string.
    /// </summary>
    /// <param name="context">The Vulkan submission diagnostic context to describe.</param>
    /// <returns>A string representation of the Vulkan submission diagnostic context.</returns>
    private static string DescribeVulkanSubmissionDiagnosticContext(VulkanSubmissionDiagnosticContext context)
    {
        string imageIndex = context.SwapchainImageIndex.HasValue
            ? context.SwapchainImageIndex.Value.ToString()
            : "<unknown>";
        string frameSlot = context.FrameSlot.HasValue
            ? context.FrameSlot.Value.ToString()
            : "<unknown>";

        return
            $"LastSubmit kind={context.SubmissionKind ?? "<unknown>"} " +
            $"frameOp={context.FrameOpKind ?? "<unknown>"} " +
            $"target={context.OutputTargetName ?? "<unknown>"} " +
            $"extent={context.OutputWidth}x{context.OutputHeight} " +
            $"internal={context.InternalWidth}x{context.InternalHeight} " +
            $"frame={context.FrameId} slot={frameSlot} image={imageIndex} " +
            $"cmdGen={context.CommandBufferDirtyGeneration} " +
            $"frameOps=0x{context.FrameOpsSignature:X16} " +
            $"planner=0x{context.PlannerRevision:X16} " +
            $"contextId={context.FrameOpContextId} resourceGen={context.ResourceGeneration} descriptorGen={context.DescriptorGeneration} " +
            $"queue={context.QueueKind ?? "<unknown>"} " +
            $"waits={context.WaitSemaphoreCount} signals={context.SignalSemaphoreCount} " +
            $"cmds={context.CommandBufferCount} firstCmd=0x{context.FirstCommandBufferHandle:X} " +
            $"fence=0x{context.FenceHandle:X} " +
            $"timeline(wait={context.WaitTimelineValue},signal={context.SignalTimelineValue}) " +
            $"opMarker=#{context.LastCommandMarkerSerial}/recordGen={context.LastCommandMarkerGeneration}:{context.LastCommandMarkerKind ?? "<unknown>"} " +
            $"opPass={context.LastCommandMarkerPassIndex} opBatch={context.LastCommandMarkerBatchIndex} " +
            $"layoutSerial={context.ImageLayoutTransitionSerial} descriptorGen={context.DescriptorTableGeneration} " +
            $"firstApi={context.FirstFailingApi ?? "<none>"} " +
            $"caller={context.Caller ?? "<unknown>"}";
    }
}
