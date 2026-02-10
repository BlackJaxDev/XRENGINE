using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private CommandBuffer[]? _commandBuffers;
        private List<ComputeTransientResources>[]? _computeTransientResources;

        private sealed class ComputeTransientResources
        {
            public DescriptorPool DescriptorPool;
            public List<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)> UniformBuffers { get; } = [];
        }

        private void DestroyCommandBuffers()
        {
            if (_commandBuffers is null)
                return;

            DestroyComputeTransientResources();

            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                if (_commandBuffers.Length > 0)
                    Api!.FreeCommandBuffers(device, commandPool, (uint)_commandBuffers.Length, commandBuffersPtr);
            }

            _commandBuffers = null;
            _commandBufferDirtyFlags = null;
        }

        private void DestroyComputeTransientResources()
        {
            if (_computeTransientResources is null)
                return;

            for (int i = 0; i < _computeTransientResources.Length; i++)
                CleanupComputeTransientResources((uint)i);

            _computeTransientResources = null;
        }

        private void CleanupComputeTransientResources(uint imageIndex)
        {
            if (_computeTransientResources is null || imageIndex >= _computeTransientResources.Length)
                return;

            List<ComputeTransientResources>? resources = _computeTransientResources[imageIndex];
            if (resources is null || resources.Count == 0)
                return;

            foreach (ComputeTransientResources resource in resources)
            {
                if (resource.DescriptorPool.Handle != 0)
                    Api!.DestroyDescriptorPool(device, resource.DescriptorPool, null);

                foreach ((Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) in resource.UniformBuffers)
                {
                    if (buffer.Handle != 0)
                        Api!.DestroyBuffer(device, buffer, null);
                    if (memory.Handle != 0)
                        Api!.FreeMemory(device, memory, null);
                }
            }

            resources.Clear();
        }

        private void RegisterComputeTransientResources(
            uint imageIndex,
            DescriptorPool descriptorPool,
            IReadOnlyList<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)> uniformBuffers)
        {
            if (_computeTransientResources is null || imageIndex >= _computeTransientResources.Length)
                return;

            _computeTransientResources[imageIndex] ??= [];
            var resource = new ComputeTransientResources
            {
                DescriptorPool = descriptorPool
            };

            if (uniformBuffers is { Count: > 0 })
                resource.UniformBuffers.AddRange(uniformBuffers);

            _computeTransientResources[imageIndex]!.Add(resource);
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
            _computeTransientResources = new List<ComputeTransientResources>[_commandBuffers.Length];
        }

        private void EnsureCommandBufferRecorded(uint imageIndex)
        {
            if (_commandBuffers is null)
                throw new InvalidOperationException("Command buffers have not been allocated yet.");

            if (_commandBufferDirtyFlags is null || imageIndex >= _commandBufferDirtyFlags.Length)
                throw new InvalidOperationException("Command buffer dirty flags are not initialised correctly.");

            if (!_commandBufferDirtyFlags[imageIndex])
                return;

            RecordCommandBuffer(imageIndex);
            _commandBufferDirtyFlags[imageIndex] = false;
        }

        private void RecordCommandBuffer(uint imageIndex)
        {
            var commandBuffer = _commandBuffers![imageIndex];

            Api!.ResetCommandBuffer(commandBuffer, 0);
            CleanupComputeTransientResources(imageIndex);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
            };

            if (Api!.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
                throw new Exception("Failed to begin recording command buffer.");

            CmdBeginLabel(commandBuffer, $"FrameCmd[{imageIndex}]");

            EmitPendingMemoryBarriers(commandBuffer);

            var ops = DrainFrameOps();
            FrameOpContext initialContext = ops.Length > 0
                ? ops[0].Context
                : CaptureFrameOpContext();
            UpdateResourcePlannerFromContext(initialContext);

            // Ensure swapchain resources are transitioned appropriately before any rendering.
            CmdBeginLabel(commandBuffer, "SwapchainBarriers");
            var swapchainBarriers = _barrierPlanner.GetBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
            if (swapchainBarriers.Count > 0)
                EmitPlannedImageBarriers(commandBuffer, swapchainBarriers);
            else
                EmitPlannedImageBarriers(commandBuffer, _barrierPlanner.ImageBarriers);
            CmdEndLabel(commandBuffer);

            int clearCount = 0;
            int drawCount = 0;
            int blitCount = 0;
            int computeCount = 0;
            foreach (var op in ops)
            {
                switch (op)
                {
                    case ClearOp: clearCount++; break;
                    case MeshDrawOp: drawCount++; break;
                    case BlitOp: blitCount++; break;
                    case ComputeDispatchOp: computeCount++; break;
                }
            }

            Debug.VulkanEvery(
                $"Vulkan.FrameOps.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] FrameOps: total={0} clears={1} draws={2} blits={3} computes={4}",
                ops.Length,
                clearCount,
                drawCount,
                blitCount,
                computeCount);

            bool renderPassActive = false;
            XRFrameBuffer? activeTarget = null;
            RenderPass activeRenderPass = default;
            Framebuffer activeFramebuffer = default;
            Rect2D activeRenderArea = default;
            int activePassIndex = int.MinValue;
            int activeSchedulingIdentity = int.MinValue;
            FrameOpContext activeContext = default;
            bool hasActiveContext = false;
            bool renderPassLabelActive = false;
            bool passIndexLabelActive = false;

            void EndActiveRenderPass()
            {
                if (!renderPassActive)
                    return;
                Api!.CmdEndRenderPass(commandBuffer);
                if (renderPassLabelActive)
                {
                    CmdEndLabel(commandBuffer);
                    renderPassLabelActive = false;
                }
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
                    CmdBeginLabel(commandBuffer, "RenderPass:Swapchain");
                    renderPassLabelActive = true;

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

                string fboName = string.IsNullOrWhiteSpace(target.Name)
                    ? $"FBO[{target.GetHashCode()}]"
                    : target.Name!;
                CmdBeginLabel(commandBuffer, $"RenderPass:{fboName}");
                renderPassLabelActive = true;

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
                {
                    CmdBeginLabel(commandBuffer, "PassBarriers");
                    EmitPlannedImageBarriers(commandBuffer, barriers);
                    CmdEndLabel(commandBuffer);
                }
            }

            foreach (var op in ops)
            {
                if (!hasActiveContext || !Equals(activeContext, op.Context))
                {
                    EndActiveRenderPass();
                    if (passIndexLabelActive)
                    {
                        CmdEndLabel(commandBuffer);
                        passIndexLabelActive = false;
                    }

                    activeContext = op.Context;
                    hasActiveContext = true;
                    UpdateResourcePlannerFromContext(activeContext);
                    activePassIndex = int.MinValue;
                    activeSchedulingIdentity = int.MinValue;
                }

                int opPassIndex = EnsureValidPassIndex(op.PassIndex, op.GetType().Name);
                int opSchedulingIdentity = op.Context.SchedulingIdentity;
                if (opPassIndex != activePassIndex || opSchedulingIdentity != activeSchedulingIdentity)
                {
                    // Barriers are safest outside render passes.
                    EndActiveRenderPass();

                    if (passIndexLabelActive)
                    {
                        CmdEndLabel(commandBuffer);
                        passIndexLabelActive = false;
                    }

                    CmdBeginLabel(
                        commandBuffer,
                        $"Pass={opPassIndex} Pipe={op.Context.PipelineIdentity} Vp={op.Context.ViewportIdentity}");
                    passIndexLabelActive = true;

                    EmitPassBarriers(opPassIndex);
                    activePassIndex = opPassIndex;
                    activeSchedulingIdentity = opSchedulingIdentity;
                }

                switch (op)
                {
                    case BlitOp blit:
                        EndActiveRenderPass();
                        CmdBeginLabel(commandBuffer, "Blit");
                        RecordBlitOp(commandBuffer, blit);
                        CmdEndLabel(commandBuffer);
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

                    case IndirectDrawOp indirectOp:
                        EndActiveRenderPass();
                        CmdBeginLabel(commandBuffer, "IndirectDraw");
                        RecordIndirectDrawOp(commandBuffer, indirectOp);
                        CmdEndLabel(commandBuffer);
                        break;

                    case ComputeDispatchOp computeOp:
                        EndActiveRenderPass();
                        CmdBeginLabel(commandBuffer, "ComputeDispatch");
                        RecordComputeDispatchOp(commandBuffer, imageIndex, computeOp);
                        CmdEndLabel(commandBuffer);
                        break;
                }
            }

            if (passIndexLabelActive)
            {
                CmdEndLabel(commandBuffer);
                passIndexLabelActive = false;
            }

            // Always finish with a swapchain render pass so ImGui/debug overlay can present.
            if (!renderPassActive || activeTarget is not null)
            {
                EndActiveRenderPass();
                BeginRenderPassForTarget(null);
            }

            // For presentation we want deterministic full-surface state regardless of prior per-viewport scissor.
            // This also makes resize issues obvious (the clear should cover the entire swapchain extent).
            Viewport swapViewport = new()
            {
                X = 0f,
                Y = 0f,
                Width = swapChainExtent.Width,
                Height = swapChainExtent.Height,
                MinDepth = 0f,
                MaxDepth = 1f
            };

            Rect2D swapScissor = new()
            {
                Offset = new Offset2D(0, 0),
                Extent = swapChainExtent
            };

            Api!.CmdSetViewport(commandBuffer, 0, 1, &swapViewport);
            Api!.CmdSetScissor(commandBuffer, 0, 1, &swapScissor);

            if (drawCount == 0 && blitCount == 0)
            {
                CmdBeginLabel(commandBuffer, "DebugTriangle");
                RenderDebugTriangle(commandBuffer);
                CmdEndLabel(commandBuffer);
            }

            if (SupportsImGui)
            {
                CmdBeginLabel(commandBuffer, "ImGui");
                RenderImGui(commandBuffer, imageIndex);
                CmdEndLabel(commandBuffer);
            }

            EndActiveRenderPass();

            CmdEndLabel(commandBuffer);

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

        private void RecordIndirectDrawOp(CommandBuffer commandBuffer, IndirectDrawOp op)
        {
            var indirectBuffer = op.IndirectBuffer.BufferHandle;
            if (indirectBuffer is null || !indirectBuffer.HasValue)
            {
                Debug.VulkanWarning("RecordIndirectDrawOp: Invalid indirect buffer.");
                return;
            }

            // Emit memory barrier to ensure indirect buffer data is visible
            MemoryBarrier memoryBarrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.IndirectCommandReadBit,
            };

            Api!.CmdPipelineBarrier(
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

            // Calculate the byte offset into the indirect buffer
            ulong bufferOffset = op.ByteOffset;

            if (op.UseCount && _supportsDrawIndirectCount && _khrDrawIndirectCount is not null)
            {
                // Use VK_KHR_draw_indirect_count path
                var parameterBuffer = op.ParameterBuffer?.BufferHandle;
                if (parameterBuffer is null || !parameterBuffer.HasValue)
                {
                    Debug.VulkanWarning("RecordIndirectDrawOp: Invalid parameter buffer for count draw.");
                    return;
                }

                // The parameter buffer contains the draw count at offset 0 (uint)
                _khrDrawIndirectCount.CmdDrawIndexedIndirectCount(
                    commandBuffer,
                    indirectBuffer.Value,
                    bufferOffset,
                    parameterBuffer.Value,
                    0, // Offset into parameter buffer where count is stored
                    op.DrawCount,
                    op.Stride);
            }
            else
            {
                // Standard indirect draw - issue one draw per command in the buffer
                // Vulkan's vkCmdDrawIndexedIndirect can only do one draw at a time, 
                // so we need to loop for multi-draw semantics
                for (uint i = 0; i < op.DrawCount; i++)
                {
                    Api!.CmdDrawIndexedIndirect(
                        commandBuffer,
                        indirectBuffer.Value,
                        bufferOffset + (i * op.Stride),
                        1,
                        op.Stride);
                }
            }
        }

        private void RecordComputeDispatchOp(CommandBuffer commandBuffer, uint imageIndex, ComputeDispatchOp op)
        {
            if (!op.Program.Link())
                return;

            Pipeline pipeline;
            try
            {
                pipeline = op.Program.GetOrCreateComputePipeline();
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"Failed to create Vulkan compute pipeline for '{op.Program.Data.Name ?? "UnnamedProgram"}': {ex.Message}");
                return;
            }

            if (pipeline.Handle == 0)
                return;

            Api!.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);

            if (op.Program.TryBuildAndBindComputeDescriptorSets(commandBuffer, op.Snapshot, out DescriptorPool descriptorPool, out var tempBuffers))
                RegisterComputeTransientResources(imageIndex, descriptorPool, tempBuffers);

            Api!.CmdDispatch(commandBuffer, op.GroupsX, op.GroupsY, op.GroupsZ);
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
