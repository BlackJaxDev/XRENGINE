using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    /// <summary>Vulkan pipelines own vertex input; legacy VAO mutation has no runtime action.</summary>
    internal void ConfigureIndirectVertexInput(XRRenderProgram program, XRMeshRenderer.BaseVersion? version)
        => _ = (program, version);

    internal void BindIndirectMesh(VkMeshRenderer? mesh)
    {
        CommandBuffers.BoundMeshRendererForIndirect = mesh;
        if (mesh is null)
        {
            CommandBuffers.BoundIndexType = IndexType.Uint32;
            CommandBuffers.BoundIndexCount = 0;
            return;
        }

        mesh.Generate();
        if (mesh.TryGetPrimaryIndexBinding(out _, out IndexType indexType, out uint indexCount))
        {
            CommandBuffers.BoundIndexType = indexType;
            CommandBuffers.BoundIndexCount = indexCount;
            return;
        }

        CommandBuffers.BoundIndexType = IndexType.Uint32;
        CommandBuffers.BoundIndexCount = 0;
    }

    internal bool TryGetIndirectIndexBufferInfo(
        VkMeshRenderer? mesh,
        out IndexSize elementSize,
        out uint indexCount)
    {
        elementSize = IndexSize.FourBytes;
        indexCount = 0;
        mesh ??= CommandBuffers.BoundMeshRendererForIndirect;
        if (mesh is null)
            return false;

        bool updateBoundState = ReferenceEquals(CommandBuffers.BoundMeshRendererForIndirect, mesh);
        mesh.Generate();
        if (!mesh.TryGetPrimaryIndexBufferInfo(out elementSize, out indexCount))
        {
            if (updateBoundState)
            {
                CommandBuffers.BoundIndexType = IndexType.Uint32;
                CommandBuffers.BoundIndexCount = 0;
            }
            return false;
        }

        if (updateBoundState)
        {
            CommandBuffers.BoundIndexType = ToIndexType(elementSize);
            CommandBuffers.BoundIndexCount = indexCount;
        }
        return true;
    }

    internal bool ValidateIndirectIndexedMesh(VkMeshRenderer? mesh)
        => TryGetIndirectIndexBufferInfo(mesh, out _, out _);

    internal bool TrySyncIndirectIndexBuffer(
        XRMeshRenderer meshRenderer,
        XRDataBuffer indexBuffer,
        IndexSize elementSize)
        => meshRenderer is not null &&
           indexBuffer is not null &&
           ResourceRuntime.WrapperLookup.GetOrCreate(meshRenderer.GetDefaultVersion()) is VkMeshRenderer mesh &&
           ResourceRuntime.WrapperLookup.GetOrCreate(indexBuffer) is VkDataBuffer buffer &&
           TrySyncIndirectIndexBuffer(mesh, buffer, indexBuffer, elementSize);

    internal bool TrySyncIndirectIndexBuffer(
        VkMeshRenderer mesh,
        VkDataBuffer indexBuffer,
        XRDataBuffer source,
        IndexSize elementSize)
    {
        mesh.Generate();
        indexBuffer.Generate();
        if (!indexBuffer.IsGenerated || indexBuffer.BufferHandle is null)
        {
            return false;
        }

        bool changed = mesh.SetTriangleIndexBuffer(indexBuffer, elementSize);
        bool boundStateChanged = changed && ReferenceEquals(CommandBuffers.BoundMeshRendererForIndirect, mesh);
        if (boundStateChanged && mesh.TryGetPrimaryIndexBinding(out _, out IndexType type, out uint count))
        {
            CommandBuffers.BoundIndexType = type;
            CommandBuffers.BoundIndexCount = count;
            MarkCommandBuffersDirtyForLegacyMeshState();
        }
        return true;
    }

    internal void BindIndirectBuffer(VkDataBuffer? buffer)
        => CommandBuffers.BoundIndirectBuffer = buffer;

    internal void BindIndirectCountBuffer(VkDataBuffer? buffer)
        => CommandBuffers.BoundParameterBuffer = buffer;

    internal bool TryBindSceneDatabaseDeviceAddressUniforms(
        VulkanBackendObjectContext backendContext,
        XRRenderProgram program,
        XRDataBuffer drawMetadataBuffer,
        XRDataBuffer? instanceTransformBuffer,
        bool useInstanceTransformBuffer,
        string consumer)
        => ResourceRuntime.TryBindSceneDatabaseDeviceAddressUniforms(
            backendContext,
            ResourceRuntime.WrapperLookup,
            program,
            drawMetadataBuffer,
            instanceTransformBuffer,
            useInstanceTransformBuffer,
            consumer);

    internal bool HasBoundIndirectCountBuffer()
        => CommandBuffers.BoundParameterBuffer?.BufferHandle is not null;

    internal bool SupportsIndirectCountDraw()
        => DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount);

    internal bool TryCreateIndirectDrawOperation(
        VulkanDescriptorManager descriptors,
        string contextName,
        int passIndex,
        XRFrameBuffer? target,
        uint drawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset,
        bool useCount,
        in FrameOpContext context,
        out IndirectDrawOp? operation)
    {
        operation = null;
        VkDataBuffer? indirectBuffer = CommandBuffers.BoundIndirectBuffer;
        VkDataBuffer? parameterBuffer = CommandBuffers.BoundParameterBuffer;
        if (indirectBuffer?.BufferHandle is null ||
            CommandBuffers.BoundMeshRendererForIndirect is null ||
            CommandBuffers.BoundIndexCount == 0 ||
            useCount && parameterBuffer?.BufferHandle is null)
        {
            return false;
        }

        if (!TryCaptureIndirectDrawPayload(
                contextName,
                target,
                out VkMeshRenderer mesh,
                out PendingMeshDraw draw))
        {
            return false;
        }

        operation = IndirectDrawOp.Rent(
            passIndex,
            target,
            indirectBuffer,
            parameterBuffer,
            mesh,
            draw,
            drawCount,
            stride,
            byteOffset,
            countByteOffset,
            useCount,
            descriptors.CaptureGlobalMaterialTextureDescriptorBindingForNextFrameOp(),
            context,
            CaptureIndirectSecondaryRecordingContract(
                indirectBuffer,
                useCount ? parameterBuffer : null,
                drawCount,
                stride,
                byteOffset,
                countByteOffset,
                useCount));
        return true;
    }

    private bool TryCaptureIndirectDrawPayload(
        string contextName,
        XRFrameBuffer? target,
        out VkMeshRenderer mesh,
        out PendingMeshDraw draw)
    {
        mesh = CommandBuffers.BoundMeshRendererForIndirect!;
        draw = default;
        if (CommandBuffers.PendingIndirectDrawState is not { } state)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.IndirectDrawStateMissing.{contextName}",
                TimeSpan.FromSeconds(1),
                "{0}: No Vulkan indirect draw state was pushed before indirect submission.",
                contextName);
            return false;
        }

        if (ResourceRuntime.WrapperLookup.GetOrCreate(state.Program) is not VkRenderProgram program ||
            !program.IsLinked && !program.Link())
        {
            Debug.VulkanWarning("{0}: Vulkan indirect draw program '{1}' is unavailable or not linked.", contextName, state.Program.Name ?? "<unnamed>");
            return false;
        }

        ComputeDispatchSnapshot bindings = program.CaptureComputeSnapshot();
        string programIdentity = state.Program.Name ?? program.GetHashCode().ToString();
        if (mesh.TryCreatePreparedIndirectDrawSnapshot(
                state.Material,
                program,
                programIdentity,
                program.LinkGeneration,
                bindings,
                state.ModelMatrix,
                target,
                out draw,
                out string reason))
        {
            return true;
        }

        Debug.VulkanWarningEvery(
            $"Vulkan.IndirectDrawSnapshotFailed.{contextName}.{reason}",
            TimeSpan.FromSeconds(2),
            "{0}: Failed to capture indirect draw state for program '{1}' material '{2}': {3}. {4}",
            contextName,
            state.Program.Name ?? "<unnamed program>",
            state.Material.Name ?? "<unnamed material>",
            reason,
            mesh.LastPrepareDetail);
        return false;
    }

    /// <summary>Captures and queues a frozen indirect draw without retaining renderer state.</summary>
    internal void EnqueueIndirectDraw(
        VulkanFrameOperationQueue queue,
        string operationName,
        uint drawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset,
        bool useCount,
        int currentPassIndex,
        in FrameOpContext context)
    {
        int passIndex = EnsureValidPassIndex(
            currentPassIndex,
            useCount ? "IndirectCountDraw" : "IndirectDraw",
            context.PassMetadata);
        if (!TryCreateIndirectDrawOperation(
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
                out IndirectDrawOp? operation) ||
            operation is null)
        {
            return;
        }

        EnqueueFrameOperation(queue, operation, passIndex);
    }

    internal void EnqueueIndirectCountDraw(
        VulkanFrameOperationQueue queue,
        uint maxDrawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset,
        int currentPassIndex,
        in FrameOpContext context)
    {
        if (!SupportsIndirectCountDraw())
        {
            Debug.VulkanWarning(
                "MultiDrawElementsIndirectCount called but VK_KHR_draw_indirect_count is not supported. Falling back to regular indirect draw.");
            EnqueueIndirectDraw(queue, "MultiDrawElementsIndirectWithOffset", maxDrawCount, stride, byteOffset, 0, false, currentPassIndex, context);
            return;
        }

        if (!HasBoundIndirectCountBuffer())
        {
            Debug.VulkanWarning(
                "MultiDrawElementsIndirectCount: No parameter (count) buffer bound. Falling back to regular indirect draw.");
            EnqueueIndirectDraw(queue, "MultiDrawElementsIndirectWithOffset", maxDrawCount, stride, byteOffset, 0, false, currentPassIndex, context);
            return;
        }

        EnqueueIndirectDraw(queue, "MultiDrawElementsIndirectCount", maxDrawCount, stride, byteOffset, countByteOffset, true, currentPassIndex, context);
    }

    private static IndexType ToIndexType(IndexSize size)
        => size switch
        {
            IndexSize.Byte => IndexType.Uint8Ext,
            IndexSize.TwoBytes => IndexType.Uint16,
            IndexSize.FourBytes => IndexType.Uint32,
            _ => IndexType.Uint32,
        };
}
