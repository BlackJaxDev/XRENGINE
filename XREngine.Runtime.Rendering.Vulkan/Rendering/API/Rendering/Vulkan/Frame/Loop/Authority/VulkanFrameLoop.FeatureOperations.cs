using XREngine.Data.Rendering;
using XREngine.Rendering.DLSS;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frame-operation translations that capture immutable context at their authority boundary.</summary>
internal sealed partial class VulkanFrameLoop
{
    internal string GetMeshletDispatchUnsupportedReason()
        => !_deviceContext.Capabilities.Supports(EVulkanDeviceCapability.MeshShader)
            ? "VK_EXT_mesh_shader is not available on the active Vulkan device."
            : _deviceContext.SupportsMeshTaskIndirectCount
                ? "VK_EXT_mesh_shader indirect-count dispatch is available."
                : "VK_EXT_mesh_shader is visible, but task/mesh shader features or vkCmdDrawMeshTasksIndirectCountEXT dispatch are unavailable.";

    internal ERendererComputeEnqueueStatus TryDispatchComputeIndirect(XRRenderProgram program, XRDataBuffer arguments, nint byteOffset, string label)
        => _commandRuntime.TryEnqueueIndirectComputeDispatch(_resourceRuntime.WrapperLookup, _frameOperationQueue, program, arguments, byteOffset, label, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), AllowSynchronousResourceUploads, IsDeviceLost);

    internal ERendererComputeEnqueueStatus TryEnqueueBufferCopy(XRDataBuffer source, nint sourceOffset, XRDataBuffer destination, nint destinationOffset, nuint byteCount, string label)
        => _commandRuntime.TryEnqueueBufferCopy(_resourceRuntime.WrapperLookup, _frameOperationQueue, source, sourceOffset, destination, destinationOffset, byteCount, label, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), AllowSynchronousResourceUploads, IsDeviceLost);

    internal ERendererComputeEnqueueStatus TryCompleteOrderedComputePass(EMemoryBarrierMask mask, string label)
        => _commandRuntime.TryEnqueueOrderedComputeBarrier(_frameOperationQueue, mask, label, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), IsDeviceLost);

    internal XRGpuFence? InsertOrderedComputeFence()
        => _commandRuntime.TryEnqueueOrderedComputeFence(_frameOperationQueue, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive());

    internal bool TryDrawMeshTasksIndirectCount(XRDataBuffer indirect, XRDataBuffer count, uint maxDrawCount, uint stride, nuint byteOffset, nuint countByteOffset, out string failureReason)
        => _commandRuntime.TryEnqueueMeshTaskIndirectCount(_resourceRuntime.WrapperLookup, _frameOperationQueue, indirect, count, maxDrawCount, stride, byteOffset, countByteOffset, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContext(), AllowSynchronousResourceUploads, out failureReason);

    internal bool TryEnqueueDlssUpscale(int passIndex, IRuntimeVendorUpscaleSession session, XRTexture sourceColor, XRTexture depth, XRTexture motion, XRTexture outputColor, XRTexture? exposure, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
        => VulkanUpscaleBridgeSidecar.TryEnqueueDlssUpscale(_resourceRuntime.WrapperLookup, _commandRuntime, _frameOperationQueue, passIndex, session, sourceColor, depth, motion, outputColor, exposure, parameters, CaptureFrameOpContext(), out failureReason);

    internal bool TryEnqueueFrameGeneration(int passIndex, IRuntimeVendorUpscaleSession session, XRTexture depth, XRTexture motion, XRTexture hudlessColor, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
        => VulkanUpscaleBridgeSidecar.TryEnqueueFrameGeneration(_resourceRuntime.WrapperLookup, _commandRuntime, _frameOperationQueue, passIndex, session, depth, motion, hudlessColor, parameters, CaptureFrameOpContext(), out failureReason);

    internal bool TryDispatchFrameGeneration(XRViewport viewport, in VulkanUpscaleBridgeDispatchParameters parameters, XRTexture depth, XRTexture motion, XRTexture hudlessColor, out int errorCode, out string? errorMessage)
        => VulkanUpscaleBridgeSidecar.TryDispatchFrameGeneration(_resourceRuntime.WrapperLookup, viewport, parameters, depth, motion, hudlessColor, out errorCode, out errorMessage);
}
