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
    public unsafe partial class VulkanRenderer
    {
        private void RecordIndirectDrawOp(CommandBuffer commandBuffer, IndirectDrawOp op, bool allowInlineBarrier = true)
        {
            var indirectBuffer = op.IndirectBuffer.BufferHandle;
            if (indirectBuffer is null || !indirectBuffer.HasValue)
            {
                Debug.VulkanWarning("RecordIndirectDrawOp: Invalid indirect buffer.");
                return;
            }

            bool plannerCoversIndirectBarrier = PlannerCoversIndirectBufferTransition(op.PassIndex, indirectBuffer.Value);
            if (!plannerCoversIndirectBarrier && allowInlineBarrier)
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
            else if (!plannerCoversIndirectBarrier)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 0, redundantCount: 1);
            }
            else
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 0, redundantCount: 1);
                Debug.VulkanWarningEvery(
                    "Vulkan.IndirectBarrier.Overlap",
                    TimeSpan.FromSeconds(2),
                    "Indirect barrier overlap detected and suppressed: pass={0} drawCount={1}",
                    op.PassIndex,
                    op.DrawCount);
            }

            // Calculate the byte offset into the indirect buffer
            ulong bufferOffset = op.ByteOffset;
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                indirectBuffer.Value.Handle,
                "IndirectDraw.Commands");

            if (IndirectTraceEnabled)
            {
                Debug.Vulkan(
                    "[VulkanIndirect] pass={0} passName='{1}' target='{2}' targetId={3} indirect=0x{4:X} parameter=0x{5:X} offset={6} countOffset={7} stride={8} maxDraws={9} useCount={10} renderer={11} material='{12}' program='{13}'",
                    op.PassIndex,
                    ResolvePassName(op.Context.PassMetadata, op.PassIndex),
                    op.Target?.Name ?? "<swapchain>",
                    op.Target?.GetHashCode() ?? 0,
                    indirectBuffer.Value.Handle,
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
                !TryBindGlobalMaterialTextureDescriptorSet(
                    commandBuffer,
                    bindlessMaterialTextures.Program,
                    bindlessMaterialTextures.Consumer))
            {
                return;
            }

            if (op.UseCount && DeviceCapabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount))
            {
                var parameterBuffer = op.ParameterBuffer?.BufferHandle;
                if (parameterBuffer is null || !parameterBuffer.HasValue)
                {
                    Debug.VulkanWarning("RecordIndirectDrawOp: Invalid parameter buffer for count draw.");
                    return;
                }

                // The parameter buffer contains the draw count at offset 0 (uint)
                TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.Buffer,
                    parameterBuffer.Value.Handle,
                    "IndirectDraw.Count");
                if (_usesCoreDrawIndirectCountCommands)
                {
                    Api!.CmdDrawIndexedIndirectCount(
                        commandBuffer,
                        indirectBuffer.Value,
                        bufferOffset,
                        parameterBuffer.Value,
                        (ulong)op.CountByteOffset,
                        op.DrawCount,
                        op.Stride);
                }
                else if (_khrDrawIndirectCount is not null)
                {
                    _khrDrawIndirectCount.CmdDrawIndexedIndirectCount(
                        commandBuffer,
                        indirectBuffer.Value,
                        bufferOffset,
                        parameterBuffer.Value,
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
                    indirectBuffer.Value,
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

			bool allowSynchronousBufferUpload = AllowSynchronousResourceUploads;
			if (GetOrCreateAPIRenderObject(dataBuffer, generateNow: allowSynchronousBufferUpload) is VkDataBuffer vkBuffer &&
				vkBuffer.TryEnsureReadyForRendering(allowSynchronousBufferUpload))
			{
				buffer = vkBuffer;
				return true;
			}

            Debug.VulkanWarning($"Failed to resolve Vulkan transform feedback {role} buffer.");
            return false;
        }

        internal void RecordMeshTaskDispatchIndirectCountOp(CommandBuffer commandBuffer, MeshTaskDispatchIndirectCountOp op)
        {
            if (!SupportsVulkanMeshTaskIndirectCount || _extMeshShader is null)
            {
                Debug.VulkanWarning("RecordMeshTaskDispatchIndirectCountOp: VK_EXT_mesh_shader indirect-count dispatch is unavailable.");
                return;
            }

            var indirectBuffer = op.IndirectBuffer.BufferHandle;
            if (indirectBuffer is null || !indirectBuffer.HasValue)
            {
                Debug.VulkanWarning("RecordMeshTaskDispatchIndirectCountOp: Invalid indirect buffer.");
                return;
            }

            var countBuffer = op.CountBuffer.BufferHandle;
            if (countBuffer is null || !countBuffer.HasValue)
            {
                Debug.VulkanWarning("RecordMeshTaskDispatchIndirectCountOp: Invalid count buffer.");
                return;
            }

            if (op.MaxDrawCount == 0u)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.MeshTaskIndirect.ZeroMaxDrawCount",
                    TimeSpan.FromSeconds(1),
                    "RecordMeshTaskDispatchIndirectCountOp skipped: maxDrawCount was zero.");
                return;
            }

            if (op.BindlessMaterialTextures is { } bindlessMaterialTextures &&
                !TryBindGlobalMaterialTextureDescriptorSet(
                    commandBuffer,
                    bindlessMaterialTextures.Program,
                    bindlessMaterialTextures.Consumer))
            {
                return;
            }

            bool plannerCoversIndirectBarrier =
                PlannerCoversIndirectBufferTransition(op.PassIndex, indirectBuffer.Value) &&
                PlannerCoversIndirectBufferTransition(op.PassIndex, countBuffer.Value);
            if (!plannerCoversIndirectBarrier)
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

            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                indirectBuffer.Value.Handle,
                "MeshTaskIndirect.Commands");
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                countBuffer.Value.Handle,
                "MeshTaskIndirect.Count");
            _extMeshShader.CmdDrawMeshTasksIndirectCount(
                commandBuffer,
                indirectBuffer.Value,
                (ulong)op.ByteOffset,
                countBuffer.Value,
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
            if (!op.Program.Link())
                throw new InvalidOperationException($"Compute program '{op.Program.Data.Name ?? "UnnamedProgram"}' became unavailable after enqueue.");

            Pipeline pipeline;
            try
            {
                pipeline = op.Program.GetOrCreateComputePipeline(op.PassIndex, op.Context.PassMetadata);
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"Failed to create Vulkan compute pipeline for '{op.Program.Data.Name ?? "UnnamedProgram"}': {ex.Message}");
                return;
            }

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

            ulong reusableDescriptorKey = ComputeReusableComputeDescriptorBindingKey(op, opIndex);
            if (!op.Program.TryBuildAndBindComputeDescriptorSets(commandBuffer, imageIndex, op.Snapshot, reusableDescriptorKey, out _, out var tempBuffers))
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
                throw new InvalidOperationException($"Descriptor binding failed for compute program '{op.Program.Data.Name ?? "UnnamedProgram"}'.");
            }

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

                if (GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source)
                    continue;

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
                    continue;

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
