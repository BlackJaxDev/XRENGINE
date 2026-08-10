using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanFrameLoop
{
    private bool UseDynamicRenderingRenderTargets
        => _deviceContext.MutableCapabilities._useDynamicRenderingRenderTargets;

    /// <summary>
    /// Captures the current output attachment identities and delegates native ImGui
    /// command encoding to the renderer-free recorder.  No renderer facade or
    /// output-state lookup is available after this immutable input is created.
    /// </summary>
    private bool TryRecordImGuiOverlay(
        uint imageIndex,
        VulkanImGuiFrameSnapshot snapshot,
        ImageLayout initialSwapchainLayout,
        CommandBuffer predecessorCommandBuffer,
        out CommandBuffer overlayCommandBuffer)
    {
        overlayCommandBuffer = default;
        if (!ImGuiOverlayAdmission.CanRecord(imageIndex) ||
            !VulkanImGuiOverlayAdmission.HasRenderableSnapshot(snapshot) ||
            !_outputRuntime.TryCaptureDynamicUiOverlayTarget(
                imageIndex,
                out VulkanDynamicUiOverlayTarget target))
        {
            return false;
        }

        ImGuiFontAtlasResources.EnsureCreated();
        ImGuiOutputPipelineService.EnsureCreated();

        CommandBuffer[]? commandBuffers = _outputRuntime._imguiResources.OverlayCommandBuffers;
        if (commandBuffers is null || imageIndex >= commandBuffers.Length)
            return false;

        VulkanImGuiOverlayRecordingInput input = new(
            imageIndex,
            commandBuffers[imageIndex],
            predecessorCommandBuffer,
            initialSwapchainLayout,
            _deviceContext.InstanceApiVersion < Vk.Version13,
            target,
            _outputRuntime._imguiResources,
            _outputRuntime._imguiTextureRegistry.DescriptorSets,
            ClearSwapchain: false,
            snapshot);
        return _imguiOverlayRecorder.TryRecord(
            new VulkanTrackedCommandEncoder(_commandRuntime),
            _telemetry,
            ImGuiDrawBufferResources,
            in input,
            out overlayCommandBuffer);
    }

    private VulkanStreamlineDeviceBinding StreamlineDeviceBinding
        => _outputRuntime.CaptureStreamlineDeviceBinding(_deviceContext);

    private void MarkDlssFrameGenerationPclMarker(
        NvidiaDlssManager.Native.StreamlinePclMarker marker)
    {
        if (!OutputRuntime.Desktop.StreamlineFrameGenerationActive)
            return;

        uint frameIndex = unchecked((uint)Math.Min(
            uint.MaxValue,
            AcceptedAttemptCount));
        if (NvidiaDlssManager.Native.TryMarkFrameGenerationPclMarker(
                StreamlineDeviceBinding,
                marker,
                frameIndex,
                out string failureReason))
        {
            return;
        }

        string message =
            $"NVIDIA DLSS frame generation failed to set Streamline PCL marker {marker}: {failureReason}";
        Debug.RenderingError(message);
        throw new InvalidOperationException(message);
    }

    private void DisableStreamlineFrameGenerationBeforeSwapchainMutation(
        string reason)
    {
        if (!OutputRuntime.Desktop.StreamlineFrameGenerationActive)
            return;

        var viewports = DesktopWsiOutput.Window.Viewports;
        if (viewports.Count == 0)
        {
            Debug.RenderingWarning(
                "NVIDIA DLSS frame generation is active, but no viewport was available to send DLSSGMode.Off before {0}.",
                reason);
            return;
        }

        VulkanStreamlineDeviceBinding binding = StreamlineDeviceBinding;
        for (int index = 0; index < viewports.Count; index++)
        {
            XRViewport viewport = viewports[index];
            if (NvidiaDlssManager.Native.TryDisableFrameGeneration(
                    binding,
                    viewport,
                    out string failureReason))
            {
                continue;
            }

            Debug.RenderingError(
                "NVIDIA DLSS frame generation could not be disabled before {0} for viewport {1}: {2}",
                reason,
                viewport.Index,
                failureReason);
        }
    }

    private void DrainStreamlineFrameGenerationDisableBeforePresent()
    {
        if (!OutputRuntime.Desktop.StreamlineFrameGenerationActive ||
            NvidiaDlssManager.IsFrameGenerationRequested)
        {
            return;
        }

        VulkanStreamlineDeviceBinding binding = StreamlineDeviceBinding;
        var viewports = DesktopWsiOutput.Window.Viewports;
        for (int index = 0; index < viewports.Count; index++)
        {
            XRViewport viewport = viewports[index];
            if (NvidiaDlssManager.Native.TryDrainFrameGenerationDisableForPresent(
                    binding,
                    viewport,
                    out string failureReason))
            {
                continue;
            }

            Debug.RenderingError(
                "NVIDIA DLSS frame generation could not finish its disable drain for viewport {0}: {1}",
                viewport.Index,
                failureReason);
        }
    }

    private bool TryPresentToQueueTracked(
        Queue queue,
        ref PresentInfoKHR presentInfo,
        out Result result,
        out string failureReason,
        out Exception? postDispatchFailure,
        [CallerMemberName] string? caller = null)
    {
        postDispatchFailure = null;
        if (!TryAdmitVulkanDeviceOperation(
                "vkQueuePresentKHR",
                out failureReason))
        {
            result = Result.ErrorDeviceLost;
            lock (_oneTimeSubmitLock)
                RecordQueueOperation("present-rejected", queue, result, caller);
            return false;
        }

        using VulkanQueueOperationLease queueOperation =
            VulkanQueueOperationLease.TryEnter(
                _oneTimeSubmitLock,
                _deviceContext.StateMachine,
                _frameTelemetry);
        if (!queueOperation.Acquired)
        {
            result = Result.ErrorDeviceLost;
            failureReason = "Vulkan device is not operational";
            lock (_oneTimeSubmitLock)
                RecordQueueOperation("present-rejected", queue, result, caller);
            return false;
        }

        bool dispatched;
        if (OutputRuntime.Desktop.StreamlineFrameGenerationActive)
        {
            dispatched = NvidiaDlssManager.Native.TryQueueProxyPresent(
                StreamlineDeviceBinding,
                queue,
                ref presentInfo,
                out result,
                out failureReason);
        }
        else
        {
            result = OutputRuntime.Desktop.SwapchainExtension!.QueuePresent(
                queue,
                ref presentInfo);
            failureReason = string.Empty;
            dispatched = true;
        }

        try
        {
            RecordQueueOperation("present", queue, result, caller);
            if (result == Result.ErrorDeviceLost)
            {
                MarkDeviceLost(
                    $"vkQueuePresentKHR:{caller ?? "<unknown>"}:{result}; QueuePresent returned ErrorDeviceLost in {caller ?? "<unknown>"}",
                    "vkQueuePresentKHR",
                    result);
            }
        }
        catch (Exception exception)
        {
            postDispatchFailure = exception;
        }

        return dispatched;
    }

    private void RecordQueueOperation(
        string operation,
        Queue queue,
        Result result,
        string? caller)
        => _commandRuntime.Synchronization.RecordQueueOperation(
            _deviceContext.State,
            operation,
            queue,
            result,
            submissionSerial: 0,
            caller);

    private VulkanSubmissionDiagnosticContext CreateDesktopSubmissionDiagnosticContext(
        string submissionKind,
        uint imageIndex,
        ulong frameNumber,
        int frameSlot,
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
            FrameSlot = frameSlot,
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

    private void RecordFinalPresentationLedger(
        ref VulkanFrameAttempt attempt,
        Result presentResult,
        bool presentAccepted,
        bool hasValidFrameContent)
    {
        if (!_frameTelemetry._finalPresentationLedger.Enabled)
            return;

        VulkanPresentationSourceTuple source = _windowPresentSource
            .CaptureForDescriptorSlot(unchecked((int)attempt.ImageIndex));
        bool sourceSnapshotReady = source.ColorTexture is not null &&
            source.Image.Handle != 0 &&
            source.ImageView.Handle != 0 &&
            source.Sampler.Handle != 0 &&
            source.DescriptorResourceEpoch != 0;
        VulkanFinalPresentationDescriptorObservation descriptor =
            _frameTelemetry._finalPresentationLedger.CaptureLatestDescriptor();

        _ = _commandRuntime.CommandBuffers.TryGetDiagnosticMetadata(
            attempt.ImageIndex,
            attempt.SceneCommandBuffer,
            out ulong plannerRevision,
            out ulong frameOpContextId,
            out ulong commandResourceGeneration,
            out ulong commandDescriptorGeneration);
        ulong commandRecordingGeneration = _commandRuntime.CommandBuffers
            .ResolveRecordingGeneration(attempt.SceneCommandBuffer);
        bool hadValidPriorSwapchainContent =
            OutputRuntime.Desktop.ImageHasValidPresentedContent is not null &&
            attempt.ImageIndex <
            OutputRuntime.Desktop.ImageHasValidPresentedContent.Length &&
            OutputRuntime.Desktop.ImageHasValidPresentedContent[attempt.ImageIndex];

        bool invariantFailed = false;
        string? invariantFailure = null;
        if (presentAccepted && hasValidFrameContent)
        {
            if (source.ColorTexture is not null && !sourceSnapshotReady)
            {
                invariantFailed = true;
                invariantFailure =
                    "accepted desktop present source is not descriptor-ready";
            }
            else if (source.ColorTexture is not null &&
                     (descriptor.Sequence == 0 ||
                      descriptor.DescriptorSlot != unchecked((int)attempt.ImageIndex)))
            {
                invariantFailed = true;
                invariantFailure =
                    "final source descriptor observation is missing or belongs to another frame-data slot";
            }
            else if (source.ColorTexture is not null && !descriptor.WriteSucceeded)
            {
                invariantFailed = true;
                invariantFailure =
                    "final source descriptor write did not complete";
            }
            else if (source.ColorTexture is not null &&
                     (descriptor.ImageView != source.ImageView.Handle ||
                      descriptor.Sampler != source.Sampler.Handle))
            {
                invariantFailed = true;
                invariantFailure =
                    "bound final source descriptor payload differs from the current native source";
            }
            else if (attempt.SceneSwapchainWriteCount <= 0 &&
                     attempt.RecoverySwapchainWriteCount <= 0 &&
                     !attempt.HasImGuiOverlayCommandBuffer &&
                     !attempt.HasDynamicTextOverlayCommandBuffer &&
                     !hadValidPriorSwapchainContent)
            {
                invariantFailed = true;
                invariantFailure =
                    "accepted desktop present has no recorded swapchain writer";
            }
        }

        _frameTelemetry._finalPresentationLedger.Append(
            new VulkanFinalPresentationLedgerEntry(
                attempt.FrameNumber,
                attempt.FrameSlot,
                attempt.ImageIndex,
                OutputRuntime.Desktop.Generation,
                OutputRuntime.Desktop.Swapchain.Handle,
                OutputRuntime.Desktop.Extent.Width,
                OutputRuntime.Desktop.Extent.Height,
                attempt.LiveFramebufferWidth,
                attempt.LiveFramebufferHeight,
                attempt.InteractiveResize,
                source.ColorTexture?.Name,
                source.FrameBuffer?.Width ?? source.Width,
                source.FrameBuffer?.Height ?? source.Height,
                sourceSnapshotReady,
                source.DescriptorResourceEpoch,
                source.DescriptorResourceEpoch,
                source.Image.Handle,
                source.ImageView.Handle,
                source.Sampler.Handle,
                source.ExpectedLayout,
                descriptor,
                unchecked((ulong)attempt.SceneCommandBuffer.Handle),
                commandRecordingGeneration,
                attempt.ScenePrimaryRecordedThisFrame,
                plannerRevision,
                frameOpContextId,
                commandResourceGeneration,
                commandDescriptorGeneration,
                attempt.SceneCommandBufferDirtyGeneration,
                attempt.SceneSwapchainWriteCount,
                attempt.RecoverySwapchainWriteCount,
                hadValidPriorSwapchainContent,
                attempt.HasImGuiOverlayCommandBuffer,
                attempt.HasDynamicTextOverlayCommandBuffer,
                presentResult,
                presentAccepted,
                hasValidFrameContent,
                invariantFailed,
                invariantFailure));
    }

    /// <summary>
    /// Captures the bounded final-presentation evidence owned by this frame-loop authority.
    /// </summary>
    internal object GetFinalPresentationLedgerDiagnostics(int limit)
    {
        _frameTelemetry._finalPresentationLedger.CaptureStatus(
            out bool enabled,
            out bool frozen,
            out int count,
            out string? freezeReason);
        VulkanFinalPresentationLedgerEntry[] entries =
            _frameTelemetry._finalPresentationLedger.Snapshot(limit);
        return new
        {
            enabled,
            frozen,
            capacity = 128,
            count,
            returnedCount = entries.Length,
            freezeReason,
            entries,
        };
    }

    /// <summary>
    /// Configures the bounded final-presentation evidence owned by this frame-loop authority.
    /// </summary>
    internal object ConfigureFinalPresentationLedgerDiagnostics(
        bool enabled,
        bool frozen,
        bool clear)
    {
        _frameTelemetry._finalPresentationLedger.Configure(enabled, frozen, clear);
        return GetFinalPresentationLedgerDiagnostics(1);
    }
}
