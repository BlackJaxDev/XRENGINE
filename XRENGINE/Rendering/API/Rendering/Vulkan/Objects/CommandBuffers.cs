using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private CommandBuffer[]? _commandBuffers;
        private List<ComputeTransientResources>[]? _computeTransientResources;
        private List<DeferredSecondaryCommandBuffer>[]? _deferredSecondaryCommandBuffers;
        private readonly object _oneTimeCommandPoolsLock = new();
        private readonly Dictionary<nint, CommandPool> _oneTimeCommandPools = new();
        private readonly object _oneTimeSubmitLock = new();
        private readonly object _commandBindStateLock = new();
        private readonly Dictionary<ulong, CommandBufferBindState> _commandBindStates = new();
        private bool _enableSecondaryCommandBuffers = false;
        private bool _enableParallelSecondaryCommandBufferRecording = false;
        private int _parallelSecondaryIndirectRunThreshold = 4;

        private sealed class ComputeTransientResources
        {
            public DescriptorPool DescriptorPool;
            public List<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)> UniformBuffers { get; } = [];
        }

        private readonly struct DeferredSecondaryCommandBuffer
        {
            public DeferredSecondaryCommandBuffer(CommandPool pool, CommandBuffer commandBuffer)
            {
                Pool = pool;
                CommandBuffer = commandBuffer;
            }

            public CommandPool Pool { get; }
            public CommandBuffer CommandBuffer { get; }
        }

        private struct CommandBufferBindState
        {
            public ulong GraphicsPipeline;
            public ulong ComputePipeline;
            public ulong GraphicsDescriptorSignature;
            public ulong ComputeDescriptorSignature;
            public ulong VertexBufferSignature;
            public ulong IndexBuffer;
            public ulong IndexOffset;
            public IndexType IndexType;
        }

        internal void ResetCommandBufferBindState(CommandBuffer commandBuffer)
        {
            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
                _commandBindStates[key] = default;
        }

        internal void BindPipelineTracked(CommandBuffer commandBuffer, PipelineBindPoint bindPoint, Pipeline pipeline)
        {
            if (pipeline.Handle == 0)
                return;

            bool shouldBind = true;
            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
            {
                _commandBindStates.TryGetValue(key, out CommandBufferBindState state);
                ulong handle = pipeline.Handle;
                if (bindPoint == PipelineBindPoint.Graphics)
                {
                    shouldBind = state.GraphicsPipeline != handle;
                    if (shouldBind)
                        state.GraphicsPipeline = handle;
                }
                else
                {
                    shouldBind = state.ComputePipeline != handle;
                    if (shouldBind)
                        state.ComputePipeline = handle;
                }

                if (shouldBind)
                    _commandBindStates[key] = state;
            }

            if (!shouldBind)
            {
                Engine.Rendering.Stats.RecordVulkanBindChurn(pipelineBindSkips: 1);
                return;
            }

            Api!.CmdBindPipeline(commandBuffer, bindPoint, pipeline);
            Engine.Rendering.Stats.RecordVulkanBindChurn(pipelineBinds: 1);
        }

        internal void BindDescriptorSetsTracked(
            CommandBuffer commandBuffer,
            PipelineBindPoint bindPoint,
            PipelineLayout layout,
            uint firstSet,
            DescriptorSet[] sets)
        {
            if (sets.Length == 0)
                return;

            HashCode hash = new();
            hash.Add((int)bindPoint);
            hash.Add(layout.Handle);
            hash.Add(firstSet);
            for (int i = 0; i < sets.Length; i++)
                hash.Add(sets[i].Handle);

            ulong signature = unchecked((ulong)hash.ToHashCode());
            bool shouldBind = true;
            ulong key = (ulong)commandBuffer.Handle;

            lock (_commandBindStateLock)
            {
                _commandBindStates.TryGetValue(key, out CommandBufferBindState state);
                if (bindPoint == PipelineBindPoint.Graphics)
                {
                    shouldBind = state.GraphicsDescriptorSignature != signature;
                    if (shouldBind)
                        state.GraphicsDescriptorSignature = signature;
                }
                else
                {
                    shouldBind = state.ComputeDescriptorSignature != signature;
                    if (shouldBind)
                        state.ComputeDescriptorSignature = signature;
                }

                if (shouldBind)
                    _commandBindStates[key] = state;
            }

            if (!shouldBind)
            {
                Engine.Rendering.Stats.RecordVulkanBindChurn(descriptorBindSkips: 1);
                return;
            }

            fixed (DescriptorSet* setPtr = sets)
                Api!.CmdBindDescriptorSets(commandBuffer, bindPoint, layout, firstSet, (uint)sets.Length, setPtr, 0, null);

            Engine.Rendering.Stats.RecordVulkanBindChurn(descriptorBinds: 1);
        }

        internal void BindVertexBuffersTracked(
            CommandBuffer commandBuffer,
            uint firstBinding,
            Silk.NET.Vulkan.Buffer[] buffers,
            ulong[] offsets)
        {
            if (buffers.Length == 0)
                return;

            HashCode hash = new();
            hash.Add(firstBinding);
            hash.Add(buffers.Length);
            for (int i = 0; i < buffers.Length; i++)
            {
                hash.Add(buffers[i].Handle);
                hash.Add(offsets[i]);
            }

            ulong signature = unchecked((ulong)hash.ToHashCode());
            bool shouldBind;
            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
            {
                _commandBindStates.TryGetValue(key, out CommandBufferBindState state);
                shouldBind = state.VertexBufferSignature != signature;
                if (shouldBind)
                {
                    state.VertexBufferSignature = signature;
                    _commandBindStates[key] = state;
                }
            }

            if (!shouldBind)
            {
                Engine.Rendering.Stats.RecordVulkanBindChurn(vertexBufferBindSkips: 1);
                return;
            }

            fixed (Silk.NET.Vulkan.Buffer* bufferPtr = buffers)
            fixed (ulong* offsetPtr = offsets)
                Api!.CmdBindVertexBuffers(commandBuffer, firstBinding, (uint)buffers.Length, bufferPtr, offsetPtr);

            Engine.Rendering.Stats.RecordVulkanBindChurn(vertexBufferBinds: 1);
        }

        internal void BindIndexBufferTracked(CommandBuffer commandBuffer, Silk.NET.Vulkan.Buffer indexBuffer, ulong offset, IndexType indexType)
        {
            if (indexBuffer.Handle == 0)
                return;

            bool shouldBind;
            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
            {
                _commandBindStates.TryGetValue(key, out CommandBufferBindState state);
                shouldBind = state.IndexBuffer != indexBuffer.Handle || state.IndexOffset != offset || state.IndexType != indexType;
                if (shouldBind)
                {
                    state.IndexBuffer = indexBuffer.Handle;
                    state.IndexOffset = offset;
                    state.IndexType = indexType;
                    _commandBindStates[key] = state;
                }
            }

            if (!shouldBind)
            {
                Engine.Rendering.Stats.RecordVulkanBindChurn(indexBufferBindSkips: 1);
                return;
            }

            Api!.CmdBindIndexBuffer(commandBuffer, indexBuffer, offset, indexType);
            Engine.Rendering.Stats.RecordVulkanBindChurn(indexBufferBinds: 1);
        }

        private void DestroyCommandBuffers()
        {
            if (_commandBuffers is null)
                return;

            DestroyComputeTransientResources();
            DestroyDeferredSecondaryCommandBuffers();
            DestroyComputeDescriptorCaches();

            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                if (_commandBuffers.Length > 0)
                    Api!.FreeCommandBuffers(device, commandPool, (uint)_commandBuffers.Length, commandBuffersPtr);
            }

            _commandBuffers = null;
            _commandBufferDirtyFlags = null;
        }

        private void DestroyDeferredSecondaryCommandBuffers()
        {
            if (_deferredSecondaryCommandBuffers is null)
                return;

            for (int i = 0; i < _deferredSecondaryCommandBuffers.Length; i++)
                ReleaseDeferredSecondaryCommandBuffers((uint)i);

            _deferredSecondaryCommandBuffers = null;
        }

        private void ReleaseDeferredSecondaryCommandBuffers(uint imageIndex)
        {
            if (_deferredSecondaryCommandBuffers is null || imageIndex >= _deferredSecondaryCommandBuffers.Length)
                return;

            List<DeferredSecondaryCommandBuffer>? deferred = _deferredSecondaryCommandBuffers[imageIndex];
            if (deferred is null || deferred.Count == 0)
                return;

            foreach (DeferredSecondaryCommandBuffer entry in deferred)
            {
                CommandBuffer secondary = entry.CommandBuffer;
                if (secondary.Handle == 0 || entry.Pool.Handle == 0)
                    continue;

                Api!.FreeCommandBuffers(device, entry.Pool, 1, ref secondary);
            }

            deferred.Clear();
        }

        private void DeferSecondaryCommandBufferFree(uint imageIndex, CommandPool pool, CommandBuffer commandBuffer)
        {
            if (commandBuffer.Handle == 0 || pool.Handle == 0)
                return;

            if (_deferredSecondaryCommandBuffers is null || imageIndex >= _deferredSecondaryCommandBuffers.Length)
            {
                Api!.FreeCommandBuffers(device, pool, 1, ref commandBuffer);
                return;
            }

            _deferredSecondaryCommandBuffers[imageIndex] ??= [];
            _deferredSecondaryCommandBuffers[imageIndex]!.Add(new DeferredSecondaryCommandBuffer(pool, commandBuffer));
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
            _deferredSecondaryCommandBuffers = new List<DeferredSecondaryCommandBuffer>[_commandBuffers.Length];
            InitializeComputeDescriptorCaches(_commandBuffers.Length);
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

            ReleaseDeferredSecondaryCommandBuffers(imageIndex);
            Api!.ResetCommandBuffer(commandBuffer, 0);
            CleanupComputeTransientResources(imageIndex);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
            };

            if (Api!.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
                throw new Exception("Failed to begin recording command buffer.");

            ResetCommandBufferBindState(commandBuffer);

            CmdBeginLabel(commandBuffer, $"FrameCmd[{imageIndex}]");

            // Global pending barriers are deferred until the first pass boundary to
            // maintain pass-scoped ordering.  Any remaining global mask is emitted
            // before the first pass barrier group via EmitPassBarriers.

            var ops = DrainFrameOps();
            FrameOpContext initialContext = ops.Length > 0
                ? ops[0].Context
                : CaptureFrameOpContext();
            UpdateResourcePlannerFromContext(initialContext);

            bool singleContext = true;
            for (int i = 1; i < ops.Length; i++)
            {
                if (!Equals(ops[i].Context, initialContext))
                {
                    singleContext = false;
                    break;
                }
            }

            if (singleContext)
                ops = _renderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);

            // Ensure swapchain resources are transitioned appropriately before any rendering.
            CmdBeginLabel(commandBuffer, "SwapchainBarriers");
            var swapchainImageBarriers = _barrierPlanner.GetBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
            var swapchainBufferBarriers = _barrierPlanner.GetBufferBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
            EmitPlannedImageBarriers(commandBuffer, swapchainImageBarriers);
            EmitPlannedBufferBarriers(commandBuffer, swapchainBufferBarriers);
            CmdEndLabel(commandBuffer);

            int clearCount = 0;
            int drawCount = 0;
            int blitCount = 0;
            int computeCount = 0;
            int swapchainWriteCount = 0;
            foreach (var op in ops)
            {
                switch (op)
                {
                    case ClearOp clear:
                        clearCount++;
                        if (clear.Target is null && (clear.ClearColor || clear.ClearDepth || clear.ClearStencil))
                            swapchainWriteCount++;
                        break;
                    case MeshDrawOp meshDraw:
                        drawCount++;
                        if (meshDraw.Target is null)
                            swapchainWriteCount++;
                        break;
                    case BlitOp blit:
                        blitCount++;
                        if (blit.OutFbo is null && (blit.ColorBit || blit.DepthBit || blit.StencilBit))
                            swapchainWriteCount++;
                        break;
                    case ComputeDispatchOp: computeCount++; break;
                }
            }

            Debug.VulkanEvery(
                $"Vulkan.FrameOps.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] FrameOps: total={0} clears={1} draws={2} blits={3} computes={4} swapchainWrites={5}",
                ops.Length,
                clearCount,
                drawCount,
                blitCount,
                computeCount,
                swapchainWriteCount);

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

            void BeginRenderPassForTarget(XRFrameBuffer? target, int passIndex, in FrameOpContext context)
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
                vkFrameBuffer.Generate();

                string fboName = string.IsNullOrWhiteSpace(target.Name)
                    ? $"FBO[{target.GetHashCode()}]"
                    : target.Name!;
                CmdBeginLabel(commandBuffer, $"RenderPass:{fboName}");
                renderPassLabelActive = true;

                RenderPass passRenderPass = vkFrameBuffer.ResolveRenderPassForPass(passIndex, context.PassMetadata);
                RenderPassBeginInfo fboPassInfo = new()
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = passRenderPass,
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
                activeRenderPass = passRenderPass;
                activeFramebuffer = vkFrameBuffer.FrameBuffer;
                activeRenderArea = fboPassInfo.RenderArea;
            }

            void EmitPassBarriers(int passIndex)
            {
                // Emit any global pending memory barriers that accumulated before recording.
                // After the first pass consumes them they are cleared.
                EmitPendingMemoryBarriers(commandBuffer);

                // Emit per-pass memory barriers registered during the frame.
                EMemoryBarrierMask perPassMask = _state.DrainMemoryBarrierForPass(passIndex);
                if (perPassMask != EMemoryBarrierMask.None)
                    EmitMemoryBarrierMask(commandBuffer, perPassMask);

                var imageBarriers = _barrierPlanner.GetBarriersForPass(passIndex);
                var bufferBarriers = _barrierPlanner.GetBufferBarriersForPass(passIndex);
                int queueOwnershipTransfers = 0;
                int stageFlushes = 0;

                for (int i = 0; i < imageBarriers.Count; i++)
                {
                    VulkanBarrierPlanner.PlannedImageBarrier planned = imageBarriers[i];
                    if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                        planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                        planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                    {
                        queueOwnershipTransfers++;
                    }

                    if (planned.Previous.StageMask != planned.Next.StageMask)
                        stageFlushes++;
                }

                for (int i = 0; i < bufferBarriers.Count; i++)
                {
                    VulkanBarrierPlanner.PlannedBufferBarrier planned = bufferBarriers[i];
                    if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                        planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                        planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                    {
                        queueOwnershipTransfers++;
                    }

                    if (planned.Previous.StageMask != planned.Next.StageMask)
                        stageFlushes++;
                }

                if (imageBarriers.Count > 0 || bufferBarriers.Count > 0)
                {
                    CmdBeginLabel(commandBuffer, "PassBarriers");
                    EmitPlannedImageBarriers(commandBuffer, imageBarriers);
                    EmitPlannedBufferBarriers(commandBuffer, bufferBarriers);
                    CmdEndLabel(commandBuffer);

                    Engine.Rendering.Stats.RecordVulkanBarrierPlannerPass(
                        imageBarrierCount: imageBarriers.Count,
                        bufferBarrierCount: bufferBarriers.Count,
                        queueOwnershipTransfers: queueOwnershipTransfers,
                        stageFlushes: stageFlushes);

                    Debug.VulkanEvery(
                        $"Vulkan.PassBarrierSummary.{passIndex}",
                        TimeSpan.FromSeconds(2),
                        "Pass barrier summary: pass={0} image={1} buffer={2} queueTransfers={3} stageFlushes={4}",
                        passIndex,
                        imageBarriers.Count,
                        bufferBarriers.Count,
                        queueOwnershipTransfers,
                        stageFlushes);
                }
            }

            for (int opIndex = 0; opIndex < ops.Length; opIndex++)
            {
                var op = ops[opIndex];
                try
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

                int opPassIndex = EnsureValidPassIndex(op.PassIndex, op.GetType().Name, op.Context.PassMetadata);
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
                        if (_enableSecondaryCommandBuffers)
                        {
                            int runEnd = opIndex;
                            if (_enableParallelSecondaryCommandBufferRecording)
                            {
                                while (runEnd + 1 < ops.Length &&
                                       ops[runEnd + 1] is BlitOp &&
                                       Equals(ops[runEnd + 1].Context, activeContext) &&
                                       EnsureValidPassIndex(ops[runEnd + 1].PassIndex, ops[runEnd + 1].GetType().Name, ops[runEnd + 1].Context.PassMetadata) == opPassIndex &&
                                       ops[runEnd + 1].Context.SchedulingIdentity == opSchedulingIdentity)
                                {
                                    runEnd++;
                                }
                            }

                            int runCount = runEnd - opIndex + 1;
                            if (runCount > 1)
                            {
                                ExecuteSecondaryCommandBufferBatchParallel(
                                    commandBuffer,
                                    "BlitBatch",
                                    runCount,
                                    imageIndex,
                                    (relativeIndex, secondary) =>
                                    {
                                        BlitOp blitOp = (BlitOp)ops[opIndex + relativeIndex];
                                        RecordBlitOp(secondary, imageIndex, blitOp);
                                    });
                                opIndex = runEnd;
                            }
                            else
                            {
                                ExecuteSecondaryCommandBuffer(commandBuffer, "Blit", imageIndex, secondary => RecordBlitOp(secondary, imageIndex, blit));
                            }
                        }
                        else
                        {
                            CmdBeginLabel(commandBuffer, "Blit");
                            RecordBlitOp(commandBuffer, imageIndex, blit);
                            CmdEndLabel(commandBuffer);
                        }
                        break;

                    case ClearOp clear:
                        if (!renderPassActive || activeTarget != clear.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(clear.Target, opPassIndex, activeContext);
                        }
                        RecordClearOp(commandBuffer, imageIndex, clear);
                        break;

                    case MeshDrawOp drawOp:
                        if (!renderPassActive || activeTarget != drawOp.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(drawOp.Target, opPassIndex, activeContext);
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
                        if (_enableSecondaryCommandBuffers)
                        {
                            int runEnd = opIndex;
                            if (_enableParallelSecondaryCommandBufferRecording)
                            {
                                while (runEnd + 1 < ops.Length &&
                                       ops[runEnd + 1] is IndirectDrawOp &&
                                       Equals(ops[runEnd + 1].Context, activeContext) &&
                                       EnsureValidPassIndex(ops[runEnd + 1].PassIndex, ops[runEnd + 1].GetType().Name, ops[runEnd + 1].Context.PassMetadata) == opPassIndex &&
                                       ops[runEnd + 1].Context.SchedulingIdentity == opSchedulingIdentity)
                                {
                                    runEnd++;
                                }
                            }

                            int runCount = runEnd - opIndex + 1;
                            bool useParallelSecondary =
                                _enableParallelSecondaryCommandBufferRecording &&
                                runCount >= Math.Max(_parallelSecondaryIndirectRunThreshold, 2);

                            if (runCount > 1 && useParallelSecondary)
                            {
                                ExecuteSecondaryCommandBufferBatchParallel(
                                    commandBuffer,
                                    "IndirectDrawBatch",
                                    runCount,
                                    imageIndex,
                                    (relativeIndex, secondary) =>
                                    {
                                        IndirectDrawOp runOp = (IndirectDrawOp)ops[opIndex + relativeIndex];
                                        RecordIndirectDrawOp(secondary, runOp);
                                    });
                                Engine.Rendering.Stats.RecordVulkanIndirectRecordingMode(
                                    usedSecondary: true,
                                    usedParallel: true,
                                    opCount: runCount);
                                opIndex = runEnd;
                            }
                            else if (runCount > 1)
                            {
                                for (int relativeIndex = 0; relativeIndex < runCount; relativeIndex++)
                                {
                                    IndirectDrawOp runOp = (IndirectDrawOp)ops[opIndex + relativeIndex];
                                    ExecuteSecondaryCommandBuffer(commandBuffer, "IndirectDraw", imageIndex, secondary => RecordIndirectDrawOp(secondary, runOp));
                                }

                                Engine.Rendering.Stats.RecordVulkanIndirectRecordingMode(
                                    usedSecondary: true,
                                    usedParallel: false,
                                    opCount: runCount);
                                opIndex = runEnd;
                            }
                            else
                            {
                                CmdBeginLabel(commandBuffer, "IndirectDraw");
                                RecordIndirectDrawOp(commandBuffer, indirectOp);
                                CmdEndLabel(commandBuffer);

                                Engine.Rendering.Stats.RecordVulkanIndirectRecordingMode(
                                    usedSecondary: false,
                                    usedParallel: false,
                                    opCount: 1);
                            }
                        }
                        else
                        {
                            CmdBeginLabel(commandBuffer, "IndirectDraw");
                            RecordIndirectDrawOp(commandBuffer, indirectOp);
                            CmdEndLabel(commandBuffer);

                            Engine.Rendering.Stats.RecordVulkanIndirectRecordingMode(
                                usedSecondary: false,
                                usedParallel: false,
                                opCount: 1);
                        }
                        break;

                    case ComputeDispatchOp computeOp:
                        EndActiveRenderPass();
                        if (_enableSecondaryCommandBuffers)
                            ExecuteSecondaryCommandBuffer(commandBuffer, "ComputeDispatch", imageIndex, secondary => RecordComputeDispatchOp(secondary, imageIndex, computeOp));
                        else
                        {
                            CmdBeginLabel(commandBuffer, "ComputeDispatch");
                            RecordComputeDispatchOp(commandBuffer, imageIndex, computeOp);
                            CmdEndLabel(commandBuffer);
                        }
                        break;
                }
                }
                catch (Exception opEx)
                {
                    // If an individual frame op fails to record (e.g. partially-initialised FBO,
                    // shader compilation error during deferred pipeline setup, etc.), skip it and
                    // continue recording the remaining ops.  The final swapchain render pass +
                    // debug triangle / ImGui overlay will still present a valid frame.
                    EndActiveRenderPass();
                    if (renderPassLabelActive)
                    {
                        CmdEndLabel(commandBuffer);
                        renderPassLabelActive = false;
                    }

                    Debug.VulkanEvery(
                        $"Vulkan.FrameOpError.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Frame op recording failed for {0}: {1}",
                        op.GetType().Name,
                        opEx.Message);
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
                BeginRenderPassForTarget(
                    null,
                    activePassIndex != int.MinValue ? activePassIndex : VulkanBarrierPlanner.SwapchainPassIndex,
                    hasActiveContext ? activeContext : initialContext);
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

            if (swapchainWriteCount == 0)
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
                    ImageAspectFlags requestedAspects = ImageAspectFlags.None;
                    if (op.ClearDepth)
                        requestedAspects |= ImageAspectFlags.DepthBit;
                    if (op.ClearStencil)
                        requestedAspects |= ImageAspectFlags.StencilBit;

                    // Only emit aspects actually supported by the swapchain depth attachment view.
                    // Example: VK_FORMAT_D32_SFLOAT does not support stencil clears.
                    ImageAspectFlags aspects = requestedAspects & _swapchainDepthAspect;

                    if (aspects == ImageAspectFlags.None)
                        goto SkipSwapchainDepthClear;

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

            SkipSwapchainDepthClear:

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

        private void RecordBlitOp(CommandBuffer commandBuffer, uint imageIndex, BlitOp op)
        {
            void ExecuteSingleBlit(in BlitImageInfo source, in BlitImageInfo destination, Filter filter)
            {
                ImageBlit region = BuildImageBlit(source, destination, op.InX, op.InY, op.InW, op.InH, op.OutX, op.OutY, op.OutW, op.OutH);

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

            bool copiedAny = false;

            if (op.ColorBit &&
                TryResolveBlitImage(op.InFbo, imageIndex, op.ReadBufferMode, wantColor: true, wantDepth: false, wantStencil: false, out var colorSource, isSource: true) &&
                TryResolveBlitImage(op.OutFbo, imageIndex, EReadBufferMode.ColorAttachment0, wantColor: true, wantDepth: false, wantStencil: false, out var colorDestination, isSource: false))
            {
                ExecuteSingleBlit(colorSource, colorDestination, op.LinearFilter ? Filter.Linear : Filter.Nearest);
                copiedAny = true;
            }

            if ((op.DepthBit || op.StencilBit) &&
                TryResolveBlitImage(op.InFbo, imageIndex, op.ReadBufferMode, wantColor: false, wantDepth: op.DepthBit, wantStencil: op.StencilBit, out var depthSource, isSource: true) &&
                TryResolveBlitImage(op.OutFbo, imageIndex, EReadBufferMode.None, wantColor: false, wantDepth: op.DepthBit, wantStencil: op.StencilBit, out var depthDestination, isSource: false))
            {
                // Vulkan only supports nearest filtering for depth/stencil blits.
                ExecuteSingleBlit(depthSource, depthDestination, Filter.Nearest);
                copiedAny = true;
            }

            if (!copiedAny)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.Blit.NoAttachment",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Blit skipped: unable to resolve source/destination attachments for requested masks (Color={0}, Depth={1}, Stencil={2}).",
                    op.ColorBit,
                    op.DepthBit,
                    op.StencilBit);
            }
        }

        private bool PlannerCoversIndirectBufferTransition(int passIndex, Silk.NET.Vulkan.Buffer indirectBuffer)
        {
            IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier> plannedBarriers = _barrierPlanner.GetBufferBarriersForPass(passIndex);
            if (plannedBarriers.Count == 0)
                return false;

            for (int i = 0; i < plannedBarriers.Count; i++)
            {
                VulkanBarrierPlanner.PlannedBufferBarrier planned = plannedBarriers[i];
                if (!TryResolveTrackedBuffer(planned.ResourceName, out Silk.NET.Vulkan.Buffer plannedBuffer, out _))
                    continue;

                if (plannedBuffer.Handle != indirectBuffer.Handle)
                    continue;

                bool transitionsToIndirectRead =
                    (planned.Next.AccessMask & AccessFlags.IndirectCommandReadBit) != 0 ||
                    (planned.Next.StageMask & PipelineStageFlags.DrawIndirectBit) != 0;

                if (transitionsToIndirectRead)
                    return true;
            }

            return false;
        }

        private void RecordIndirectDrawOp(CommandBuffer commandBuffer, IndirectDrawOp op)
        {
            var indirectBuffer = op.IndirectBuffer.BufferHandle;
            if (indirectBuffer is null || !indirectBuffer.HasValue)
            {
                Debug.VulkanWarning("RecordIndirectDrawOp: Invalid indirect buffer.");
                return;
            }

            bool plannerCoversIndirectBarrier = PlannerCoversIndirectBufferTransition(op.PassIndex, indirectBuffer.Value);
            if (!plannerCoversIndirectBarrier)
            {
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

                Engine.Rendering.Stats.RecordVulkanAdhocBarrier(emittedCount: 1, redundantCount: 0);
            }
            else
            {
                Engine.Rendering.Stats.RecordVulkanAdhocBarrier(emittedCount: 0, redundantCount: 1);
                Debug.VulkanWarningEvery(
                    "Vulkan.IndirectBarrier.Overlap",
                    TimeSpan.FromSeconds(2),
                    "Indirect barrier overlap detected and suppressed: pass={0} drawCount={1}",
                    op.PassIndex,
                    op.DrawCount);
            }

            // Calculate the byte offset into the indirect buffer
            ulong bufferOffset = op.ByteOffset;

            if (op.DrawCount == 0)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.Indirect.ZeroDrawCount",
                    TimeSpan.FromSeconds(1),
                    "RecordIndirectDrawOp skipped: drawCount was zero.");
                return;
            }

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

                Engine.Rendering.Stats.RecordVulkanIndirectSubmission(
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

                Engine.Rendering.Stats.RecordVulkanIndirectSubmission(
                    usedCountPath: false,
                    usedLoopFallback: false,
                    apiCalls: 1,
                    submittedDraws: op.DrawCount);
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

            BindPipelineTracked(commandBuffer, PipelineBindPoint.Compute, pipeline);

            if (op.Program.TryBuildAndBindComputeDescriptorSets(commandBuffer, imageIndex, op.Snapshot, out DescriptorPool descriptorPool, out var tempBuffers))
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

            EmitMemoryBarrierMask(commandBuffer, pendingMask);
            _state.ClearPendingMemoryBarrierMask();
        }

        /// <summary>
        /// Emits a <c>vkCmdPipelineBarrier</c> for the given <see cref="EMemoryBarrierMask"/>.
        /// Used both for global pending barriers and per-pass barriers.
        /// </summary>
        private void EmitMemoryBarrierMask(CommandBuffer commandBuffer, EMemoryBarrierMask mask)
        {
            if (mask == EMemoryBarrierMask.None)
                return;

            ResolveBarrierScopes(mask, out PipelineStageFlags srcStages, out PipelineStageFlags dstStages, out AccessFlags srcAccess, out AccessFlags dstAccess);

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
                    SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
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

        private void EmitPlannedBufferBarriers(CommandBuffer commandBuffer, IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier>? plannedBarriers)
        {
            if (plannedBarriers is null || plannedBarriers.Count == 0)
                return;

            foreach (VulkanBarrierPlanner.PlannedBufferBarrier planned in plannedBarriers)
            {
                if (!TryResolveTrackedBuffer(planned.ResourceName, out Silk.NET.Vulkan.Buffer buffer, out ulong size) || buffer.Handle == 0)
                    continue;

                BufferMemoryBarrier barrier = new()
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = planned.Previous.AccessMask,
                    DstAccessMask = planned.Next.AccessMask,
                    SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                    Buffer = buffer,
                    Offset = 0,
                    Size = size > 0 ? size : Vk.WholeSize
                };

                Api!.CmdPipelineBarrier(
                    commandBuffer,
                    planned.Previous.StageMask,
                    planned.Next.StageMask,
                    DependencyFlags.None,
                    0,
                    null,
                    1,
                    &barrier,
                    0,
                    null);
            }
        }

        private void ExecuteSecondaryCommandBuffer(CommandBuffer primaryCommandBuffer, string label, uint imageIndex, Action<CommandBuffer> recorder)
        {
            CmdBeginLabel(primaryCommandBuffer, label);
            CommandBuffer secondary = default;
            bool allocated = false;
            CommandPool pool = default;
            bool executedInPrimary = false;

            try
            {
                pool = GetThreadCommandPool();

                CommandBufferAllocateInfo allocInfo = new()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = pool,
                    Level = CommandBufferLevel.Secondary,
                    CommandBufferCount = 1
                };

                Api!.AllocateCommandBuffers(device, ref allocInfo, out secondary);
                allocated = secondary.Handle != 0;

                CommandBufferBeginInfo beginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit
                };

                CommandBufferInheritanceInfo inheritanceInfo = new()
                {
                    SType = StructureType.CommandBufferInheritanceInfo,
                    RenderPass = default,
                    Subpass = 0,
                    Framebuffer = default,
                    OcclusionQueryEnable = Vk.False,
                    QueryFlags = QueryControlFlags.None,
                    PipelineStatistics = QueryPipelineStatisticFlags.None
                };

                beginInfo.PInheritanceInfo = &inheritanceInfo;

                if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                    throw new Exception("Failed to begin Vulkan secondary command buffer.");

                ResetCommandBufferBindState(secondary);

                recorder(secondary);

                if (Api!.EndCommandBuffer(secondary) != Result.Success)
                    throw new Exception("Failed to end Vulkan secondary command buffer.");

                Api!.CmdExecuteCommands(primaryCommandBuffer, 1, &secondary);
                executedInPrimary = true;
            }
            finally
            {
                if (allocated && pool.Handle != 0)
                {
                    if (executedInPrimary)
                        DeferSecondaryCommandBufferFree(imageIndex, pool, secondary);
                    else
                        Api!.FreeCommandBuffers(device, pool, 1, ref secondary);
                }

                CmdEndLabel(primaryCommandBuffer);
            }
        }

        private void ExecuteSecondaryCommandBufferBatchParallel(
            CommandBuffer primaryCommandBuffer,
            string label,
            int count,
            uint imageIndex,
            Action<int, CommandBuffer> recorder)
        {
            if (count <= 0)
                return;

            if (count == 1)
            {
                ExecuteSecondaryCommandBuffer(primaryCommandBuffer, label, imageIndex, cmd => recorder(0, cmd));
                return;
            }

            CmdBeginLabel(primaryCommandBuffer, label);
            CommandBuffer[] secondaryBuffers = new CommandBuffer[count];
            CommandPool[] ownerPools = new CommandPool[count];
            bool[] allocated = new bool[count];
            Exception? firstError = null;
            object errorLock = new();
            bool executedInPrimary = false;

            try
            {
                Task[] tasks = new Task[count];
                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    tasks[index] = Task.Run(() =>
                    {
                        if (firstError is not null)
                            return;

                        CommandBuffer secondary = default;
                        bool localAllocated = false;
                        CommandPool pool = default;

                        try
                        {
                            pool = GetThreadCommandPool();
                            CommandBufferAllocateInfo allocInfo = new()
                            {
                                SType = StructureType.CommandBufferAllocateInfo,
                                CommandPool = pool,
                                Level = CommandBufferLevel.Secondary,
                                CommandBufferCount = 1
                            };

                            Api!.AllocateCommandBuffers(device, ref allocInfo, out secondary);
                            localAllocated = secondary.Handle != 0;

                            CommandBufferBeginInfo beginInfo = new()
                            {
                                SType = StructureType.CommandBufferBeginInfo,
                                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
                            };

                            CommandBufferInheritanceInfo inheritanceInfo = new()
                            {
                                SType = StructureType.CommandBufferInheritanceInfo,
                                RenderPass = default,
                                Subpass = 0,
                                Framebuffer = default,
                                OcclusionQueryEnable = Vk.False,
                                QueryFlags = QueryControlFlags.None,
                                PipelineStatistics = QueryPipelineStatisticFlags.None
                            };

                            beginInfo.PInheritanceInfo = &inheritanceInfo;

                            if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                                throw new Exception("Failed to begin Vulkan secondary command buffer.");

                            ResetCommandBufferBindState(secondary);

                            recorder(index, secondary);

                            if (Api!.EndCommandBuffer(secondary) != Result.Success)
                                throw new Exception("Failed to end Vulkan secondary command buffer.");

                            secondaryBuffers[index] = secondary;
                            ownerPools[index] = pool;
                            allocated[index] = localAllocated;
                        }
                        catch (Exception ex)
                        {
                            lock (errorLock)
                            {
                                firstError ??= ex;
                            }

                            if (localAllocated && pool.Handle != 0)
                            {
                                try
                                {
                                    Api!.FreeCommandBuffers(device, pool, 1, ref secondary);
                                }
                                catch
                                {
                                }
                            }
                        }
                    });
                }

                Task.WaitAll(tasks);

                if (firstError is not null)
                    throw firstError;

                fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                    Api!.CmdExecuteCommands(primaryCommandBuffer, (uint)count, secondaryPtr);

                executedInPrimary = true;
            }
            finally
            {
                for (int i = 0; i < count; i++)
                {
                    if (!allocated[i] || ownerPools[i].Handle == 0 || secondaryBuffers[i].Handle == 0)
                        continue;

                    if (executedInPrimary)
                        DeferSecondaryCommandBufferFree(imageIndex, ownerPools[i], secondaryBuffers[i]);
                    else
                        Api!.FreeCommandBuffers(device, ownerPools[i], 1, ref secondaryBuffers[i]);
                }

                CmdEndLabel(primaryCommandBuffer);
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
            CommandPool pool = GetThreadCommandPool();

            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = pool,
                CommandBufferCount = 1,
            };

            Api!.AllocateCommandBuffers(device, ref allocateInfo, out CommandBuffer commandBuffer);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            Api!.BeginCommandBuffer(commandBuffer, ref beginInfo);
            ResetCommandBufferBindState(commandBuffer);

            lock (_oneTimeCommandPoolsLock)
                _oneTimeCommandPools[commandBuffer.Handle] = pool;

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

            lock (_oneTimeSubmitLock)
            {
                Api!.QueueSubmit(graphicsQueue, 1, ref submitInfo, default);
                Api!.QueueWaitIdle(graphicsQueue);
            }

            CommandPool pool = GetThreadCommandPool();
            lock (_oneTimeCommandPoolsLock)
            {
                if (_oneTimeCommandPools.Remove(commandBuffer.Handle, out CommandPool ownerPool) && ownerPool.Handle != 0)
                    pool = ownerPool;
            }

            Api!.FreeCommandBuffers(device, pool, 1, ref commandBuffer);
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
