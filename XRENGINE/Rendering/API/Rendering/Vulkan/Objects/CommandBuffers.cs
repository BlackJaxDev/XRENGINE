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

            // Ensure swapchain resources are transitioned appropriately before any rendering.
            var swapchainBarriers = _barrierPlanner.GetBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
            if (swapchainBarriers.Count > 0)
                EmitPlannedImageBarriers(commandBuffer, swapchainBarriers);
            else
                EmitPlannedImageBarriers(commandBuffer, _barrierPlanner.ImageBarriers);

            var ops = DrainFrameOps();

            bool renderPassActive = false;
            XRFrameBuffer? activeTarget = null;
            RenderPass activeRenderPass = default;
            Framebuffer activeFramebuffer = default;
            Rect2D activeRenderArea = default;
            int activePassIndex = int.MinValue;

            void EndActiveRenderPass()
            {
                if (!renderPassActive)
                    return;
                Api!.CmdEndRenderPass(commandBuffer);
                renderPassActive = false;
                activeTarget = null;
                activeRenderPass = default;
                activeFramebuffer = default;
                activeRenderArea = default;
            }

            void BeginRenderPassForTarget(XRFrameBuffer? target)
            {
                // Assumes no active render pass.
                if (target is null)
                {
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

                    const uint attachmentCount = 2;
                    ClearValue* clearValues = stackalloc ClearValue[(int)attachmentCount];
                    _state.WriteClearValues(clearValues, attachmentCount);
                    renderPassInfo.ClearValueCount = attachmentCount;
                    renderPassInfo.PClearValues = clearValues;

                    Api!.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);

                    renderPassActive = true;
                    activeTarget = null;
                    activeRenderPass = _renderPass;
                    activeFramebuffer = swapChainFramebuffers![imageIndex];
                    activeRenderArea = renderPassInfo.RenderArea;
                    return;
                }

                var vkFrameBuffer = GenericToAPI<VkFrameBuffer>(target);
                if (vkFrameBuffer is null)
                    throw new InvalidOperationException("Failed to resolve Vulkan framebuffer for target.");

                RenderPassBeginInfo fboPassInfo = new()
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = vkFrameBuffer.RenderPass,
                    Framebuffer = vkFrameBuffer.FrameBuffer,
                    RenderArea = new Rect2D
                    {
                        Offset = new Offset2D(0, 0),
                        Extent = new Extent2D(Math.Max(target.Width, 1u), Math.Max(target.Height, 1u))
                    }
                };

                uint attachmentCountFbo = Math.Max(vkFrameBuffer.AttachmentCount, 1u);
                ClearValue* clearValuesFbo = stackalloc ClearValue[(int)attachmentCountFbo];
                vkFrameBuffer.WriteClearValues(clearValuesFbo, attachmentCountFbo);
                fboPassInfo.ClearValueCount = attachmentCountFbo;
                fboPassInfo.PClearValues = clearValuesFbo;

                Api!.CmdBeginRenderPass(commandBuffer, &fboPassInfo, SubpassContents.Inline);

                renderPassActive = true;
                activeTarget = target;
                activeRenderPass = vkFrameBuffer.RenderPass;
                activeFramebuffer = vkFrameBuffer.FrameBuffer;
                activeRenderArea = fboPassInfo.RenderArea;
            }

            void EmitPassBarriers(int passIndex)
            {
                var barriers = _barrierPlanner.GetBarriersForPass(passIndex);
                if (barriers.Count > 0)
                    EmitPlannedImageBarriers(commandBuffer, barriers);
            }

            foreach (var op in ops)
            {
                if (op.PassIndex != activePassIndex)
                {
                    // Barriers are safest outside render passes.
                    EndActiveRenderPass();
                    EmitPassBarriers(op.PassIndex);
                    activePassIndex = op.PassIndex;
                }

                switch (op)
                {
                    case BlitOp blit:
                        EndActiveRenderPass();
                        RecordBlitOp(commandBuffer, blit);
                        break;

                    case ClearOp clear:
                        if (!renderPassActive || activeTarget != clear.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(clear.Target);
                        }
                        RecordClearOp(commandBuffer, imageIndex, clear);
                        break;

                    case MeshDrawOp drawOp:
                        if (!renderPassActive || activeTarget != drawOp.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(drawOp.Target);
                        }

                        // Apply per-draw dynamic state snapshot (OpenGL-like immediate semantics).
                        Viewport viewport = drawOp.Draw.Viewport;
                        Api!.CmdSetViewport(commandBuffer, 0, 1, &viewport);
                        Rect2D scissor = drawOp.Draw.Scissor;
                        Api!.CmdSetScissor(commandBuffer, 0, 1, &scissor);

                        drawOp.Draw.Renderer.RecordDraw(commandBuffer, drawOp.Draw, activeRenderPass);
                        break;
                }
            }

            // Always finish with a swapchain render pass so ImGui/debug overlay can present.
            if (!renderPassActive || activeTarget is not null)
            {
                EndActiveRenderPass();
                BeginRenderPassForTarget(null);
            }

            ApplyDynamicState(commandBuffer);
            RenderDebugTriangle(commandBuffer);
            RenderImGui(commandBuffer, imageIndex);
            EndActiveRenderPass();

            if (Api!.EndCommandBuffer(commandBuffer) != Result.Success)
                throw new Exception("Failed to record command buffer.");
        }

        private void RecordClearOp(CommandBuffer commandBuffer, uint imageIndex, ClearOp op)
        {
            _ = imageIndex;
            ClearRect clearRect = new()
            {
                Rect = op.Rect,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            ClearRect* rectPtr = stackalloc ClearRect[1];
            rectPtr[0] = clearRect;

            if (op.Target is null)
            {
                // Swapchain: single color attachment + depth.
                ClearAttachment* attachments = stackalloc ClearAttachment[2];
                uint count = 0;

                if (op.ClearColor)
                {
                    attachments[count++] = new ClearAttachment
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        ColorAttachment = 0,
                        ClearValue = new ClearValue
                        {
                            Color = new ClearColorValue
                            {
                                Float32_0 = op.Color.R,
                                Float32_1 = op.Color.G,
                                Float32_2 = op.Color.B,
                                Float32_3 = op.Color.A
                            }
                        }
                    };
                }

                if (op.ClearDepth || op.ClearStencil)
                {
                    ImageAspectFlags aspects = ImageAspectFlags.None;
                    if (op.ClearDepth)
                        aspects |= ImageAspectFlags.DepthBit;
                    if (op.ClearStencil)
                        aspects |= ImageAspectFlags.StencilBit;

                    attachments[count++] = new ClearAttachment
                    {
                        AspectMask = aspects,
                        ClearValue = new ClearValue
                        {
                            DepthStencil = new ClearDepthStencilValue
                            {
                                Depth = op.Depth,
                                Stencil = op.Stencil
                            }
                        }
                    };
                }

                if (count > 0)
                    Api!.CmdClearAttachments(commandBuffer, count, attachments, 1, rectPtr);

                return;
            }

            var vkFrameBuffer = GenericToAPI<VkFrameBuffer>(op.Target);
            if (vkFrameBuffer is null)
                return;

            uint maxAttachments = Math.Max(vkFrameBuffer.AttachmentCount + 1u, 2u);
            ClearAttachment* fboAttachments = stackalloc ClearAttachment[(int)maxAttachments];
            uint fboCount = vkFrameBuffer.WriteClearAttachments(fboAttachments, op.ClearColor, op.ClearDepth, op.ClearStencil);
            if (fboCount > 0)
                Api!.CmdClearAttachments(commandBuffer, fboCount, fboAttachments, 1, rectPtr);
        }

        private void RecordBlitOp(CommandBuffer commandBuffer, BlitOp op)
        {
            if (!TryResolveBlitImage(op.InFbo, op.ColorBit, op.DepthBit, op.StencilBit, out var source))
                return;
            if (!TryResolveBlitImage(op.OutFbo, op.ColorBit, op.DepthBit, op.StencilBit, out var destination))
                return;

            Filter filter = op.LinearFilter ? Filter.Linear : Filter.Nearest;
            ImageBlit region = BuildImageBlit(source, destination, op.InX, op.InY, op.InW, op.InH, op.OutX, op.OutY, op.OutW, op.OutH);

            // Transition to transfer layouts, blit, then transition back to preferred layouts.
            TransitionForBlit(
                commandBuffer,
                source,
                source.PreferredLayout,
                ImageLayout.TransferSrcOptimal,
                source.AccessMask,
                AccessFlags.TransferReadBit,
                source.StageMask,
                PipelineStageFlags.TransferBit);

            TransitionForBlit(
                commandBuffer,
                destination,
                destination.PreferredLayout,
                ImageLayout.TransferDstOptimal,
                destination.AccessMask,
                AccessFlags.TransferWriteBit,
                destination.StageMask,
                PipelineStageFlags.TransferBit);

            Api!.CmdBlitImage(
                commandBuffer,
                source.Image,
                ImageLayout.TransferSrcOptimal,
                destination.Image,
                ImageLayout.TransferDstOptimal,
                1,
                &region,
                filter);

            TransitionForBlit(
                commandBuffer,
                source,
                ImageLayout.TransferSrcOptimal,
                source.PreferredLayout,
                AccessFlags.TransferReadBit,
                source.AccessMask,
                PipelineStageFlags.TransferBit,
                source.StageMask);

            TransitionForBlit(
                commandBuffer,
                destination,
                ImageLayout.TransferDstOptimal,
                destination.PreferredLayout,
                AccessFlags.TransferWriteBit,
                destination.AccessMask,
                PipelineStageFlags.TransferBit,
                destination.StageMask);
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
            PipelineStageFlags srcStagesLocal = 0;
            PipelineStageFlags dstStagesLocal = 0;
            AccessFlags srcAccessLocal = 0;
            AccessFlags dstAccessLocal = 0;

            void Merge(bool condition, PipelineStageFlags srcStage, PipelineStageFlags dstStage, AccessFlags srcAcc, AccessFlags dstAcc)
            {
                if (!condition)
                    return;

                srcStagesLocal |= srcStage;
                dstStagesLocal |= dstStage;
                srcAccessLocal |= srcAcc;
                dstAccessLocal |= dstAcc;
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
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

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

            if (srcStagesLocal == 0)
                srcStagesLocal = PipelineStageFlags.AllCommandsBit;
            if (dstStagesLocal == 0)
                dstStagesLocal = PipelineStageFlags.AllCommandsBit;
            if (srcAccessLocal == 0)
                srcAccessLocal = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
            if (dstAccessLocal == 0)
                dstAccessLocal = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;

            srcStages = srcStagesLocal;
            dstStages = dstStagesLocal;
            srcAccess = srcAccessLocal;
            dstAccess = dstAccessLocal;
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
