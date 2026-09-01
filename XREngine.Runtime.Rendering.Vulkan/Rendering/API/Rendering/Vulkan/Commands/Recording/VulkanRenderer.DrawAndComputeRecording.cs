using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanCommandRuntime
    {
        private unsafe void RecordIndirectDrawOp(CommandBuffer commandBuffer, IndirectDrawOp op, bool allowInlineBarrier = true)
        {
            Silk.NET.Vulkan.Buffer indirectBuffer = RequirePreparedBuffer(op.IndirectBuffer, "indirect draw command");

            if (allowInlineBarrier)
            {
                MemoryBarrier memoryBarrier = new()
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.IndirectCommandReadBit,
                };

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                    PipelineStageFlags.DrawIndirectBit,
                    DependencyFlags.None,
                    1,
                    &memoryBarrier,
                    0,
                    null,
                    0,
                    null);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 1, redundantCount: 0);
            }
            else
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 0, redundantCount: 1);
            }

            // Calculate the byte offset into the indirect buffer
            ulong bufferOffset = op.ByteOffset;
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                indirectBuffer.Handle,
                "IndirectDraw.Commands");

            if (IndirectTraceEnabled)
            {
                Debug.Vulkan(
                    "[VulkanIndirect] pass={0} passName='{1}' target='{2}' targetId={3} indirect=0x{4:X} parameter=0x{5:X} offset={6} countOffset={7} stride={8} maxDraws={9} useCount={10} renderer={11} material='{12}' program='{13}'",
                    op.PassIndex,
                    ResolvePassName(op.Context.PassMetadata, op.PassIndex),
                    op.Target?.Name ?? "<swapchain>",
                    op.Target?.GetHashCode() ?? 0,
                    indirectBuffer.Handle,
                    op.ParameterBuffer?.BufferHandle?.Handle ?? 0UL,
                    op.ByteOffset,
                    op.CountByteOffset,
                    op.Stride,
                    op.DrawCount,
                    op.UseCount,
                    op.MeshRenderer.MeshRenderer.Name ?? op.MeshRenderer.GetHashCode().ToString(),
                    (op.Draw.MaterialOverride ?? op.MeshRenderer.MeshRenderer.Material)?.Name ?? "<unnamed material>",
                    op.Draw.PreparedProgramIdentity ?? "<no program>");
            }

            if (op.DrawCount == 0)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.Indirect.ZeroDrawCount",
                    TimeSpan.FromSeconds(1),
                    "RecordIndirectDrawOp skipped: drawCount was zero.");
                return;
            }

            if (op.BindlessMaterialTextures is { } bindlessMaterialTextures &&
                !TryBindPreparedGlobalMaterialTextureDescriptorSet(
                    commandBuffer,
                    bindlessMaterialTextures.Program,
                    bindlessMaterialTextures.Consumer,
                    op.Draw.ProgramBindingSnapshot?.MaterialTablePublication))
            {
                return;
            }

            if (op.UseCount && _deviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount))
            {
                VkDataBuffer parameterResource = op.ParameterBuffer ?? throw new VulkanPlanPreconditionException(
                    "The prepared indirect-count draw no longer has a parameter buffer.");
                Silk.NET.Vulkan.Buffer parameterBuffer = RequirePreparedBuffer(parameterResource, "indirect draw count");

                // The parameter buffer contains the draw count at offset 0 (uint)
                TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.Buffer,
                    parameterBuffer.Handle,
                    "IndirectDraw.Count");
                if (_deviceContext.MutableCapabilities._usesCoreDrawIndirectCountCommands)
                {
                    Api!.CmdDrawIndexedIndirectCount(
                        commandBuffer,
                        indirectBuffer,
                        bufferOffset,
                        parameterBuffer,
                        (ulong)op.CountByteOffset,
                        op.DrawCount,
                        op.Stride);
                }
                else if (DeviceContext.ExtensionFunctions.KhrDrawIndirectCount is { } drawIndirectCount)
                {
                    drawIndirectCount.CmdDrawIndexedIndirectCount(
                        commandBuffer,
                        indirectBuffer,
                        bufferOffset,
                        parameterBuffer,
                        (ulong)op.CountByteOffset,
                        op.DrawCount,
                        op.Stride);
                }
                else
                {
                    Debug.VulkanWarning("RecordIndirectDrawOp: Indirect-count support was published without a core or KHR command entry point.");
                    return;
                }

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(
                    usedCountPath: true,
                    usedLoopFallback: false,
                    apiCalls: 1,
                    submittedDraws: op.DrawCount);
            }
            else
            {
                // Prefer contiguous multi-draw in the non-count path.
                Api!.CmdDrawIndexedIndirect(
                    commandBuffer,
                    indirectBuffer,
                    bufferOffset,
                    op.DrawCount,
                    op.Stride);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(
                    usedCountPath: false,
                    usedLoopFallback: false,
                    apiCalls: 1,
                    submittedDraws: op.DrawCount);
            }
        }

        internal void RecordTransformFeedbackOp(CommandBuffer commandBuffer, TransformFeedbackOp op)
        {
            switch (op.Operation)
            {
                case EXRTransformFeedbackOperation.BindBuffer:
                    op.TransformFeedback.BindFeedbackBuffer(
                        commandBuffer,
                        op.FeedbackBufferOffset,
                        op.FeedbackBufferSize ?? Vk.WholeSize);
                    break;

                case EXRTransformFeedbackOperation.Begin:
                    if (op.CounterBuffer is null)
                    {
                        op.TransformFeedback.Begin(commandBuffer);
                    }
                    else if (TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? beginCounter))
                    {
                        op.TransformFeedback.Begin(commandBuffer, beginCounter, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset);
                    }
                    break;

                case EXRTransformFeedbackOperation.End:
                    if (op.CounterBuffer is null)
                    {
                        op.TransformFeedback.End(commandBuffer);
                    }
                    else if (TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? endCounter))
                    {
                        op.TransformFeedback.End(commandBuffer, endCounter, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset);
                    }
                    break;

                case EXRTransformFeedbackOperation.Pause:
                    if (!TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? pauseCounter))
                    {
                        Debug.VulkanWarning("Transform feedback pause skipped: Vulkan pause/resume requires a counter buffer.");
                        return;
                    }

                    op.TransformFeedback.End(commandBuffer, pauseCounter, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset);
                    break;

                case EXRTransformFeedbackOperation.Resume:
                    if (!TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? resumeCounter))
                    {
                        Debug.VulkanWarning("Transform feedback resume skipped: Vulkan pause/resume requires a counter buffer.");
                        return;
                    }

                    op.TransformFeedback.Begin(commandBuffer, resumeCounter, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset);
                    break;

                case EXRTransformFeedbackOperation.DrawIndirectByteCount:
                    if (!TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? drawCounter))
                    {
                        Debug.VulkanWarning("Transform feedback byte-count draw skipped: missing counter buffer.");
                        return;
                    }

                    op.TransformFeedback.DrawIndirectByteCount(
                        commandBuffer,
                        op.InstanceCount,
                        op.FirstInstance,
                        drawCounter,
                        op.CounterBufferOffset,
                        op.CounterOffset,
                        op.VertexStride);
                    break;

                default:
                    Debug.VulkanWarning($"Unsupported Vulkan transform feedback operation '{op.Operation}'.");
                    break;
            }
        }

        private bool TryResolveTransformFeedbackBuffer(
            XRDataBuffer? dataBuffer,
            string role,
            [NotNullWhen(true)] out VkDataBuffer? buffer)
        {
            buffer = null;
			if (dataBuffer is null)
				return false;

            if (ResourceRuntime.BackendObjects.Get(dataBuffer) is not VkDataBuffer vkBuffer ||
                vkBuffer.BufferHandle is not { } publishedBuffer ||
                publishedBuffer.Handle == 0)
            {
                throw new VulkanPlanPreconditionException(
                    $"The prepared transform-feedback {role} buffer is missing its published Vulkan buffer.");
            }

            buffer = vkBuffer;
            return true;
        }

        private void RecordIndirectDrawPayload(CommandBuffer commandBuffer, in IndirectDrawPayload op, bool allowInlineBarrier = true)
        {
            Silk.NET.Vulkan.Buffer indirectBuffer = RequirePreparedBuffer(op.IndirectBuffer, "indirect draw command");
            if (allowInlineBarrier) EmitIndirectReadBarrier(commandBuffer); else RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(0, 1);
            if (op.DrawCount == 0) return;
            if (op.BindlessMaterialTextures is { } binding && !TryBindPreparedGlobalMaterialTextureDescriptorSet(commandBuffer, binding.Program, binding.Consumer, op.Draw.ProgramBindingSnapshot?.MaterialTablePublication)) return;
            TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, indirectBuffer.Handle, "IndirectDraw.Commands");
            if (op.UseCount && _deviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount))
            {
                VkDataBuffer countOwner = op.ParameterBuffer ?? throw new VulkanPlanPreconditionException("The prepared indirect-count draw no longer has a parameter buffer.");
                Silk.NET.Vulkan.Buffer count = RequirePreparedBuffer(countOwner, "indirect draw count");
                TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, count.Handle, "IndirectDraw.Count");
                if (_deviceContext.MutableCapabilities._usesCoreDrawIndirectCountCommands) Api!.CmdDrawIndexedIndirectCount(commandBuffer, indirectBuffer, (ulong)op.ByteOffset, count, (ulong)op.CountByteOffset, op.DrawCount, op.Stride);
                else if (DeviceContext.ExtensionFunctions.KhrDrawIndirectCount is { } ext) ext.CmdDrawIndexedIndirectCount(commandBuffer, indirectBuffer, (ulong)op.ByteOffset, count, (ulong)op.CountByteOffset, op.DrawCount, op.Stride);
                else return;
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(true, false, 1, op.DrawCount);
            }
            else { Api!.CmdDrawIndexedIndirect(commandBuffer, indirectBuffer, (ulong)op.ByteOffset, op.DrawCount, op.Stride); RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(false, false, 1, op.DrawCount); }
        }

        private unsafe void EmitIndirectReadBarrier(CommandBuffer commandBuffer)
        {
            MemoryBarrier barrier = new() { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.IndirectCommandReadBit };
            CmdPipelineBarrierTracked(commandBuffer, PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit, PipelineStageFlags.DrawIndirectBit, DependencyFlags.None, 1, &barrier, 0, null, 0, null);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(1, 0);
        }

        internal void RecordTransformFeedbackPayload(CommandBuffer commandBuffer, in TransformFeedbackPayload op)
        {
            switch (op.Operation)
            {
                case EXRTransformFeedbackOperation.BindBuffer: op.TransformFeedback.BindFeedbackBuffer(commandBuffer, op.FeedbackBufferOffset, op.FeedbackBufferSize ?? Vk.WholeSize); break;
                case EXRTransformFeedbackOperation.Begin: if (op.CounterBuffer is null) op.TransformFeedback.Begin(commandBuffer); else if (TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? b)) op.TransformFeedback.Begin(commandBuffer, b, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset); break;
                case EXRTransformFeedbackOperation.End: if (op.CounterBuffer is null) op.TransformFeedback.End(commandBuffer); else if (TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? e)) op.TransformFeedback.End(commandBuffer, e, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset); break;
                case EXRTransformFeedbackOperation.Pause: if (TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? p)) op.TransformFeedback.End(commandBuffer, p, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset); break;
                case EXRTransformFeedbackOperation.Resume: if (TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? r)) op.TransformFeedback.Begin(commandBuffer, r, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset); break;
                case EXRTransformFeedbackOperation.DrawIndirectByteCount: if (TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? d)) op.TransformFeedback.DrawIndirectByteCount(commandBuffer, op.InstanceCount, op.FirstInstance, d, op.CounterBufferOffset, op.CounterOffset, op.VertexStride); break;
            }
        }

        internal unsafe void RecordMeshTaskDispatchIndirectCountOp(CommandBuffer commandBuffer, MeshTaskDispatchIndirectCountOp op)
        {
            if (!DeviceContext.SupportsMeshTaskIndirectCount ||
                DeviceContext.ExtensionFunctions.ExtMeshShader is not { } meshShader)
            {
                Debug.VulkanWarning("RecordMeshTaskDispatchIndirectCountOp: {0}", DeviceContext.MeshletDispatchStatus);
                return;
            }

            Silk.NET.Vulkan.Buffer indirectBuffer = RequirePreparedBuffer(op.IndirectBuffer, "mesh-task indirect command");
            Silk.NET.Vulkan.Buffer countBuffer = RequirePreparedBuffer(op.CountBuffer, "mesh-task indirect count");

            if (op.MaxDrawCount == 0u)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.MeshTaskIndirect.ZeroMaxDrawCount",
                    TimeSpan.FromSeconds(1),
                    "RecordMeshTaskDispatchIndirectCountOp skipped: maxDrawCount was zero.");
                return;
            }

            if (op.BindlessMaterialTextures is { } bindlessMaterialTextures &&
                !TryBindPreparedGlobalMaterialTextureDescriptorSet(
                    commandBuffer,
                    bindlessMaterialTextures.Program,
                    bindlessMaterialTextures.Consumer,
                    op.ProgramBindingSnapshot.MaterialTablePublication))
            {
                return;
            }

            MemoryBarrier memoryBarrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
                // The compute expansion writes both the indirect/count pair and
                // the task-record stream consumed by the EXT task shader.
                DstAccessMask = AccessFlags.IndirectCommandReadBit | AccessFlags.ShaderReadBit,
            };

            PipelineStageFlags destinationStages = PipelineStageFlags.DrawIndirectBit;
            if (DeviceContext.SupportsMeshTaskIndirectCount)
            {
                destinationStages |= PipelineStageFlags.TaskShaderBitExt |
                    PipelineStageFlags.MeshShaderBitExt;
            }

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                destinationStages,
                DependencyFlags.None,
                1,
                &memoryBarrier,
                0,
                null,
                0,
                null);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 1, redundantCount: 0);

            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                indirectBuffer.Handle,
                "MeshTaskIndirect.Commands");
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                countBuffer.Handle,
                "MeshTaskIndirect.Count");
            meshShader.CmdDrawMeshTasksIndirectCount(
                commandBuffer,
                indirectBuffer,
                (ulong)op.ByteOffset,
                countBuffer,
                (ulong)op.CountByteOffset,
                op.MaxDrawCount,
                op.Stride);

            RecordGpuMeshletDispatchEmission();

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(
                usedCountPath: true,
                usedLoopFallback: false,
                apiCalls: 1,
                submittedDraws: op.MaxDrawCount);
        }

        internal unsafe void RecordMeshTaskDispatchIndirectCountPayload(CommandBuffer commandBuffer, uint imageIndex, in MeshTaskDispatchIndirectCountPayload op)
        {
            if (!DeviceContext.SupportsMeshTaskIndirectCount || DeviceContext.ExtensionFunctions.ExtMeshShader is not { } meshShader)
                return;
            string? bindingFailure = null;
            if (!op.Program.IsLinked || op.Program.LinkGeneration != op.ProgramLinkGeneration ||
                op.Program.PipelineLayout.Handle == 0 ||
                !op.Program.ValidateComputeSnapshot(op.ProgramBindingSnapshot, out bindingFailure))
            {
                throw new VulkanPlanPreconditionException(
                    $"Prepared mesh-task graphics state became invalid before recording: {bindingFailure ?? "program link or layout changed"}.");
            }
            if (op.Pipeline.Handle == 0)
                throw new VulkanPlanPreconditionException("Prepared mesh-task graphics pipeline became unavailable before recording.");
            if (op.BindlessMaterialTextures is { } capturedMaterialBinding)
            {
                if (!ReferenceEquals(capturedMaterialBinding.Program, op.Program))
                {
                    throw new VulkanPlanPreconditionException(
                        "The sealed mesh-task material descriptor scope belongs to a different program.");
                }
            }
            else if (op.Program.HasGlobalTextureArrayOnlySet)
            {
                throw new VulkanPlanPreconditionException(
                    "The mesh-task program requires the shared global material descriptor set, but no sealed binding was captured.");
            }
            BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, op.Pipeline);
            if (op.ProducerSnapshot.IndexedViewportScissors.Count > 0)
                SetViewportScissorTracked(commandBuffer, op.ProducerSnapshot.IndexedViewportScissors.Viewports!, op.ProducerSnapshot.IndexedViewportScissors.Scissors!, op.ProducerSnapshot.IndexedViewportScissors.Count);
            else
                SetViewportScissorTracked(commandBuffer, op.ProducerSnapshot.Viewport, op.ProducerSnapshot.Scissor);
            if (!op.Program.TryBuildAndBindComputeDescriptorSets(
                    CreateProgramRecordingRequest(commandBuffer),
                    imageIndex,
                    op.ProgramBindingSnapshot,
                    op.ProgramBindingSnapshot.ComputeReusableDescriptorBindingKey(),
                    PipelineBindPoint.Graphics,
                    out _,
                    out _,
                    out var meshTaskTemporaryBuffers,
                    excludeGlobalTextureArray: op.BindlessMaterialTextures is not null))
            {
                foreach ((Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) in meshTaskTemporaryBuffers)
                    DestroyBuffer(buffer, memory);
                throw new VulkanPlanPreconditionException("Prepared mesh-task graphics descriptors became unavailable before recording.");
            }
            RegisterComputeTransientUniformBuffers(imageIndex, meshTaskTemporaryBuffers);
            Silk.NET.Vulkan.Buffer indirect = RequirePreparedBuffer(op.IndirectBuffer, "mesh-task indirect command");
            Silk.NET.Vulkan.Buffer count = RequirePreparedBuffer(op.CountBuffer, "mesh-task indirect count");
            if (op.MaxDrawCount == 0u ||
                (op.BindlessMaterialTextures is { } materialBinding &&
                 !TryBindPreparedGlobalMaterialTextureDescriptorSet(
                     commandBuffer,
                     materialBinding.Program,
                     materialBinding.Consumer,
                     op.ProgramBindingSnapshot.MaterialTablePublication)))
            {
                return;
            }
            TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, indirect.Handle, "MeshTaskIndirect.Commands"); TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, count.Handle, "MeshTaskIndirect.Count");
            meshShader.CmdDrawMeshTasksIndirectCount(commandBuffer, indirect, (ulong)op.ByteOffset, count, (ulong)op.CountByteOffset, op.MaxDrawCount, op.Stride);
            RecordGpuMeshletDispatchEmission();
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(true, false, 1, op.MaxDrawCount);
        }

        /// <summary>
        /// Publishes meshlet-production evidence only after the Vulkan command has
        /// been emitted. Enqueue and plan admission can still fail before this point.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecordGpuMeshletDispatchEmission()
        {
            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletProductionFrame(1);
            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletDispatch(groups: 0u);
        }

        internal void RecordComputeDispatchPayload(
            CommandBuffer commandBuffer,
            uint imageIndex,
            in ComputeDispatchPayload payload,
            ulong reusableDescriptorKey,
            bool allowSynchronousResourceUploads = true)
        {
            Pipeline pipeline = payload.Program.ComputePipeline;
            if (pipeline.Handle == 0)
                throw new InvalidOperationException($"Compute pipeline '{payload.Program.Data.Name ?? "UnnamedProgram"}' became unavailable after enqueue.");

            BindPipelineTracked(commandBuffer, PipelineBindPoint.Compute, pipeline);
            EnsureComputeStorageImageLayoutsForDispatch(
                commandBuffer,
                payload.Snapshot,
                allowSynchronousResourceUploads);
            PushConstantsTracked(
                commandBuffer,
                payload.Program.PipelineLayout,
                VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
                    _deviceContext),
                0,
                new ComputeDispatchPushConstants(
                    payload.GroupsX,
                    payload.GroupsY,
                    payload.GroupsZ,
                    0u));
            if (!payload.Program.TryBuildAndBindComputeDescriptorSets(CreateProgramRecordingRequest(commandBuffer), imageIndex, payload.Snapshot, reusableDescriptorKey, PipelineBindPoint.Compute, out _, out DescriptorSet[] boundDescriptorSets, out var tempBuffers, allowSynchronousResourceUploads: allowSynchronousResourceUploads))
            {
                foreach ((Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) in tempBuffers) DestroyBuffer(buffer, memory);
                throw new VulkanPlanPreconditionException($"Prepared compute descriptors became unavailable while recording '{payload.Program.Data.Name ?? "UnnamedProgram"}'.");
            }

            _commandBufferRecordingScratch.Value!.PreparedComputePayload = new VulkanPreparedComputePayload(boundDescriptorSets);
            RegisterComputeTransientUniformBuffers(imageIndex, tempBuffers);
            Api!.CmdDispatch(commandBuffer, payload.GroupsX, payload.GroupsY, payload.GroupsZ);
        }

        /// <summary>
        /// Establishes the exact sampled-image layouts required by an inline
        /// primary compute dispatch. Compute work executes outside a rendering
        /// scope, so an image that was just used as an attachment must not be
        /// treated as an overlapping active attachment here.
        /// </summary>
        private void EnsureComputeSampledImageLayoutsForDispatch(
            CommandBuffer commandBuffer,
            ComputeDispatchSnapshot snapshot,
            bool allowSynchronousResourceUploads = true)
        {
            foreach (XRTexture texture in snapshot.Samplers.Values)
                TransitionComputeDescriptorTextureForSampling(
                    commandBuffer,
                    texture,
                    snapshot,
                    allowSynchronousResourceUploads);

            foreach (XRTexture texture in snapshot.SamplersByName.Values)
                TransitionComputeDescriptorTextureForSampling(
                    commandBuffer,
                    texture,
                    snapshot,
                    allowSynchronousResourceUploads);
        }

        /// <summary>
        /// Transitions a compute sampler without collapsing mixed per-mip state
        /// when the same image is also bound for storage. Reduction chains read
        /// one completed mip while writing the next; treating their full sampled
        /// view as one layout range can fall back to an Undefined old layout and
        /// discard already-produced mip contents.
        /// </summary>
        private void TransitionComputeDescriptorTextureForSampling(
            CommandBuffer commandBuffer,
            XRTexture texture,
            ComputeDispatchSnapshot snapshot,
            bool allowSynchronousResourceUploads)
        {
            if (ResourceRuntime.BackendObjects.Get(texture) is not IVkImageDescriptorSource source)
            {
                if (!allowSynchronousResourceUploads)
                    throw new VulkanPlanPreconditionException(
                        $"Sampled compute image '{texture.Name ?? "<unnamed>"}' has no published Vulkan descriptor source.");
                TransitionDescriptorTextureForSampling(
                    commandBuffer,
                    texture,
                    target: null,
                    passIndex: 0,
                    passMetadata: null);
                return;
            }

            if (!allowSynchronousResourceUploads)
            {
                if (!source.TryGetDescriptorSnapshot(
                        requestedViewType: null,
                        requestedAspectMask: null,
                        reason: "sampled compute image transition",
                        allowSynchronousUpload: false,
                        out VkImageDescriptorSnapshot publishedSnapshot) ||
                    !TryGetDescriptorHeapImageViewCreateInfo(
                        publishedSnapshot.View,
                        out ImageViewCreateInfo publishedViewInfo) ||
                    publishedViewInfo.Image.Handle != publishedSnapshot.Image.Handle)
                {
                    throw new VulkanPlanPreconditionException(
                        $"Sampled compute image '{texture.Name ?? "<unnamed>"}' has no registered published view tuple.");
                }

                ImageLayout publishedLayout = ResourceRuntime.Descriptors
                    .ResolveDescriptorImageLayout(
                        source,
                        in publishedSnapshot,
                        DescriptorType.CombinedImageSampler);
                ImageSubresourceRange publishedRange = publishedViewInfo.SubresourceRange;
                ImageAspectFlags publishedAspect = NormalizeBarrierAspectMask(
                    publishedSnapshot.Format,
                    publishedRange.AspectMask);
                uint publishedMipLevels = Math.Max(publishedRange.LevelCount, 1u);
                uint publishedArrayLayers = Math.Max(publishedRange.LayerCount, 1u);
                for (uint mipOffset = 0; mipOffset < publishedMipLevels; mipOffset++)
                for (uint layerOffset = 0; layerOffset < publishedArrayLayers; layerOffset++)
                {
                    ImageSubresourceRange range = new()
                    {
                        AspectMask = publishedAspect,
                        BaseMipLevel = publishedRange.BaseMipLevel + mipOffset,
                        LevelCount = 1u,
                        BaseArrayLayer = publishedRange.BaseArrayLayer + layerOffset,
                        LayerCount = 1u,
                    };
                    TransitionDescriptorImageRangeForSampling(
                        commandBuffer, publishedSnapshot.Image, range,
                        publishedLayout, target: null, passIndex: 0,
                        passMetadata: null);
                }

                return;
            }

            Image descriptorImage = source.DescriptorImage;
            if (descriptorImage.Handle == 0 ||
                !ComputeSnapshotWritesImage(
                    snapshot,
                    descriptorImage,
                    allowSynchronousResourceUploads: true))
            {
                TransitionDescriptorTextureForSampling(
                    commandBuffer,
                    texture,
                    target: null,
                    passIndex: 0,
                    passMetadata: null);
                return;
            }

            ImageLayout targetLayout = ResourceRuntime.Descriptors.ResolveDescriptorImageLayout(
                source, DescriptorType.CombinedImageSampler);
            ImageView descriptorView = source.DescriptorView;
            if (!TryGetDescriptorHeapImageViewCreateInfo(
                    descriptorView,
                    out ImageViewCreateInfo sampledViewInfo) ||
                sampledViewInfo.Image.Handle != descriptorImage.Handle)
            {
                TransitionDescriptorTextureForSampling(
                    commandBuffer,
                    texture,
                    target: null,
                    passIndex: 0,
                    passMetadata: null);
                return;
            }

            ImageSubresourceRange sampledRange = sampledViewInfo.SubresourceRange;
            ImageAspectFlags aspect = NormalizeBarrierAspectMask(
                source.DescriptorFormat,
                sampledRange.AspectMask);
            uint mipLevels = Math.Max(sampledRange.LevelCount, 1u);
            uint arrayLayers = Math.Max(sampledRange.LayerCount, 1u);
            for (uint mipOffset = 0; mipOffset < mipLevels; mipOffset++)
            for (uint layerOffset = 0; layerOffset < arrayLayers; layerOffset++)
            {
                ImageSubresourceRange range = new()
                {
                    AspectMask = aspect,
                    BaseMipLevel = sampledRange.BaseMipLevel + mipOffset,
                    LevelCount = 1u,
                    BaseArrayLayer = sampledRange.BaseArrayLayer + layerOffset,
                    LayerCount = 1u,
                };
                TransitionDescriptorImageRangeForSampling(
                    commandBuffer,
                    descriptorImage,
                    range,
                    targetLayout,
                    target: null,
                    passIndex: 0,
                    passMetadata: null);
            }
        }

        private bool ComputeSnapshotWritesImage(
            ComputeDispatchSnapshot snapshot,
            Image sampledImage,
            bool allowSynchronousResourceUploads)
        {
            foreach (ProgramImageBinding binding in snapshot.Images.Values)
            {
                if (ResourceRuntime.BackendObjects.Get(binding.Texture) is not
                    IVkImageDescriptorSource storageSource)
                    continue;
                VkImageDescriptorSnapshot storageSnapshot = default;
                if (!allowSynchronousResourceUploads &&
                    !storageSource.TryGetDescriptorSnapshot(
                        requestedViewType: null,
                        requestedAspectMask: null,
                        reason: "sampled compute storage overlap",
                        allowSynchronousUpload: false,
                        out storageSnapshot))
                {
                    throw new VulkanPlanPreconditionException(
                        "Compute storage-image overlap has no published descriptor tuple.");
                }

                Image storageImage = allowSynchronousResourceUploads
                    ? storageSource.DescriptorImage
                    : storageSnapshot.Image;
                if (storageImage.Handle == sampledImage.Handle)
                {
                    return true;
                }
            }

            return false;
        }

        private unsafe void EnsureComputeStorageImageLayoutsForDispatch(
            CommandBuffer commandBuffer,
            ComputeDispatchSnapshot snapshot,
            bool allowSynchronousResourceUploads = true)
        {
            foreach (ProgramImageBinding binding in snapshot.Images.Values)
            {
                XRTexture texture = binding.Texture;
                if (texture is null)
                    continue;

                if (ResourceRuntime.BackendObjects.Get(texture) is not IVkImageDescriptorSource source)
                    throw new VulkanPlanPreconditionException(
                        $"Compute storage image '{texture.Name ?? "<unnamed>"}' has no prepared Vulkan image wrapper.");

                VkImageDescriptorSnapshot publishedSnapshot = default;
                if (!allowSynchronousResourceUploads &&
                    !source.TryGetDescriptorSnapshot(
                        requestedViewType: null,
                        requestedAspectMask: null,
                        reason: "compute storage image layout",
                        allowSynchronousUpload: false,
                        out publishedSnapshot))
                {
                    throw new VulkanPlanPreconditionException(
                        $"Compute storage image '{texture.Name ?? "<unnamed>"}' has no published descriptor snapshot.");
                }

                if ((!allowSynchronousResourceUploads && !publishedSnapshot.UsesAllocatorImage) ||
                    (allowSynchronousResourceUploads && !source.UsesAllocatorImage))
                    continue;

                ImageUsageFlags usage = allowSynchronousResourceUploads
                    ? source.DescriptorUsage
                    : publishedSnapshot.Usage;
                if ((usage & ImageUsageFlags.StorageBit) == 0)
                    continue;

                uint mipLevels = allowSynchronousResourceUploads
                    ? Math.Max(source.DescriptorMipLevels, 1u)
                    : Math.Max(publishedSnapshot.MipLevels, 1u);
                uint arrayLayers = allowSynchronousResourceUploads
                    ? Math.Max(source.DescriptorArrayLayers, 1u)
                    : Math.Max(publishedSnapshot.ArrayLayers, 1u);
                uint baseMipLevel = binding.Level < 0 ? 0u : Math.Min((uint)binding.Level, mipLevels - 1u);
                uint baseArrayLayer = binding.Layered || binding.Layer < 0 ? 0u : Math.Min((uint)binding.Layer, arrayLayers - 1u);
                uint layerCount = binding.Layered || binding.Layer < 0 ? arrayLayers - baseArrayLayer : 1u;
                Image image = allowSynchronousResourceUploads
                    ? source.DescriptorImage
                    : publishedSnapshot.Image;
                if (image.Handle == 0)
                    throw new VulkanPlanPreconditionException(
                        $"Compute storage image '{texture.Name ?? "<unnamed>"}' has no published Vulkan image handle.");

                ImageAspectFlags aspect = allowSynchronousResourceUploads
                    ? source.DescriptorAspect
                    : publishedSnapshot.Aspect;
                if (aspect == 0)
                    aspect = ImageAspectFlags.ColorBit;

                ImageSubresourceRange range = new()
                {
                    AspectMask = aspect,
                    BaseMipLevel = baseMipLevel,
                    LevelCount = 1u,
                    BaseArrayLayer = baseArrayLayer,
                    LayerCount = Math.Max(layerCount, 1u)
                };

                ImageLayout oldLayout = TryGetRecordedImageAccessState(
                    commandBuffer,
                    image,
                    range,
                    out VulkanImageAccessState recordedState)
                        ? recordedState.Layout
                        : ImageLayout.Undefined;
                if (oldLayout == ImageLayout.General)
                    continue;

                ImageMemoryBarrier barrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = ResolveComputeStorageImageSourceAccess(oldLayout),
                    DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                    OldLayout = oldLayout,
                    NewLayout = ImageLayout.General,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image,
                    SubresourceRange = range
                };

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    ResolveComputeStorageImageSourceStage(oldLayout),
                    PipelineStageFlags.ComputeShaderBit,
                    DependencyFlags.None,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        private static PipelineStageFlags ResolveComputeStorageImageSourceStage(ImageLayout layout)
            => layout switch
            {
                ImageLayout.Undefined => PipelineStageFlags.TopOfPipeBit,
                ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
                ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthStencilReadOnlyOptimal =>
                    PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
                ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                ImageLayout.PresentSrcKhr => PipelineStageFlags.BottomOfPipeBit,
                _ => PipelineStageFlags.AllCommandsBit
            };

        private static AccessFlags ResolveComputeStorageImageSourceAccess(ImageLayout layout)
            => layout switch
            {
                ImageLayout.Undefined => AccessFlags.None,
                ImageLayout.ColorAttachmentOptimal => AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                ImageLayout.DepthStencilAttachmentOptimal => AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                ImageLayout.DepthStencilReadOnlyOptimal => AccessFlags.DepthStencilAttachmentReadBit,
                ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
                ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
                ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
                _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit
            };


    }
}
