using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private CommandBuffer[]? _commandBuffers;

        private void DestroyCommandBuffers()
        {
            if (_commandBuffers is null)
                return;

            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                if (_commandBuffers.Length > 0)
                    Api!.FreeCommandBuffers(device, commandPool, (uint)_commandBuffers.Length, commandBuffersPtr);
            }

            _commandBuffers = null;
            _commandBufferDirtyFlags = null;
        }

        private void CreateCommandBuffers()
        {
            if (swapChainFramebuffers is null || swapChainFramebuffers.Length == 0)
                throw new InvalidOperationException("Framebuffers must be created before allocating command buffers.");

            _commandBuffers = new CommandBuffer[swapChainFramebuffers.Length];

            CommandBufferAllocateInfo allocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = (uint)_commandBuffers.Length,
            };

            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                if (Api!.AllocateCommandBuffers(device, ref allocInfo, commandBuffersPtr) != Result.Success)
                    throw new Exception("Failed to allocate command buffers.");
            }

            AllocateCommandBufferDirtyFlags();
        }

        private void EnsureCommandBufferRecorded(uint imageIndex)
        {
            if (_commandBuffers is null)
                throw new InvalidOperationException("Command buffers have not been allocated yet.");

            if (_commandBufferDirtyFlags is null || imageIndex >= _commandBufferDirtyFlags.Length)
                throw new InvalidOperationException("Command buffer dirty flags are not initialised correctly.");

            if (!_commandBufferDirtyFlags[imageIndex])
                return;

            UpdateResourcePlannerFromPipeline();
            RecordCommandBuffer(imageIndex);
            _commandBufferDirtyFlags[imageIndex] = false;
        }

        private void RecordCommandBuffer(uint imageIndex)
        {
            var commandBuffer = _commandBuffers![imageIndex];

            Api!.ResetCommandBuffer(commandBuffer, 0);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
            };

            if (Api!.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
                throw new Exception("Failed to begin recording command buffer.");

            EmitPendingMemoryBarriers(commandBuffer);

            var swapchainBarriers = _barrierPlanner.GetBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
            if (swapchainBarriers.Count > 0)
                EmitPlannedImageBarriers(commandBuffer, swapchainBarriers);
            else
                EmitPlannedImageBarriers(commandBuffer, _barrierPlanner.ImageBarriers);

            RenderPassBeginInfo renderPassInfo = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _renderPass,
                Framebuffer = swapChainFramebuffers![imageIndex],
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D(0, 0),
                    Extent = swapChainExtent
                }
            };

            const uint attachmentCount = 1;
            ClearValue* clearValues = stackalloc ClearValue[(int)attachmentCount];
            _state.WriteClearValues(clearValues, attachmentCount);

            renderPassInfo.ClearValueCount = attachmentCount;
            renderPassInfo.PClearValues = clearValues;

            Api!.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);

            ApplyDynamicState(commandBuffer);
            RenderImGui(commandBuffer, imageIndex);

            Api!.CmdEndRenderPass(commandBuffer);

            if (Api!.EndCommandBuffer(commandBuffer) != Result.Success)
                throw new Exception("Failed to record command buffer.");
        }

        private void ApplyDynamicState(CommandBuffer commandBuffer)
        {
            Viewport viewport = _state.GetViewport();
            Api!.CmdSetViewport(commandBuffer, 0, 1, &viewport);

            Rect2D scissor = _state.GetScissor();
            Api!.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        }

        private void EmitPendingMemoryBarriers(CommandBuffer commandBuffer)
        {
            var pendingMask = _state.PendingMemoryBarrierMask;
            if (pendingMask == EMemoryBarrierMask.None)
                return;

            ResolveBarrierScopes(pendingMask, out PipelineStageFlags srcStages, out PipelineStageFlags dstStages, out AccessFlags srcAccess, out AccessFlags dstAccess);

            MemoryBarrier memoryBarrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
            };

            Api!.CmdPipelineBarrier(
                commandBuffer,
                srcStages,
                dstStages,
                DependencyFlags.None,
                1,
                &memoryBarrier,
                0,
                null,
                0,
                null);

            _state.ClearPendingMemoryBarrierMask();
        }

        private static void ResolveBarrierScopes(
            EMemoryBarrierMask mask,
            out PipelineStageFlags srcStages,
            out PipelineStageFlags dstStages,
            out AccessFlags srcAccess,
            out AccessFlags dstAccess)
        {
            srcStages = 0;
            dstStages = 0;
            srcAccess = 0;
            dstAccess = 0;

            void Merge(bool condition, PipelineStageFlags srcStage, PipelineStageFlags dstStage, AccessFlags srcAcc, AccessFlags dstAcc)
            {
                if (!condition)
                    return;

                srcStages |= srcStage;
                dstStages |= dstStage;
                srcAccess |= srcAcc;
                dstAccess |= dstAcc;
            }

            Merge(mask.HasFlag(EMemoryBarrierMask.VertexAttribArray),
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit,
                PipelineStageFlags.VertexInputBit,
                AccessFlags.TransferWriteBit | AccessFlags.VertexAttributeReadBit,
                AccessFlags.VertexAttributeReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.ElementArray),
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit,
                PipelineStageFlags.VertexInputBit,
                AccessFlags.TransferWriteBit | AccessFlags.IndexReadBit,
                AccessFlags.IndexReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.Uniform),
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit,
                AccessFlags.UniformReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.TextureFetch) || mask.HasFlag(EMemoryBarrierMask.TextureUpdate),
                PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.TransferWriteBit | AccessFlags.ShaderReadBit,
                AccessFlags.ShaderReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.ShaderGlobalAccess) || mask.HasFlag(EMemoryBarrierMask.ShaderImageAccess) || mask.HasFlag(EMemoryBarrierMask.ShaderStorage),
                PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit,
                PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.Command),
                PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                PipelineStageFlags.DrawIndirectBit,
                AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit,
                AccessFlags.IndirectCommandReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.PixelBuffer) || mask.HasFlag(EMemoryBarrierMask.BufferUpdate),
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit,
                AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit,
                AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit | AccessFlags.VertexAttributeReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.Framebuffer),
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
                AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.AtomicCounter),
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.AtomicCounterReadBit | AccessFlags.AtomicCounterWriteBit,
                AccessFlags.AtomicCounterReadBit | AccessFlags.AtomicCounterWriteBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.ClientMappedBuffer),
                PipelineStageFlags.HostBit,
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.HostWriteBit,
                AccessFlags.TransferReadBit | AccessFlags.VertexAttributeReadBit | AccessFlags.UniformReadBit | AccessFlags.ShaderReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.QueryBuffer),
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit,
                AccessFlags.MemoryWriteBit,
                AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

            if (srcStages == 0)
                srcStages = PipelineStageFlags.AllCommandsBit;
            if (dstStages == 0)
                dstStages = PipelineStageFlags.AllCommandsBit;
            if (srcAccess == 0)
                srcAccess = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
            if (dstAccess == 0)
                dstAccess = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
        }

        private void EmitPlannedImageBarriers(CommandBuffer commandBuffer, IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier>? plannedBarriers)
        {
            if (plannedBarriers is null || plannedBarriers.Count == 0)
                return;

            foreach (var planned in plannedBarriers)
            {
                planned.Group.EnsureAllocated(this);

                ImageSubresourceRange range = new()
                {
                    AspectMask = planned.Next.AspectMask,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = Math.Max(planned.Group.Template.Layers, 1u)
                };

                ImageMemoryBarrier barrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = planned.Previous.AccessMask,
                    DstAccessMask = planned.Next.AccessMask,
                    OldLayout = planned.Previous.Layout,
                    NewLayout = planned.Next.Layout,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = planned.Group.Image,
                    SubresourceRange = range
                };

                Api!.CmdPipelineBarrier(
                    commandBuffer,
                    planned.Previous.StageMask,
                    planned.Next.StageMask,
                    DependencyFlags.None,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        public class CommandScope : IDisposable
        {
            private readonly VulkanRenderer _api;

            public CommandScope(VulkanRenderer api, CommandBuffer cmd)
            {
                _api = api;
                CommandBuffer = cmd;
            }

            public CommandBuffer CommandBuffer { get; }

            public void Dispose()
            {
                _api.CommandsStop(CommandBuffer);
                GC.SuppressFinalize(this);
            }
        }

        private CommandScope NewCommandScope()
            => new(this, CommandsStart());

        private CommandBuffer CommandsStart()
        {
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = commandPool,
                CommandBufferCount = 1,
            };

            Api!.AllocateCommandBuffers(device, ref allocateInfo, out CommandBuffer commandBuffer);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            Api!.BeginCommandBuffer(commandBuffer, ref beginInfo);

            return commandBuffer;
        }

        private void CommandsStop(CommandBuffer commandBuffer)
        {
            Api!.EndCommandBuffer(commandBuffer);

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };

            Api!.QueueSubmit(graphicsQueue, 1, ref submitInfo, default);
            Api!.QueueWaitIdle(graphicsQueue);

            Api!.FreeCommandBuffers(device, commandPool, 1, ref commandBuffer);
        }

        private void AllocateCommandBufferDirtyFlags()
        {
            if (_commandBuffers is null)
            {
                _commandBufferDirtyFlags = null;
                return;
            }

            _commandBufferDirtyFlags = new bool[_commandBuffers.Length];
            for (int i = 0; i < _commandBufferDirtyFlags.Length; i++)
                _commandBufferDirtyFlags[i] = true;
        }
    }
}
