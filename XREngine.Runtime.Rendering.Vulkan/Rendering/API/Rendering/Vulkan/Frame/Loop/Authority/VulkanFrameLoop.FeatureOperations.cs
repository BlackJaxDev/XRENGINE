using XREngine.Data.Rendering;
using XREngine.Rendering.DLSS;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frame-operation translations that capture immutable context at their authority boundary.</summary>
internal sealed partial class VulkanFrameLoop
{
    internal bool SupportsAdvancedVisibilityStage(EAdvancedRenderStage stage)
        => stage is (EAdvancedRenderStage.VisibilityPreparation or
               EAdvancedRenderStage.VisibilityRaster or
               EAdvancedRenderStage.DepthPyramidAndLateVisibility) &&
           _commandRuntime.IsAdvancedVisibilityProductionPromoted &&
           _deviceContext.IsOperational &&
           _resourceRuntime.AdvancedVisibilityResources.IsReady &&
           _resourceRuntime.AdvancedSceneResources.IsReady &&
           _deviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount) &&
           _commandRuntime.CanAdmitAdvancedVisibilityFamily();

    internal bool TryEnqueueAdvancedVisibilityStage(
        in AdvancedVisibilityStageBackendRequest request,
        out string failureReason)
    {
        if (!request.IsValid)
        {
            failureReason = "The advanced visibility request is incomplete.";
            return false;
        }
        if (request.Views.ViewCount != 1)
        {
            failureReason =
                "The Vulkan advanced visibility family currently admits exactly one view; stereo and multiview requests remain fail-closed.";
            return false;
        }
        if (!SupportsAdvancedVisibilityStage(request.Stage))
        {
            failureReason = "The Vulkan zero-readback visibility lane is unavailable on this device or resource generation.";
            return false;
        }

        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex < 0)
        {
            failureReason = "No render-graph pass is active for the advanced visibility stage.";
            return false;
        }

        FrameOpContext context = CaptureFrameOpContextForCurrentPipelineScope();
        VulkanAdvancedVisibilityStageRequest vulkanRequest = new(
            request.Stage,
            request.Publication,
            request.Publication.VisibilityContentGeneration,
            request.Extractor,
            request.RenderFrameId,
            request.Views,
            request.Target,
            request.IdentityTargetName,
            request.MetadataTargetName,
            request.SelectionTargetName,
            request.DepthTargetName,
            request.CurrentDepthPyramidTargetName);
        _frameOperationQueue.EnqueuePrepared(
            new AdvancedVisibilityOp(passIndex, vulkanRequest, context));
        failureReason = "Ready";
        return true;
    }

    internal string GetMeshletDispatchUnsupportedReason()
        => _deviceContext.MeshletDispatchStatus;

    internal ERendererComputeEnqueueStatus TryDispatchComputeIndirect(XRRenderProgram program, XRDataBuffer arguments, nint byteOffset, string label)
        => _commandRuntime.TryEnqueueIndirectComputeDispatch(_resourceRuntime.WrapperLookup, _frameOperationQueue, program, arguments, byteOffset, label, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), AllowSynchronousResourceUploads, IsDeviceLost);

    internal ERendererComputeEnqueueStatus TryEnqueueBufferCopy(XRDataBuffer source, nint sourceOffset, XRDataBuffer destination, nint destinationOffset, nuint byteCount, string label)
        => _commandRuntime.TryEnqueueBufferCopy(_resourceRuntime.WrapperLookup, _frameOperationQueue, source, sourceOffset, destination, destinationOffset, byteCount, label, requireGpuWriteVisibility: false, diagnosticReceipt: null, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), AllowSynchronousResourceUploads, IsDeviceLost);

    internal ERendererComputeEnqueueStatus TryEnqueueGpuBufferCopy(XRDataBuffer source, nint sourceOffset, XRDataBuffer destination, nint destinationOffset, nuint byteCount, string label)
        => _commandRuntime.TryEnqueueBufferCopy(_resourceRuntime.WrapperLookup, _frameOperationQueue, source, sourceOffset, destination, destinationOffset, byteCount, label, requireGpuWriteVisibility: true, diagnosticReceipt: null, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), AllowSynchronousResourceUploads, IsDeviceLost);

    internal ERendererComputeEnqueueStatus TryEnqueueGpuDiagnosticBufferSnapshot(XRDataBuffer source, XRDataBuffer destination, nuint byteCount, string label)
        => TryEnqueueGpuDiagnosticBufferSnapshot(source, 0, destination, 0, byteCount, label);

    internal ERendererComputeEnqueueStatus TryEnqueueGpuDiagnosticBufferSnapshot(XRDataBuffer source, nuint sourceByteOffset, XRDataBuffer destination, nuint destinationByteOffset, nuint byteCount, string label)
    {
        GpuDiagnosticSnapshotReceipt receipt = GetOrCreateGpuDiagnosticSnapshotReceipt(destination);
        ERendererComputeEnqueueStatus status = _commandRuntime.TryEnqueueBufferCopy(
            _resourceRuntime.WrapperLookup,
            _frameOperationQueue,
            source,
            checked((nint)sourceByteOffset),
            destination,
            checked((nint)destinationByteOffset),
            byteCount,
            label,
            requireGpuWriteVisibility: true,
            receipt,
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            CaptureFrameOpContextForCurrentPipelineScope(),
            AllowSynchronousResourceUploads,
            IsDeviceLost);
        if (status == ERendererComputeEnqueueStatus.Enqueued)
            receipt.RegisterCopy();
        return status;
    }

    internal ERendererComputeEnqueueStatus TryCompleteOrderedComputePass(EMemoryBarrierMask mask, string label)
        => _commandRuntime.TryEnqueueOrderedComputeBarrier(_frameOperationQueue, mask, label, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), IsDeviceLost);

    internal XRGpuFence? InsertOrderedComputeFence()
        => _commandRuntime.TryEnqueueOrderedComputeFence(_frameOperationQueue, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive());

    internal bool TryDrawMeshTasksIndirectCount(XRRenderProgram program, XRDataBuffer indirect, XRDataBuffer count, uint maxDrawCount, uint stride, nuint byteOffset, nuint countByteOffset, out string failureReason)
        => _commandRuntime.TryEnqueueMeshTaskIndirectCount(_resourceRuntime.WrapperLookup, _resourceRuntime.Descriptors, _frameOperationQueue, program, indirect, count, maxDrawCount, stride, byteOffset, countByteOffset, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextForCurrentPipelineScope(), AllowSynchronousResourceUploads, out failureReason);

    internal bool TryEnqueueDlssUpscale(int passIndex, IRuntimeVendorUpscaleSession session, XRTexture sourceColor, XRTexture depth, XRTexture motion, XRTexture outputColor, XRTexture? exposure, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
        => VulkanUpscaleBridgeSidecar.TryEnqueueDlssUpscale(_resourceRuntime.WrapperLookup, _commandRuntime, _frameOperationQueue, passIndex, session, sourceColor, depth, motion, outputColor, exposure, parameters, CaptureFrameOpContextForCurrentPipelineScope(), out failureReason);

    internal bool TryEnqueueFrameGeneration(int passIndex, IRuntimeVendorUpscaleSession session, XRTexture depth, XRTexture motion, XRTexture hudlessColor, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
        => VulkanUpscaleBridgeSidecar.TryEnqueueFrameGeneration(_resourceRuntime.WrapperLookup, _commandRuntime, _frameOperationQueue, passIndex, session, depth, motion, hudlessColor, parameters, CaptureFrameOpContextForCurrentPipelineScope(), out failureReason);

    internal bool TryDispatchFrameGeneration(XRViewport viewport, in VulkanUpscaleBridgeDispatchParameters parameters, XRTexture depth, XRTexture motion, XRTexture hudlessColor, out int errorCode, out string? errorMessage)
        => VulkanUpscaleBridgeSidecar.TryDispatchFrameGeneration(_resourceRuntime.WrapperLookup, viewport, parameters, depth, motion, hudlessColor, out errorCode, out errorMessage);
}
