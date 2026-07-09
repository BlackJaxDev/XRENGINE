using System;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int VulkanCrashBreadcrumbCapacity = 64;
    private const int VulkanDeviceAddressRangeCapacity = 512;
    private const int VulkanDeviceAddressBindingEventCapacity = 128;
    private const int VulkanNvCheckpointMarkerCapacity = 256;

    private readonly object _deviceLostTransitionLock = new();
    private readonly object _vulkanSubmissionDiagnosticsLock = new();
    private readonly VulkanCrashBreadcrumb[] _vulkanCrashBreadcrumbs = new VulkanCrashBreadcrumb[VulkanCrashBreadcrumbCapacity];
    private VulkanSubmissionDiagnosticContext _lastVulkanSubmissionDiagnosticContext;
    private long _vulkanCrashBreadcrumbSerial;
    private long _vulkanCommandDiagnosticMarkerSerial;
    private VulkanCommandDiagnosticMarker _lastVulkanCommandDiagnosticMarker;
    private long _vulkanImageLayoutTransitionSerial;
    private long _vulkanDescriptorTableGeneration;
    private string? _firstFailingVulkanApi;
    private readonly object _vulkanDeviceAddressDiagnosticsLock = new();
    private readonly VulkanDeviceAddressRange[] _vulkanDeviceAddressRanges = new VulkanDeviceAddressRange[VulkanDeviceAddressRangeCapacity];
    private readonly VulkanDeviceAddressBindingEvent[] _vulkanDeviceAddressBindingEvents = new VulkanDeviceAddressBindingEvent[VulkanDeviceAddressBindingEventCapacity];
    private long _vulkanDeviceAddressBindingEventSerial;
    private readonly object _vulkanNvCheckpointMarkerLock = new();
    private readonly ulong[] _vulkanNvCheckpointMarkerPayloads = new ulong[VulkanNvCheckpointMarkerCapacity];
    private readonly VulkanNvCheckpointMarker[] _vulkanNvCheckpointMarkers = new VulkanNvCheckpointMarker[VulkanNvCheckpointMarkerCapacity];
    private System.Runtime.InteropServices.GCHandle _vulkanNvCheckpointMarkerPayloadsPin;

    private readonly record struct VulkanSubmissionDiagnosticContext
    {
        public string? SubmissionKind { get; init; }
        public string? FrameOpKind { get; init; }
        public string? OutputTargetName { get; init; }
        public uint OutputWidth { get; init; }
        public uint OutputHeight { get; init; }
        public uint InternalWidth { get; init; }
        public uint InternalHeight { get; init; }
        public ulong FrameId { get; init; }
        public int? FrameSlot { get; init; }
        public uint? SwapchainImageIndex { get; init; }
        public long CommandBufferDirtyGeneration { get; init; }
        public ulong FrameOpsSignature { get; init; }
        public ulong PlannerRevision { get; init; }
        public ulong WaitTimelineValue { get; init; }
        public ulong SignalTimelineValue { get; init; }
        public string? QueueKind { get; init; }
        public string? Caller { get; init; }
        public uint WaitSemaphoreCount { get; init; }
        public uint SignalSemaphoreCount { get; init; }
        public uint CommandBufferCount { get; init; }
        public ulong FirstCommandBufferHandle { get; init; }
        public ulong FenceHandle { get; init; }
        public ulong LastCommandMarkerSerial { get; init; }
        public string? LastCommandMarkerKind { get; init; }
        public int LastCommandMarkerPassIndex { get; init; }
        public int LastCommandMarkerBatchIndex { get; init; }
        public ulong ImageLayoutTransitionSerial { get; init; }
        public ulong DescriptorTableGeneration { get; init; }
        public string? FirstFailingApi { get; init; }

        public bool IsEmpty =>
            SubmissionKind is null &&
            FrameOpKind is null &&
            OutputTargetName is null &&
            CommandBufferCount == 0 &&
            FirstCommandBufferHandle == 0 &&
            FenceHandle == 0;
    }

    private readonly record struct VulkanCrashBreadcrumb
    {
        public ulong Serial { get; init; }
        public string? SubmissionKind { get; init; }
        public string? FrameOpKind { get; init; }
        public string? OutputTargetName { get; init; }
        public ulong FrameId { get; init; }
        public int? FrameSlot { get; init; }
        public uint? SwapchainImageIndex { get; init; }
        public long CommandBufferDirtyGeneration { get; init; }
        public ulong FrameOpsSignature { get; init; }
        public ulong PlannerRevision { get; init; }
        public string? QueueKind { get; init; }
        public uint CommandBufferCount { get; init; }
        public ulong FirstCommandBufferHandle { get; init; }
        public ulong FenceHandle { get; init; }
        public ulong WaitTimelineValue { get; init; }
        public ulong SignalTimelineValue { get; init; }
        public string? Caller { get; init; }
        public ulong LastCommandMarkerSerial { get; init; }
        public string? LastCommandMarkerKind { get; init; }
        public int LastCommandMarkerPassIndex { get; init; }
        public int LastCommandMarkerBatchIndex { get; init; }
        public ulong ImageLayoutTransitionSerial { get; init; }
        public ulong DescriptorTableGeneration { get; init; }
        public string? FirstFailingApi { get; init; }
    }

    private readonly record struct VulkanCommandDiagnosticMarker
    {
        public ulong Serial { get; init; }
        public string? OpKind { get; init; }
        public string? OutputTargetName { get; init; }
        public int PassIndex { get; init; }
        public int BatchIndex { get; init; }
        public int PipelineIdentity { get; init; }
        public int ViewportIdentity { get; init; }
        public ulong CommandBufferHandle { get; init; }
        public bool IsEmpty => Serial == 0;
    }

    private readonly record struct VulkanDeviceAddressRange(
        Buffer Buffer,
        ulong BaseAddress,
        ulong Size,
        string? Label,
        bool Active);

    private readonly record struct VulkanDeviceAddressBindingEvent(
        ulong Serial,
        ulong BaseAddress,
        ulong Size,
        DeviceAddressBindingTypeEXT BindingType,
        DeviceAddressBindingFlagsEXT Flags,
        string? CorrelatedObject);

    private readonly record struct VulkanNvCheckpointMarker
    {
        public ulong Serial { get; init; }
        public string? OpKind { get; init; }
        public string? OutputTargetName { get; init; }
        public int PassIndex { get; init; }
        public int BatchIndex { get; init; }
        public int PipelineIdentity { get; init; }
        public int ViewportIdentity { get; init; }
        public ulong CommandBufferHandle { get; init; }
    }

    private VulkanSubmissionDiagnosticContext CreateSwapchainSubmissionDiagnosticContext(
        string submissionKind,
        uint imageIndex,
        ulong frameNumber,
        ulong waitTimelineValue,
        ulong signalTimelineValue,
        long commandBufferDirtyGeneration,
        ulong frameOpsSignature)
        => new()
        {
            SubmissionKind = submissionKind,
            FrameOpKind = "MainViewport",
            OutputTargetName = "Swapchain",
            OutputWidth = swapChainExtent.Width,
            OutputHeight = swapChainExtent.Height,
            InternalWidth = swapChainExtent.Width,
            InternalHeight = swapChainExtent.Height,
            FrameId = frameNumber,
            FrameSlot = currentFrame,
            SwapchainImageIndex = imageIndex,
            CommandBufferDirtyGeneration = commandBufferDirtyGeneration,
            FrameOpsSignature = frameOpsSignature,
            WaitTimelineValue = waitTimelineValue,
            SignalTimelineValue = signalTimelineValue,
        };

    private VulkanSubmissionDiagnosticContext CreateOpenXrSubmissionDiagnosticContext(
        string submissionKind,
        string frameOpKind,
        uint openXrViewIndex,
        uint openXrImageIndex,
        uint frameDataSlotIndex,
        Extent2D extent)
        => new()
        {
            SubmissionKind = submissionKind,
            FrameOpKind = frameOpKind,
            OutputTargetName = ResolveOpenXrExternalSwapchainTargetName(openXrViewIndex),
            OutputWidth = extent.Width,
            OutputHeight = extent.Height,
            InternalWidth = extent.Width,
            InternalHeight = extent.Height,
            FrameId = _vkDebugFrameCounter,
            FrameSlot = unchecked((int)Math.Min(frameDataSlotIndex, int.MaxValue)),
            SwapchainImageIndex = openXrImageIndex,
            CommandBufferDirtyGeneration = SnapshotCommandBufferDirtyGeneration(),
        };

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
            FrameId = _vkDebugFrameCounter,
            FrameSlot = unchecked((int)Math.Min(firstRecorded.FrameDataSlotIndex, int.MaxValue)),
            SwapchainImageIndex = firstRecorded.OpenXrImageIndex,
            CommandBufferDirtyGeneration = SnapshotCommandBufferDirtyGeneration(),
        };

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
            FrameId = _vkDebugFrameCounter,
            FrameSlot = unchecked((int)Math.Min(recorded.FrameDataSlotIndex, int.MaxValue)),
            SwapchainImageIndex = recorded.OpenXrImageIndex,
            CommandBufferDirtyGeneration = SnapshotCommandBufferDirtyGeneration(),
        };

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

        uint outputWidth = context.OutputWidth != 0 ? context.OutputWidth : swapChainExtent.Width;
        uint outputHeight = context.OutputHeight != 0 ? context.OutputHeight : swapChainExtent.Height;
        uint internalWidth = context.InternalWidth != 0 ? context.InternalWidth : outputWidth;
        uint internalHeight = context.InternalHeight != 0 ? context.InternalHeight : outputHeight;
        VulkanCommandDiagnosticMarker commandMarker = SnapshotLastVulkanCommandDiagnosticMarker();

        return context with
        {
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
            FrameId = context.FrameId != 0 ? context.FrameId : _vkDebugFrameCounter,
            FrameSlot = context.FrameSlot ?? currentFrame,
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
            LastCommandMarkerKind = commandMarker.OpKind,
            LastCommandMarkerPassIndex = commandMarker.PassIndex,
            LastCommandMarkerBatchIndex = commandMarker.BatchIndex,
            ImageLayoutTransitionSerial = unchecked((ulong)Math.Max(Volatile.Read(ref _vulkanImageLayoutTransitionSerial), 0L)),
            DescriptorTableGeneration = unchecked((ulong)Math.Max(Volatile.Read(ref _vulkanDescriptorTableGeneration), 0L)),
            FirstFailingApi = Volatile.Read(ref _firstFailingVulkanApi),
        };
    }

    private string ResolveVulkanQueueKind(Queue queue)
    {
        if (queue.Handle == graphicsQueue.Handle)
            return "Graphics";
        if (queue.Handle == transferQueue.Handle)
            return "Transfer";
        if (queue.Handle == presentQueue.Handle)
            return "Present";

        return "Unknown";
    }

    private void RecordLastVulkanSubmissionDiagnosticContext(VulkanSubmissionDiagnosticContext context)
    {
        lock (_vulkanSubmissionDiagnosticsLock)
            _lastVulkanSubmissionDiagnosticContext = context;

        if (_diagnosticOptions.EnableCrashBreadcrumbs)
            RecordVulkanCrashBreadcrumb(context);
    }

    private VulkanSubmissionDiagnosticContext SnapshotLastVulkanSubmissionDiagnosticContext()
    {
        lock (_vulkanSubmissionDiagnosticsLock)
            return _lastVulkanSubmissionDiagnosticContext;
    }

    private string BuildDeviceLostReasonWithSubmissionContext(string? reason)
    {
        string baseReason = string.IsNullOrWhiteSpace(reason) ? "<unknown>" : reason.Trim();
        VulkanSubmissionDiagnosticContext context = SnapshotLastVulkanSubmissionDiagnosticContext();
        StringBuilder builder = new(baseReason);

        if (!context.IsEmpty)
            builder.Append("; ").Append(DescribeVulkanSubmissionDiagnosticContext(context));

        string breadcrumbs = DescribeVulkanCrashBreadcrumbTail();
        if (!string.IsNullOrWhiteSpace(breadcrumbs))
            builder.Append("; ").Append(breadcrumbs);

        string validationSummary = DescribeVulkanValidationSummary();
        if (!string.IsNullOrWhiteSpace(validationSummary))
            builder.Append("; ").Append(validationSummary);

        string? firstFailingApi = Volatile.Read(ref _firstFailingVulkanApi);
        if (!string.IsNullOrWhiteSpace(firstFailingApi))
            builder.Append("; FirstFailingApi=").Append(firstFailingApi);

        string faultSummary = DescribeVulkanFaultDiagnosticsAfterDeviceLoss();
        if (!string.IsNullOrWhiteSpace(faultSummary))
            builder.Append("; ").Append(faultSummary);

        return builder.ToString();
    }

    private void RecordVulkanCrashBreadcrumb(VulkanSubmissionDiagnosticContext context)
    {
        long serial = Interlocked.Increment(ref _vulkanCrashBreadcrumbSerial);
        int index = unchecked((int)((serial - 1) % VulkanCrashBreadcrumbCapacity));
        _vulkanCrashBreadcrumbs[index] = new()
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
            QueueKind = context.QueueKind,
            CommandBufferCount = context.CommandBufferCount,
            FirstCommandBufferHandle = context.FirstCommandBufferHandle,
            FenceHandle = context.FenceHandle,
            WaitTimelineValue = context.WaitTimelineValue,
            SignalTimelineValue = context.SignalTimelineValue,
            Caller = context.Caller,
            LastCommandMarkerSerial = context.LastCommandMarkerSerial,
            LastCommandMarkerKind = context.LastCommandMarkerKind,
            LastCommandMarkerPassIndex = context.LastCommandMarkerPassIndex,
            LastCommandMarkerBatchIndex = context.LastCommandMarkerBatchIndex,
            ImageLayoutTransitionSerial = context.ImageLayoutTransitionSerial,
            DescriptorTableGeneration = context.DescriptorTableGeneration,
            FirstFailingApi = context.FirstFailingApi,
        };
    }

    private string DescribeVulkanCrashBreadcrumbTail(int maxEntries = 8)
    {
        if (!_diagnosticOptions.EnableCrashBreadcrumbs)
            return string.Empty;

        long latestSerial = Volatile.Read(ref _vulkanCrashBreadcrumbSerial);
        if (latestSerial <= 0)
            return string.Empty;

        int count = (int)Math.Min(latestSerial, VulkanCrashBreadcrumbCapacity);
        int emitted = 0;
        StringBuilder builder = new();
        builder.Append("BreadcrumbTail");

        for (long serial = latestSerial; serial > 0 && emitted < maxEntries && emitted < count; serial--)
        {
            int index = unchecked((int)((serial - 1) % VulkanCrashBreadcrumbCapacity));
            VulkanCrashBreadcrumb breadcrumb = _vulkanCrashBreadcrumbs[index];
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

    private string DescribeVulkanFaultDiagnosticsAfterDeviceLoss()
    {
        StringBuilder builder = new();
        AppendDeviceFaultSummary(builder);
        AppendNvCheckpointSummary(builder);
        AppendDeviceAddressBindingSummary(builder);
        return builder.ToString();
    }

    private void AppendDeviceFaultSummary(StringBuilder builder)
    {
        AppendDeviceFaultSummaryDetailed(builder);
    }

    private void AppendNvCheckpointSummary(StringBuilder builder)
    {
        AppendNvCheckpointSummaryDetailed(builder);
    }

    private static void AppendFaultSection(StringBuilder builder, string value)
    {
        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(value);
    }

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
            $"queue={context.QueueKind ?? "<unknown>"} " +
            $"waits={context.WaitSemaphoreCount} signals={context.SignalSemaphoreCount} " +
            $"cmds={context.CommandBufferCount} firstCmd=0x{context.FirstCommandBufferHandle:X} " +
            $"fence=0x{context.FenceHandle:X} " +
            $"timeline(wait={context.WaitTimelineValue},signal={context.SignalTimelineValue}) " +
            $"opMarker=#{context.LastCommandMarkerSerial}:{context.LastCommandMarkerKind ?? "<unknown>"} " +
            $"opPass={context.LastCommandMarkerPassIndex} opBatch={context.LastCommandMarkerBatchIndex} " +
            $"layoutSerial={context.ImageLayoutTransitionSerial} descriptorGen={context.DescriptorTableGeneration} " +
            $"firstApi={context.FirstFailingApi ?? "<none>"} " +
            $"caller={context.Caller ?? "<unknown>"}";
    }
}
