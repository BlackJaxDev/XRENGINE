using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>Emits the exact argument buffers whose generations authorize this key.</summary>
    private bool TryRecordPreparedIndirectDrawPayload(
        CommandBuffer commandBuffer,
        in IndirectDrawPayload payload,
        scoped in VulkanPreparedCommandChainKey preparedKey)
    {
        if (!preparedKey.IsComplete)
        {
            // Fresh-only output skips reuse-key construction and retains the
            // ordinary recording path. An incomplete proof is never reusable.
            RecordIndirectDrawPayload(commandBuffer, in payload, allowInlineBarrier: false);
            return true;
        }

        ref readonly RecordedPacketKey packet = ref VulkanPreparedCommandChainKey.GetRecordedPacketKeyReference(in preparedKey);
        VulkanRecordedBufferIdentity commands = packet.AuxiliaryBuffers.Get(0);
        if (!TryTrackPreparedIndirectArgument(commandBuffer, payload.IndirectBuffer, in commands, "IndirectDraw.Commands"))
            return false;

        Silk.NET.Vulkan.Buffer commandBufferHandle = new(commands.BufferHandle);
        if (payload.UseCount && _deviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount))
        {
            VulkanRecordedBufferIdentity count = packet.AuxiliaryBuffers.Get(1);
            if (payload.ParameterBuffer is not { } countOwner ||
                !TryTrackPreparedIndirectArgument(commandBuffer, countOwner, in count, "IndirectDraw.Count"))
                return false;
            Silk.NET.Vulkan.Buffer countBufferHandle = new(count.BufferHandle);
            if (_deviceContext.MutableCapabilities._usesCoreDrawIndirectCountCommands)
                Api!.CmdDrawIndexedIndirectCount(commandBuffer, commandBufferHandle, commands.Offset,
                    countBufferHandle, count.Offset, payload.DrawCount, payload.Stride);
            else if (DeviceContext.ExtensionFunctions.KhrDrawIndirectCount is { } extension)
                extension.CmdDrawIndexedIndirectCount(commandBuffer, commandBufferHandle, commands.Offset,
                    countBufferHandle, count.Offset, payload.DrawCount, payload.Stride);
            else
                return false;
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(true, false, 1, payload.DrawCount);
        }
        else
        {
            Api!.CmdDrawIndexedIndirect(commandBuffer, commandBufferHandle, commands.Offset, payload.DrawCount, payload.Stride);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(false, false, 1, payload.DrawCount);
        }
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(0, 1);
        return true;
    }

    private bool TryTrackPreparedIndirectArgument(
        CommandBuffer commandBuffer, VkDataBuffer owner,
        in VulkanRecordedBufferIdentity identity, string label)
    {
        if (!identity.IsBound || !identity.IsComplete || owner.BufferHandle?.Handle != identity.BufferHandle)
            return false;
        TrackCommandBufferResource(commandBuffer,
            new VulkanResourceLifetimeKey(ObjectType.Buffer, identity.BufferHandle), label,
            expectedGeneration: identity.AllocationGeneration);
        return true;
    }

    private VulkanRecordedRenderTargetSnapshot CaptureIndirectRecordingTargetSnapshot(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        XRFrameBuffer? target,
        in FrameOpContext context)
    {
        XRFrameBuffer? resolvedTarget = target ?? context.OutputFrameBuffer;
        if (resolvedTarget is not null)
            return ResourceRuntime.BackendObjects.Get(resolvedTarget) is VkFrameBuffer frameBuffer &&
                frameBuffer.TryCaptureRecordedRenderTargetSnapshot(out VulkanRecordedRenderTargetSnapshot captured)
                ? captured : default;

        // SwapchainTarget is the exact lease selected for this primary, not
        // the ambient window's current swapchain. Track both image generations.
        SwapchainRecordingTarget prepared = recordingState.SwapchainTarget;
        VulkanRecordedRenderTargetSnapshot snapshot = default;
        snapshot.Initialize(prepared.Framebuffer.Handle,
            prepared.Framebuffer.Handle == 0 ? 0UL : GetCurrentVulkanResourceGeneration(ObjectType.Framebuffer, prepared.Framebuffer.Handle),
            prepared.Extent.Width, prepared.Extent.Height, viewMask: 0u, attachmentCount: 2);
        snapshot.SetAttachment(0, new VulkanNativeAttachmentIdentity(
            prepared.Image.Handle, GetCurrentVulkanResourceGeneration(ObjectType.Image, prepared.Image.Handle),
            prepared.ImageView.Handle, GetCurrentVulkanResourceGeneration(ObjectType.ImageView, prepared.ImageView.Handle),
            ImageLayout.ColorAttachmentOptimal));
        snapshot.SetAttachment(1, new VulkanNativeAttachmentIdentity(
            prepared.DepthImage.Handle, GetCurrentVulkanResourceGeneration(ObjectType.Image, prepared.DepthImage.Handle),
            prepared.DepthView.Handle, GetCurrentVulkanResourceGeneration(ObjectType.ImageView, prepared.DepthView.Handle),
            ImageLayout.DepthStencilAttachmentOptimal));
        return snapshot;
    }

    /// <summary>Matches frozen prepared mesh binds and exact indirect argument ranges.</summary>
    private bool TryCapturePreparedIndirectBufferIdentities(
        CommandChain chain,
        in IndirectDrawPayload indirect,
        in VkMeshRenderer.IndirectDrawRecordingState state,
        out VulkanRecordedBufferIdentity indexBuffer,
        out VulkanRecordedBufferIdentityBuffer vertexBuffers,
        out VulkanRecordedBufferIdentityBuffer auxiliaryBuffers)
    {
        indexBuffer = default;
        vertexBuffers = default;
        auxiliaryBuffers = default;
        Span<VulkanRecordedBufferIdentity> vertices = stackalloc VulkanRecordedBufferIdentity[VulkanRecordedBufferIdentityBuffer.Capacity];
        Span<VulkanRecordedBufferIdentity> indices = stackalloc VulkanRecordedBufferIdentity[3];
        indirect.MeshRenderer.CaptureRecordedBufferBindings(vertices, out int vertexCount,
            out bool verticesComplete, indices, out int indexCount, out bool indicesComplete);
        if (!verticesComplete || !indicesComplete || vertexCount != state.VertexBufferCount ||
            vertexCount < 0 || vertexCount > vertices.Length || state.VertexBuffers is null || state.VertexBindings is null ||
            state.VertexBuffers.Length < vertexCount || state.VertexBindings.Length < vertexCount)
            return false;
        for (int i = 0; i < vertexCount; i++)
            if (vertices[i].BufferHandle != state.VertexBuffers[i].Handle ||
                vertices[i].Binding != state.VertexBindings[i] || !vertices[i].IsComplete)
                return false;
        int matchingIndexCount = 0;
        for (int i = 0; i < indexCount; i++)
            if (indices[i].BufferHandle == state.IndexBuffer.Handle && indices[i].IsBound && indices[i].IsComplete)
            {
                indexBuffer = indices[i];
                matchingIndexCount++;
            }
        if (matchingIndexCount != 1 || indirect.DrawCount == 0)
            return false;

        // Vertex bindings are captured in encoder order. Retain an immutable
        // previous array when exact values match; allocate overflow storage
        // only when native binding topology changes, never on a stable frame.
        ref readonly RecordedPacketKey previous = ref VulkanPreparedCommandChainKey.GetRecordedPacketKeyReference(chain.PreparedKeyReference);
        VulkanRecordedBufferIdentityBuffer oldVertices = previous.VertexBuffers;
        bool sameVertices = oldVertices.IsComplete && oldVertices.Count == vertexCount;
        for (int i = 0; sameVertices && i < vertexCount; i++)
            sameVertices = oldVertices.Get(i) == vertices[i];
        if (sameVertices)
            vertexBuffers = oldVertices;
        else
        {
            vertexBuffers.Initialize(vertexCount);
            for (int i = 0; i < vertexCount; i++)
                vertexBuffers.Set(i, vertices[i]);
        }

        ulong range = checked((ulong)(indirect.DrawCount - 1u) * indirect.Stride + 20UL);
        if (!TryCaptureIndirectArgumentIdentity(indirect.IndirectBuffer, EVulkanRecordedBufferBindingKind.Indirect,
                (ulong)indirect.ByteOffset, range, out VulkanRecordedBufferIdentity arguments))
            return false;
        auxiliaryBuffers.Initialize(indirect.UseCount ? 2 : 1);
        auxiliaryBuffers.Set(0, arguments);
        if (indirect.UseCount)
        {
            if (indirect.ParameterBuffer is not { } parameter ||
                !TryCaptureIndirectArgumentIdentity(parameter, EVulkanRecordedBufferBindingKind.IndirectCount,
                    (ulong)indirect.CountByteOffset, sizeof(uint), out VulkanRecordedBufferIdentity count))
                return false;
            auxiliaryBuffers.Set(1, count);
        }
        return vertexBuffers.IsComplete && auxiliaryBuffers.IsComplete;
    }

    private bool TryCaptureIndirectArgumentIdentity(
        VkDataBuffer buffer, EVulkanRecordedBufferBindingKind kind, ulong offset, ulong range,
        out VulkanRecordedBufferIdentity identity)
    {
        identity = default;
        if (range == 0 || offset > buffer.AllocatedByteSize || range > buffer.AllocatedByteSize - offset)
            return false;
        identity = CaptureRecordedBufferIdentity(buffer, kind, 0u, offset, range);
        return identity.IsBound && identity.IsComplete;
    }

    private bool CanReuseIndirectCommandChainSecondary(
        CommandChain chain,
        in VulkanIndirectSecondaryRecordingContract contract,
        int uniformSlot,
        bool policyAllowsReuse,
        bool preparedKeysMatch,
        scoped in VulkanRecordedCommandInheritance inheritance)
        => policyAllowsReuse && !CommandChainBenchmarkForceRerecord &&
            chain.SecondaryCommandBufferExecutable &&
            preparedKeysMatch &&
            chain.RecordedIndirectSecondaryContract ==
                contract &&
            chain.RecordedUniformSlotSignature == unchecked((ulong)(uint)uniformSlot) &&
            CommandChainSecondaryInheritanceMatches(
                chain,
                inheritance.DynamicRendering,
                inheritance.RenderPass,
                inheritance.Framebuffer,
                inheritance.DynamicRenderingFormats,
                inheritance.DepthStencilReadOnly,
                inheritance.Samples,
                inheritance.LocalReadSignature,
                inheritance.RenderingFlags);

    /// <summary>
    /// Captures exactly the pipeline, layout, and published descriptor sets
    /// chosen during indirect-draw preparation. Reuse must not substitute
    /// live renderer state after the primary has transitioned those images.
    /// </summary>
    private static VulkanPreparedCommandChainKey IncompleteIndirectKey(string reason)
    {
        if (CommandChainValidationEnabled)
            RuntimeEngine.Rendering.Stats.Vulkan.RecordIndirectSecondaryIncompleteKey(reason);
        return VulkanPreparedCommandChainKey.Incomplete;
    }

    private VulkanPreparedCommandChainKey CapturePreparedIndirectCommandChainKey(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        int opIndex,
        CommandChain chain,
        in IndirectDrawPayload indirect,
        in VkMeshRenderer.IndirectDrawRecordingState state)
    {
        // This dedicated chain may be outside the generic schedule. Capture
        // its exact resolved target from the prepared primary target authority,
        // rather than borrowing an absent or unrelated planner packet.
        ref readonly FrameOpContext context = ref recordingState.Ops.GetContext(opIndex);
        VulkanRecordedRenderTargetSnapshot nativeTarget = CaptureIndirectRecordingTargetSnapshot(
            ref recordingState, recordingState.Ops.GetTarget(opIndex), in context);
        if (!nativeTarget.IsComplete)
            return IncompleteIndirectKey("RenderTarget");

        VulkanIndirectDrawCommandIdentity commandIdentity = VulkanIndirectDrawCommandIdentity.Capture(
            in indirect, state.IndexType, state.FrameDataGeneration);
        if (!commandIdentity.IsComplete || !TryCapturePreparedIndirectBufferIdentities(
                chain, in indirect, in state, out VulkanRecordedBufferIdentity indexBuffer,
                out VulkanRecordedBufferIdentityBuffer vertexBuffers, out VulkanRecordedBufferIdentityBuffer auxiliaryBuffers))
            return IncompleteIndirectKey("PreparedBuffersOrViewport");
        if (state.Program is null ||
            state.Program.BindingId == 0u ||
            state.Program.LinkGeneration == 0UL ||
            state.Pipeline.Handle == 0UL ||
            state.PipelineLayout.Handle == 0UL)
        {
            return IncompleteIndirectKey("Program");
        }

        ulong pipelineGeneration = GetCurrentVulkanResourceGeneration(
            ObjectType.Pipeline,
            state.Pipeline.Handle);
        ulong layoutGeneration = GetCurrentVulkanResourceGeneration(
            ObjectType.PipelineLayout,
            state.PipelineLayout.Handle);
        if (pipelineGeneration == 0UL || layoutGeneration == 0UL)
            return IncompleteIndirectKey("NativePipelineGeneration");

        FrameOpSignatureHasher pipelineHash = new();
        pipelineHash.Add(state.Program.BindingId);
        pipelineHash.Add(state.Program.LinkGeneration);
        pipelineHash.Add(state.Pipeline.Handle);
        pipelineHash.Add(pipelineGeneration);
        pipelineHash.Add(state.PipelineLayout.Handle);
        pipelineHash.Add(layoutGeneration);

        bool usesDescriptorHeap = ResourceRuntime.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap;
        int descriptorSetCount = 0;
        VulkanRecordedDescriptorSetIdentityBuffer exactDescriptorSets = default;
        VulkanDescriptorHeapDrawIdentityBuffer heapDraws = default;
        DescriptorHeapBindingIdentity heapBinding = default;
        if (usesDescriptorHeap)
        {
            // Heap payloads have no descriptor-set identity. The exact
            // owner/generation root token and heap-storage identity are
            // the replacement proof; count zero is intentional here.
            exactDescriptorSets.Initialize(0);
            heapDraws.Initialize(1);
            heapDraws.Set(0, new VulkanDescriptorHeapDrawIdentity(
                state.DescriptorHeapPushDataIdentity,
                state.PushConstants));
            if (!state.DescriptorHeapPushDataIdentity.IsComplete ||
                !(heapBinding = CaptureDescriptorHeapBindingIdentity()).IsComplete)
            {
                return IncompleteIndirectKey("HeapPayloadOrBinding");
            }
        }
        else
        {
            DescriptorSet[]? descriptorSets = state.DescriptorSets;
            descriptorSetCount = descriptorSets?.Length ?? 0;
            exactDescriptorSets = CaptureRecordedDescriptorSetIdentities(descriptorSets, null);
            // Conventional push constants also contribute exact command
            // bytes. Descriptor-set identity alone cannot prove them equal.
            heapDraws.Initialize(1);
            heapDraws.Set(0, new VulkanDescriptorHeapDrawIdentity(
                DescriptorHeapPushDataIdentity.CompleteNotUsed,
                state.PushConstants));
            if (!exactDescriptorSets.IsComplete)
                return IncompleteIndirectKey("DescriptorSets");
        }

        VulkanRecordedProgramIdentityBuffer exactPrograms = default;
        exactPrograms.Initialize(1);
        exactPrograms.Set(
            0,
            new VulkanRecordedProgramIdentity(
                state.Program.BindingId,
                state.Program.LinkGeneration,
                state.PipelineLayout.Handle,
                layoutGeneration,
                state.Pipeline.Handle,
                pipelineGeneration));
        RecordedPacketKey preparedPacketKey = new(
            RenderPacketExecutionDomain.GraphicsRendering,
            nativeTarget,
            ResourcePlanSnapshot.PackRenderArea(nativeTarget.Width, nativeTarget.Height),
            context.SubmissionQueueFamily,
            exactDescriptorSets,
            exactPrograms,
            indexBuffer,
            vertexBuffers,
            auxiliaryBuffers);

        return new VulkanPreparedCommandChainKey(
            pipelineHash.ToHash(),
            ComputeRecordedDescriptorSetIdentityHash(exactDescriptorSets),
            descriptorSetCount,
            usesDescriptorHeap,
            heapBinding,
            heapDraws,
            preparedPacketKey,
            IsComplete: preparedPacketKey.IsComplete &&
                (!usesDescriptorHeap || heapDraws.IsComplete))
        {
            IndirectCommand = commandIdentity,
        };
    }
}
