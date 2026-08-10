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
    internal sealed unsafe partial class VulkanCommandRuntime
    {
        private void RecordIndirectDrawOp(CommandBuffer commandBuffer, IndirectDrawOp op, bool allowInlineBarrier = true)
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
                    bindlessMaterialTextures.Consumer))
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

        internal void RecordMeshTaskDispatchIndirectCountOp(CommandBuffer commandBuffer, MeshTaskDispatchIndirectCountOp op)
        {
            if (!DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.MeshShader) ||
                DeviceContext.ExtensionFunctions.ExtMeshShader is not { } meshShader)
            {
                Debug.VulkanWarning("RecordMeshTaskDispatchIndirectCountOp: VK_EXT_mesh_shader indirect-count dispatch is unavailable.");
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
                    bindlessMaterialTextures.Consumer))
            {
                return;
            }

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

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(
                usedCountPath: true,
                usedLoopFallback: false,
                apiCalls: 1,
                submittedDraws: op.MaxDrawCount);
        }

        internal void RecordComputeDispatchOp(CommandBuffer commandBuffer, uint imageIndex, ComputeDispatchOp op, int opIndex = -1)
        {
            // Resource preparation is the only phase allowed to link shaders or
            // create compute pipelines. Recording consumes the published handle
            // directly so a cache miss cannot compile inside the command scope.
            Pipeline pipeline = op.Program.ComputePipeline;
            if (pipeline.Handle == 0)
                throw new InvalidOperationException($"Compute pipeline '{op.Program.Data.Name ?? "UnnamedProgram"}' became unavailable after enqueue.");

            BindPipelineTracked(commandBuffer, PipelineBindPoint.Compute, pipeline);
            EnsureComputeStorageImageLayoutsForDispatch(commandBuffer, op.Snapshot);

            PushConstantsTracked(
                commandBuffer,
                op.Program.PipelineLayout,
                CommonPushConstantStageFlags,
                0,
                new ComputeDispatchPushConstants(op.GroupsX, op.GroupsY, op.GroupsZ, 0u));

            // The prepared operation index is sidecar state owned by this
            // recording pass; never write it back into the sealed frame plan.
            int descriptorBindingOrdinal = opIndex;
            ulong reusableDescriptorKey =
                ComputeReusableComputeDescriptorBindingKey(
                    op,
                    descriptorBindingOrdinal);
            if (!op.Program.TryBuildAndBindComputeDescriptorSets(commandBuffer, imageIndex, op.Snapshot, reusableDescriptorKey, out _, out DescriptorSet[] boundDescriptorSets, out var tempBuffers))
            {
                foreach ((Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) in tempBuffers)
                    DestroyBuffer(buffer, memory);

                // Descriptor binding failed (e.g. a required storage image lacks STORAGE_BIT).
                // Dispatching without valid descriptors causes GPU faults â†’ device lost.
                Debug.VulkanWarningEvery(
                    $"Vulkan.ComputeDispatch.NoDescriptors.{op.Program.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Skipping compute dispatch for '{0}' â€” descriptor binding failed.",
                    op.Program.Data.Name ?? "UnnamedProgram");
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                    op.Program.Data.Name,
                    "descriptor-set",
                    "<compute-dispatch>",
                    0,
                    0,
                    skippedDraw: false,
                    skippedDispatch: true,
                    "compute dispatch skipped because descriptor binding failed");
                throw new VulkanPlanPreconditionException(
                    $"Prepared compute descriptors became unavailable while recording " +
                    $"'{op.Program.Data.Name ?? "UnnamedProgram"}'. The frame must be retried; " +
                    "publishing a secondary without its dispatch would cache incomplete scene work.");
            }

            _commandBufferRecordingScratch.Value!.PreparedComputePayload =
                new VulkanPreparedComputePayload(boundDescriptorSets);

            RegisterComputeTransientUniformBuffers(imageIndex, tempBuffers);
            Api!.CmdDispatch(commandBuffer, op.GroupsX, op.GroupsY, op.GroupsZ);
        }

        private void EnsureComputeStorageImageLayoutsForDispatch(CommandBuffer commandBuffer, ComputeDispatchSnapshot snapshot)
        {
            foreach (ProgramImageBinding binding in snapshot.Images.Values)
            {
                XRTexture texture = binding.Texture;
                if (texture is null)
                    continue;

                if (ResourceRuntime.BackendObjects.Get(texture) is not IVkImageDescriptorSource source)
                    throw new VulkanPlanPreconditionException(
                        $"Compute storage image '{texture.Name ?? "<unnamed>"}' has no prepared Vulkan image wrapper.");

                if (!source.UsesAllocatorImage)
                    continue;

                if ((source.DescriptorUsage & ImageUsageFlags.StorageBit) == 0)
                    continue;

                uint mipLevels = Math.Max(source.DescriptorMipLevels, 1u);
                uint arrayLayers = Math.Max(source.DescriptorArrayLayers, 1u);
                uint baseMipLevel = binding.Level < 0 ? 0u : Math.Min((uint)binding.Level, mipLevels - 1u);
                uint baseArrayLayer = binding.Layered || binding.Layer < 0 ? 0u : Math.Min((uint)binding.Layer, arrayLayers - 1u);
                uint layerCount = binding.Layered || binding.Layer < 0 ? arrayLayers - baseArrayLayer : 1u;
                Image image = source.DescriptorImage;
                if (image.Handle == 0)
                    throw new VulkanPlanPreconditionException(
                        $"Compute storage image '{texture.Name ?? "<unnamed>"}' has no published Vulkan image handle.");

                ImageAspectFlags aspect = source.DescriptorAspect;
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
