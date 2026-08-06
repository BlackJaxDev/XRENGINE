using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly VulkanFinalPresentationLedgerState _finalPresentationLedger =
        new(XREnvironment.IsEnabled(
            XREngineEnvironmentVariables.VulkanFinalPresentationLedger));

    internal void ObserveFinalPresentationDescriptor(
        int descriptorSlot,
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        uint set,
        uint binding,
        string? bindingName,
        in DescriptorImageInfo imageInfo,
        ulong resourceSignature,
        bool writeMatched,
        bool writeSucceeded)
    {
        if (!string.Equals(bindingName, "SourceTexture", StringComparison.Ordinal))
            return;

        if (writeMatched && writeSucceeded)
        {
            VulkanPresentationSourceTuple current =
                _windowPresentSource.Capture();
            _ = _windowPresentSource.TryBindDescriptor(
                current.LogicalEpoch,
                imageInfo,
                descriptorSet,
                GetCurrentVulkanResourceGeneration(
                    ObjectType.DescriptorSet,
                    descriptorSet.Handle),
                descriptorSlot,
                resourceSignature,
                commandBuffer,
                ResolveCommandBufferRecordingGeneration(commandBuffer),
                out _);
        }

        if (!_finalPresentationLedger.Enabled)
            return;

        _finalPresentationLedger.ObserveDescriptor(
            VulkanFrameCounter,
            descriptorSlot,
            unchecked((ulong)commandBuffer.Handle),
            descriptorSet.Handle,
            set,
            binding,
            bindingName,
            imageInfo,
            resourceSignature,
            writeMatched,
            writeSucceeded);
    }

    private void RecordFinalPresentationLedger(
        ref VulkanFrameAttempt attempt,
        Result presentResult,
        bool presentAccepted,
        bool hasValidFrameContent)
    {
        if (!_finalPresentationLedger.Enabled)
            return;

        string? sourceName = _lastWindowPresentColorTexture?.Name;
        uint sourceWidth = _lastWindowPresentFrameBuffer?.Width ?? 0;
        uint sourceHeight = _lastWindowPresentFrameBuffer?.Height ?? 0;
        RenderTextureSamplingState samplingState =
            GetTextureShaderSamplingState(_lastWindowPresentColorTexture);

        VkImageDescriptorSnapshot sourceSnapshot = default;
        bool sourceSnapshotReady =
            _lastWindowPresentColorTexture is not null &&
            GetOrCreateAPIRenderObject(
                _lastWindowPresentColorTexture,
                generateNow: false) is IVkImageDescriptorSource source &&
            source.TryGetDescriptorSnapshot(
                requestedViewType: null,
                requestedAspectMask: null,
                "final-presentation ledger",
                allowSynchronousUpload: false,
                out sourceSnapshot);

        VulkanFinalPresentationDescriptorObservation descriptor =
            _finalPresentationLedger.CaptureLatestDescriptor();

        _ = TryGetCommandBufferDiagnosticMetadata(
            attempt.ImageIndex,
            attempt.SceneCommandBuffer,
            out ulong plannerRevision,
            out ulong frameOpContextId,
            out ulong commandResourceGeneration,
            out ulong commandDescriptorGeneration);
        ulong commandRecordingGeneration =
            ResolveCommandBufferRecordingGeneration(
                attempt.SceneCommandBuffer);
        bool hadValidPriorSwapchainContent =
            _swapchainImageHasValidPresentedContent is not null &&
            attempt.ImageIndex < _swapchainImageHasValidPresentedContent.Length &&
            _swapchainImageHasValidPresentedContent[attempt.ImageIndex];

        bool invariantFailed = false;
        string? invariantFailure = null;
        if (presentAccepted && hasValidFrameContent)
        {
            if (_lastWindowPresentColorTexture is not null &&
                (!samplingState.IsReady || !sourceSnapshotReady ||
                 sourceSnapshot.View.Handle == 0))
            {
                invariantFailed = true;
                invariantFailure = "accepted desktop present source is not descriptor-ready";
            }
            else if (_lastWindowPresentColorTexture is not null &&
                     (descriptor.Sequence == 0 ||
                      descriptor.DescriptorSlot != unchecked((int)attempt.ImageIndex)))
            {
                invariantFailed = true;
                invariantFailure = "final source descriptor observation is missing or belongs to another frame-data slot";
            }
            else if (_lastWindowPresentColorTexture is not null &&
                     !descriptor.WriteSucceeded)
            {
                invariantFailed = true;
                invariantFailure = "final source descriptor write did not complete";
            }
            else if (_lastWindowPresentColorTexture is not null &&
                     (descriptor.ImageView != sourceSnapshot.View.Handle ||
                      descriptor.Sampler != sourceSnapshot.Sampler.Handle))
            {
                invariantFailed = true;
                invariantFailure = "bound final source descriptor payload differs from the current native source";
            }
            else if (attempt.SceneSwapchainWriteCount <= 0 &&
                     attempt.RecoverySwapchainWriteCount <= 0 &&
                     !attempt.HasImGuiOverlayCommandBuffer &&
                     !attempt.HasDynamicTextOverlayCommandBuffer &&
                     !hadValidPriorSwapchainContent)
            {
                invariantFailed = true;
                invariantFailure = "accepted desktop present has no recorded swapchain writer";
            }
        }

        _finalPresentationLedger.Append(
            new VulkanFinalPresentationLedgerEntry(
                attempt.FrameNumber,
                attempt.FrameSlot,
                attempt.ImageIndex,
                _swapchainGeneration,
                swapChain.Handle,
                swapChainExtent.Width,
                swapChainExtent.Height,
                attempt.LiveFramebufferWidth,
                attempt.LiveFramebufferHeight,
                attempt.InteractiveResize,
                sourceName,
                sourceWidth,
                sourceHeight,
                samplingState.IsReady,
                samplingState.DescriptorResourceEpoch,
                sourceSnapshot.Generation,
                sourceSnapshot.Image.Handle,
                sourceSnapshot.View.Handle,
                sourceSnapshot.Sampler.Handle,
                sourceSnapshot.TrackedLayout,
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

    public object GetFinalPresentationLedgerDiagnostics(int limit = 64)
    {
        _finalPresentationLedger.CaptureStatus(
            out bool enabled,
            out bool frozen,
            out int count,
            out string? freezeReason);
        VulkanFinalPresentationLedgerEntry[] entries =
            _finalPresentationLedger.Snapshot(limit);
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

    public object ConfigureFinalPresentationLedgerDiagnostics(
        bool enabled,
        bool frozen,
        bool clear)
    {
        _finalPresentationLedger.Configure(enabled, frozen, clear);
        return GetFinalPresentationLedgerDiagnostics(1);
    }
}
