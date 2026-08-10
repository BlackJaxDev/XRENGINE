using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Translates legacy indirect-draw API calls into frozen Vulkan frame operations.</summary>
public unsafe partial class VulkanRenderer
{
    public override void BindVAOForRenderer(XRMeshRenderer.BaseVersion? version)
        => _commandRuntime.BindIndirectMesh(
            version is null ? null : GenericToAPI<VkMeshRenderer>(version));

    public override bool ValidateIndexedVAO(XRMeshRenderer.BaseVersion? version)
        => TryGetIndexBufferInfo(version, out _, out _);

    public override bool TryGetIndexBufferInfo(
        XRMeshRenderer.BaseVersion? version,
        out IndexSize indexElementSize,
        out uint indexCount)
        => _commandRuntime.TryGetIndirectIndexBufferInfo(
            version is null ? null : GenericToAPI<VkMeshRenderer>(version),
            out indexElementSize,
            out indexCount);

    public override bool TrySyncMeshRendererIndexBuffer(
        XRMeshRenderer meshRenderer,
        XRDataBuffer indexBuffer,
        IndexSize elementSize)
    {
        if (meshRenderer is null || indexBuffer is null ||
            GenericToAPI<VkMeshRenderer>(meshRenderer.GetDefaultVersion()) is not { } mesh ||
            GenericToAPI<VkDataBuffer>(indexBuffer) is not { } buffer)
        {
            return false;
        }

        bool synchronized = _commandRuntime.TrySyncIndirectIndexBuffer(
            mesh,
            buffer,
            indexBuffer,
            elementSize,
            out bool boundStateChanged);
        if (boundStateChanged)
            MarkCommandBuffersDirtyForLegacyMeshState();
        return synchronized;
    }

    public override void BindDrawIndirectBuffer(XRDataBuffer buffer)
        => _commandRuntime.BindIndirectBuffer(GenericToAPI<VkDataBuffer>(buffer));

    public override void UnbindDrawIndirectBuffer()
        => _commandRuntime.BindIndirectBuffer(null);

    public override void BindParameterBuffer(XRDataBuffer buffer)
        => _commandRuntime.BindIndirectCountBuffer(GenericToAPI<VkDataBuffer>(buffer));

    public override void UnbindParameterBuffer()
        => _commandRuntime.BindIndirectCountBuffer(null);

    public override void MultiDrawElementsIndirect(uint drawCount, uint stride)
        => MultiDrawElementsIndirectWithOffset(drawCount, stride, 0);

    public override void MultiDrawElementsIndirectWithOffset(
        uint drawCount,
        uint stride,
        nuint byteOffset)
        => EnqueueIndirectDraw(
            "MultiDrawElementsIndirectWithOffset",
            drawCount,
            stride,
            byteOffset,
            0,
            useCount: false);

    public override void MultiDrawElementsIndirectCount(
        uint maxDrawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset)
    {
        if (!SupportsIndirectCountDraw())
        {
            Debug.VulkanWarning(
                "MultiDrawElementsIndirectCount called but VK_KHR_draw_indirect_count is not supported. Falling back to regular indirect draw.");
            MultiDrawElementsIndirectWithOffset(maxDrawCount, stride, byteOffset);
            return;
        }

        if (_commandRuntime.CommandBuffers.BoundParameterBuffer?.BufferHandle is null)
        {
            Debug.VulkanWarning(
                "MultiDrawElementsIndirectCount: No parameter (count) buffer bound. Falling back to regular indirect draw.");
            MultiDrawElementsIndirectWithOffset(maxDrawCount, stride, byteOffset);
            return;
        }

        EnqueueIndirectDraw(
            "MultiDrawElementsIndirectCount",
            maxDrawCount,
            stride,
            byteOffset,
            countByteOffset,
            useCount: true);
    }

    private void EnqueueIndirectDraw(
        string operationName,
        uint drawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset,
        bool useCount)
    {
        FrameOpContext context = CaptureFrameOpContext();
        int passIndex = EnsureValidPassIndex(
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            useCount ? "IndirectCountDraw" : "IndirectDraw",
            context.PassMetadata);
        if (_commandRuntime.TryCreateIndirectDrawOperation(
                BackendObjectContext,
                ResourceRuntime.Descriptors,
                operationName,
                passIndex,
                ResolveCurrentFrameOpDrawTarget(),
                drawCount,
                stride,
                byteOffset,
                countByteOffset,
                useCount,
                context,
                out IndirectDrawOp? operation) &&
            operation is not null)
        {
            EnqueueFrameOp(operation);
        }
    }

    public override bool SupportsIndirectCountDraw()
        => _deviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount);

    public override void ConfigureVAOAttributesForProgram(
        XRRenderProgram program,
        XRMeshRenderer.BaseVersion? version)
    {
        // Vulkan pipeline vertex-input state replaces VAO attribute mutation.
    }
}
