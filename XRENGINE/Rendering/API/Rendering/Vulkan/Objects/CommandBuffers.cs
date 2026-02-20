using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private CommandBuffer[]? _commandBuffers;
        private ulong[]? _commandBufferFrameOpSignatures;
        private ulong[]? _commandBufferPlannerRevisions;
        private ComputeTransientResources[]? _computeTransientResources;
        private List<DeferredSecondaryCommandBuffer>[]? _deferredSecondaryCommandBuffers;
        private readonly object _oneTimeCommandPoolsLock = new();
        private readonly Dictionary<nint, OneTimeCommandOwner> _oneTimeCommandPools = new();
        private readonly object _oneTimeSubmitLock = new();
        private int _oneTimeGraphicsSubmitCounter;
        private readonly object _commandBindStateLock = new();
        private readonly Dictionary<ulong, CommandBufferBindState> _commandBindStates = new();
        private bool _enableSecondaryCommandBuffers = true;
        private bool _enableParallelSecondaryCommandBufferRecording = true;
        private int _parallelSecondaryIndirectRunThreshold = 4;
        private string? _vulkanDiagnosticBaseWindowTitle;
        private string? _vulkanDiagnosticLastTitle;
        private int _vulkanLastFrameDroppedDrawOps;
        private int _vulkanLastFrameDroppedOps;

        private void UpdateVulkanOnScreenDiagnostic(string pipelineLabel, ColorF4 clearColor, int droppedDrawOps, int droppedOps, string swapchainWriter)
        {
            string currentTitle = Window?.Title ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_vulkanDiagnosticBaseWindowTitle))
                _vulkanDiagnosticBaseWindowTitle = currentTitle;

            string baseTitle = _vulkanDiagnosticBaseWindowTitle ?? string.Empty;
            string diagnosticTitle =
                $"{baseTitle} | VK[{pipelineLabel}] clr=({clearColor.R:F2},{clearColor.G:F2},{clearColor.B:F2},{clearColor.A:F2}) sw={swapchainWriter} dropDraw={droppedDrawOps} dropOps={droppedOps}";

            if (string.Equals(_vulkanDiagnosticLastTitle, diagnosticTitle, StringComparison.Ordinal) &&
                _vulkanLastFrameDroppedDrawOps == droppedDrawOps &&
                _vulkanLastFrameDroppedOps == droppedOps)
            {
                return;
            }

            _vulkanDiagnosticLastTitle = diagnosticTitle;
            _vulkanLastFrameDroppedDrawOps = droppedDrawOps;
            _vulkanLastFrameDroppedOps = droppedOps;
            if (Window is not null)
                Window.Title = diagnosticTitle;
        }

        private sealed class ComputeTransientResources
        {
            public DescriptorPool ActiveDescriptorPool;
            public List<DescriptorPool> DescriptorPools { get; } = [];
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

        private readonly struct OneTimeCommandOwner
        {
            public OneTimeCommandOwner(CommandPool pool, bool useTransferQueue)
            {
                Pool = pool;
                UseTransferQueue = useTransferQueue;
            }

            public CommandPool Pool { get; }
            public bool UseTransferQueue { get; }
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

        internal void RemoveCommandBufferBindState(CommandBuffer commandBuffer)
        {
            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
                _commandBindStates.Remove(key);
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
            _commandBufferFrameOpSignatures = null;
            _commandBufferPlannerRevisions = null;
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
                RemoveCommandBufferBindState(secondary);
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

            ComputeTransientResources? resources = _computeTransientResources[imageIndex];
            if (resources is null)
                return;

            foreach (DescriptorPool descriptorPool in resources.DescriptorPools)
            {
                if (descriptorPool.Handle != 0)
                    Api!.DestroyDescriptorPool(device, descriptorPool, null);
            }

            resources.DescriptorPools.Clear();
            resources.ActiveDescriptorPool = default;

            foreach ((Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) in resources.UniformBuffers)
            {
                if (buffer.Handle != 0)
                    Api!.DestroyBuffer(device, buffer, null);
                if (memory.Handle != 0)
                    Api!.FreeMemory(device, memory, null);
            }

            resources.UniformBuffers.Clear();
        }

        internal bool TryAllocateTransientComputeDescriptorSets(
            uint imageIndex,
            DescriptorSetLayout[] setLayouts,
            DescriptorPoolSize[] poolSizes,
            bool requireUpdateAfterBind,
            out DescriptorSet[] descriptorSets)
        {
            descriptorSets = Array.Empty<DescriptorSet>();
            if (setLayouts.Length == 0)
                return false;

            if (_computeTransientResources is null || imageIndex >= _computeTransientResources.Length)
                return false;

            ComputeTransientResources resources = _computeTransientResources[imageIndex] ??= new ComputeTransientResources();

            bool TryAllocateFromPool(DescriptorPool pool, out DescriptorSet[] sets)
            {
                sets = new DescriptorSet[setLayouts.Length];
                fixed (DescriptorSetLayout* layoutPtr = setLayouts)
                fixed (DescriptorSet* setPtr = sets)
                {
                    DescriptorSetAllocateInfo allocInfo = new()
                    {
                        SType = StructureType.DescriptorSetAllocateInfo,
                        DescriptorPool = pool,
                        DescriptorSetCount = (uint)setLayouts.Length,
                        PSetLayouts = layoutPtr,
                    };

                    Result allocResult = Api!.AllocateDescriptorSets(device, ref allocInfo, setPtr);
                    if (allocResult == Result.Success)
                        return true;

                    sets = Array.Empty<DescriptorSet>();
                    return false;
                }
            }

            if (resources.ActiveDescriptorPool.Handle != 0 && TryAllocateFromPool(resources.ActiveDescriptorPool, out descriptorSets))
                return true;

            uint descriptorScale = 8u;
            DescriptorPoolSize[] scaledPoolSizes = new DescriptorPoolSize[poolSizes.Length];
            for (int i = 0; i < poolSizes.Length; i++)
            {
                DescriptorPoolSize source = poolSizes[i];
                uint scaledCount = Math.Max(source.DescriptorCount * descriptorScale, source.DescriptorCount);
                scaledPoolSizes[i] = new DescriptorPoolSize
                {
                    Type = source.Type,
                    DescriptorCount = scaledCount
                };
            }

            DescriptorPoolCreateFlags poolFlags = requireUpdateAfterBind
                ? DescriptorPoolCreateFlags.UpdateAfterBindBit
                : 0;

            fixed (DescriptorPoolSize* poolSizesPtr = scaledPoolSizes)
            {
                DescriptorPoolCreateInfo poolInfo = new()
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    Flags = poolFlags,
                    MaxSets = Math.Max((uint)setLayouts.Length * descriptorScale, 32u),
                    PoolSizeCount = (uint)scaledPoolSizes.Length,
                    PPoolSizes = poolSizesPtr,
                };

                if (Api!.CreateDescriptorPool(device, ref poolInfo, null, out DescriptorPool descriptorPool) != Result.Success)
                    return false;

                resources.DescriptorPools.Add(descriptorPool);
                resources.ActiveDescriptorPool = descriptorPool;
            }

            return TryAllocateFromPool(resources.ActiveDescriptorPool, out descriptorSets);
        }

        private void RegisterComputeTransientUniformBuffers(
            uint imageIndex,
            IReadOnlyList<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)> uniformBuffers)
        {
            if (uniformBuffers is not { Count: > 0 })
                return;

            if (_computeTransientResources is null || imageIndex >= _computeTransientResources.Length)
            {
                foreach ((Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) in uniformBuffers)
                {
                    if (buffer.Handle != 0)
                        Api!.DestroyBuffer(device, buffer, null);

                    if (memory.Handle != 0)
                        Api!.FreeMemory(device, memory, null);
                }

                return;
            }

            ComputeTransientResources resources = _computeTransientResources[imageIndex] ??= new ComputeTransientResources();
            resources.UniformBuffers.AddRange(uniformBuffers);
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
            _computeTransientResources = new ComputeTransientResources[_commandBuffers.Length];
            _deferredSecondaryCommandBuffers = new List<DeferredSecondaryCommandBuffer>[_commandBuffers.Length];
            InitializeComputeDescriptorCaches(_commandBuffers.Length);
        }

        private bool TryEnsureCommandBuffersForSwapchain()
        {
            if (swapChainFramebuffers is null || swapChainFramebuffers.Length == 0)
                return false;

            bool needsAllocation =
                _commandBuffers is null ||
                _commandBufferDirtyFlags is null ||
                _commandBuffers.Length != swapChainFramebuffers.Length ||
                _commandBufferDirtyFlags.Length != swapChainFramebuffers.Length;

            if (!needsAllocation)
                return true;

            if (_commandBuffers is not null)
                DestroyCommandBuffers();

            CreateCommandBuffers();

            return _commandBuffers is not null &&
                _commandBufferDirtyFlags is not null &&
                _commandBuffers.Length == swapChainFramebuffers.Length &&
                _commandBufferDirtyFlags.Length == swapChainFramebuffers.Length;
        }

        private void EnsureCommandBufferRecorded(uint imageIndex)
        {
            if (!TryEnsureCommandBuffersForSwapchain())
                throw new InvalidOperationException("Command buffers are unavailable because swapchain framebuffers are not initialised.");

            if (_commandBuffers is null)
                throw new InvalidOperationException("Command buffers have not been allocated yet.");

            if (imageIndex >= _commandBuffers.Length)
                throw new InvalidOperationException($"Command buffer index {imageIndex} is out of range for {_commandBuffers.Length} allocated command buffers.");

            if (_commandBufferDirtyFlags is null || imageIndex >= _commandBufferDirtyFlags.Length)
                throw new InvalidOperationException("Command buffer dirty flags are not initialised correctly.");

            if (_commandBufferFrameOpSignatures is null || imageIndex >= _commandBufferFrameOpSignatures.Length)
                throw new InvalidOperationException("Command buffer frame-op signatures are not initialised correctly.");

            if (_commandBufferPlannerRevisions is null || imageIndex >= _commandBufferPlannerRevisions.Length)
                throw new InvalidOperationException("Command buffer planner revisions are not initialised correctly.");

            bool dirty = _commandBufferDirtyFlags[imageIndex];
            var ops = DrainFrameOps(out ulong frameOpsSignature);
            bool hasFrameOps = ops.Length > 0;

            FrameOpContext plannerContext = hasFrameOps
                ? ops[0].Context
                : CaptureFrameOpContext();

            UpdateResourcePlannerFromContext(plannerContext);
            ulong plannerRevision = ResourcePlannerRevision;

            if (!dirty && hasFrameOps && _commandBufferFrameOpSignatures[imageIndex] != frameOpsSignature)
                dirty = true;

            if (!dirty && _commandBufferPlannerRevisions[imageIndex] != plannerRevision)
                dirty = true;

            if (!dirty)
                return;

            RecordCommandBuffer(imageIndex, ops);
            _commandBufferDirtyFlags[imageIndex] = false;
            _commandBufferFrameOpSignatures[imageIndex] = frameOpsSignature;
            _commandBufferPlannerRevisions[imageIndex] = plannerRevision;
        }

        private void RecordCommandBuffer(uint imageIndex, FrameOp[] ops)
        {
            var commandBuffer = _commandBuffers![imageIndex];
            int droppedDrawOps = 0;
            int droppedFrameOps = 0;

            ReleaseDeferredSecondaryCommandBuffers(imageIndex);
            Api!.ResetCommandBuffer(commandBuffer, 0);
            CleanupComputeTransientResources(imageIndex);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
            };

            if (Api!.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
                throw new Exception("Failed to begin recording command buffer.");

            BeginFrameTimingQueries(commandBuffer, currentFrame);

            ResetCommandBufferBindState(commandBuffer);

            CmdBeginLabel(commandBuffer, $"FrameCmd[{imageIndex}]");

            // Global pending barriers are deferred until the first pass boundary to
            // maintain pass-scoped ordering.  Any remaining global mask is emitted
            // before the first pass barrier group via EmitPassBarriers.

            FrameOpContext initialContext = ops.Length > 0
                ? ops[0].Context
                : CaptureFrameOpContext();

            // Coalesce swapchain-targeting ops into a single context to avoid
            // render-pass restarts across pipeline boundaries.  Context changes
            // between pipelines that all render to the swapchain cause
            // EndActiveRenderPass + BeginRenderPassForTarget cycles that can lose
            // composited content (e.g. the skybox turns black).  FBO-targeting ops
            // keep their original context for correct barrier/resource planning.
            VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);

            // Always sort frame ops by (GroupOrder, PassOrder, OriginalIndex).
            // GroupOrder preserves inter-pipeline enqueue order while grouping ops
            // from the same pipeline together.  Previously sorting was limited to
            // single-context frames, but multi-context frames (e.g.
            // DebugOpaqueRenderPipeline + UserInterfaceRenderPipeline) also need
            // correct pass ordering to avoid render-pass restarts that clear
            // composited swapchain content (e.g. the skybox turning black).
            ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);

            IReadOnlyList<VulkanRenderGraphCompiler.SecondaryRecordingBucket> secondaryBuckets =
                _renderGraphCompiler.BuildSecondaryRecordingBuckets(ops);
            Dictionary<int, VulkanRenderGraphCompiler.SecondaryRecordingBucket>? secondaryBucketByStart = null;
            if (secondaryBuckets.Count > 0)
            {
                secondaryBucketByStart = new Dictionary<int, VulkanRenderGraphCompiler.SecondaryRecordingBucket>(secondaryBuckets.Count);
                foreach (VulkanRenderGraphCompiler.SecondaryRecordingBucket bucket in secondaryBuckets)
                    secondaryBucketByStart[bucket.StartIndex] = bucket;
            }

            // Ensure swapchain resources are transitioned appropriately before any rendering.
            CmdBeginLabel(commandBuffer, "SwapchainBarriers");
            var swapchainImageBarriers = _barrierPlanner.GetBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
            var swapchainBufferBarriers = _barrierPlanner.GetBufferBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
            EmitPlannedImageBarriers(commandBuffer, swapchainImageBarriers);
            EmitPlannedBufferBarriers(commandBuffer, swapchainBufferBarriers);
            CmdEndLabel(commandBuffer);

            // Transition any freshly-allocated physical images from UNDEFINED to
            // a safe initial layout so that render passes never see UNDEFINED.
            EmitInitialImageBarriersForUnknownPass(commandBuffer);

            int clearCount = 0;
            int drawCount = 0;
            int blitCount = 0;
            int computeCount = 0;
            int swapchainWriteCount = 0;
            int swapchainClearWrites = 0;
            int swapchainDrawWrites = 0;
            int swapchainBlitWrites = 0;
            string swapchainLastWriter = "None";
            int swapchainLastWriterPass = int.MinValue;
            int swapchainLastWriterOpIndex = -1;

            // Per-pipeline context identity tracking for swapchain writes
            Dictionary<int, int> swapchainWritesByPipeline = [];
            Dictionary<int, string> swapchainWriterLabelByPipeline = [];
            Dictionary<int, string> pipelineNameByIdentity = [];

            void RememberPipelineName(in FrameOpContext context)
            {
                if (!pipelineNameByIdentity.ContainsKey(context.PipelineIdentity))
                {
                    string name = context.PipelineInstance?.Pipeline?.GetType().Name;
                    if (string.IsNullOrWhiteSpace(name))
                        name = "UnknownPipeline";
                    pipelineNameByIdentity[context.PipelineIdentity] = name;
                }
            }

            void MarkSwapchainWriter(string writerLabel, int passIndex, int opIndex, int pipelineIdentity)
            {
                swapchainLastWriter = writerLabel;
                swapchainLastWriterPass = passIndex;
                swapchainLastWriterOpIndex = opIndex;
                swapchainWritesByPipeline.TryGetValue(pipelineIdentity, out int count);
                swapchainWritesByPipeline[pipelineIdentity] = count + 1;
                swapchainWriterLabelByPipeline[pipelineIdentity] = writerLabel;
            }

            void LogSwapchainWritersByPipeline(string phase)
            {
                if (swapchainWritesByPipeline.Count == 0)
                    return;

                string byPipeline = string.Join(", ",
                    swapchainWritesByPipeline
                        .OrderByDescending(kv => kv.Value)
                        .Take(6)
                        .Select(kv =>
                        {
                            string label = swapchainWriterLabelByPipeline.TryGetValue(kv.Key, out string? l)
                                ? l
                                : "Unknown";
                            string pipelineName = pipelineNameByIdentity.TryGetValue(kv.Key, out string? n)
                                ? n
                                : "UnknownPipeline";
                            return $"{label}#P{kv.Key}[{pipelineName}]:{kv.Value}";
                        }));

                Debug.VulkanEvery(
                    $"Vulkan.FrameOpsByPipeline.{phase}.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Swapchain writers by pipeline ({0}): {1}",
                    phase,
                    byPipeline);
            }

            int opScanIndex = 0;
            foreach (var op in ops)
            {
                switch (op)
                {
                    case ClearOp clear:
                        RememberPipelineName(clear.Context);
                        clearCount++;
                        if (clear.Target is null && (clear.ClearColor || clear.ClearDepth || clear.ClearStencil))
                        {
                            swapchainWriteCount++;
                            swapchainClearWrites++;
                            MarkSwapchainWriter(nameof(ClearOp), clear.PassIndex, opScanIndex, clear.Context.PipelineIdentity);
                        }
                        break;
                    case MeshDrawOp meshDraw:
                        RememberPipelineName(meshDraw.Context);
                        drawCount++;
                        if (meshDraw.Target is null)
                        {
                            swapchainWriteCount++;
                            swapchainDrawWrites++;
                            MarkSwapchainWriter(nameof(MeshDrawOp), meshDraw.PassIndex, opScanIndex, meshDraw.Context.PipelineIdentity);
                        }
                        break;
                    case BlitOp blit:
                        RememberPipelineName(blit.Context);
                        blitCount++;
                        if (blit.OutFbo is null && (blit.ColorBit || blit.DepthBit || blit.StencilBit))
                        {
                            swapchainWriteCount++;
                            swapchainBlitWrites++;
                            MarkSwapchainWriter(nameof(BlitOp), blit.PassIndex, opScanIndex, blit.Context.PipelineIdentity);
                        }
                        break;
                    case ComputeDispatchOp: computeCount++; break;
                }

                opScanIndex++;
            }

            Debug.VulkanEvery(
                $"Vulkan.FrameOps.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] FrameOps: total={0} clears={1} draws={2} blits={3} computes={4} swapchainWrites={5} (C{6}/D{7}/B{8})",
                ops.Length,
                clearCount,
                drawCount,
                blitCount,
                computeCount,
                swapchainWriteCount,
                swapchainClearWrites,
                swapchainDrawWrites,
                swapchainBlitWrites);

            LogSwapchainWritersByPipeline("PreOverlay");

            bool renderPassActive = false;
            bool activeDynamicRendering = false;
            XRFrameBuffer? activeTarget = null;
            RenderPass activeRenderPass = default;
            Framebuffer activeFramebuffer = default;
            Rect2D activeRenderArea = default;
            int activePassIndex = int.MinValue;
            int activeSchedulingIdentity = int.MinValue;
            FrameOpContext activeContext = default;
            bool hasActiveContext = false;
            FrameOpContext plannerContext = default;
            bool hasPlannerContext = false;
            bool renderPassLabelActive = false;
            bool passIndexLabelActive = false;
            IDisposable? activePipelineOverrideScope = null;

            // Track whether the swapchain has already had its first render pass
            // this frame. Subsequent re-entries (e.g. after a compute dispatch
            // forced EndActiveRenderPass) use LoadOp.Load to preserve contents
            // instead of clearing the composited scene.
            bool swapchainClearedThisFrame = false;

            bool skipUiPipelineOps = string.Equals(
                Environment.GetEnvironmentVariable("XRE_SKIP_UI_PIPELINE"),
                "1",
                StringComparison.Ordinal);

            // Track swapchain writes that happen outside a swapchain render pass
            // (e.g. CmdBlitImage to swapchain). If true, the first swapchain render
            // pass this frame must Load existing color instead of clearing.
            bool swapchainWrittenOutsideRenderPass = false;

            // Track per-FBO attachment layouts across render-pass restarts within
            // the current command buffer.  On first use the layouts are null
            // (→ initialLayout = Undefined);  after EndActiveRenderPass we store
            // the finalLayout of each attachment so the next BeginRenderPassForTarget
            // can set initialLayout correctly and preserve content across passes.
            Dictionary<XRFrameBuffer, ImageLayout[]> fboLayoutTracking = [];

            void ApplyPipelineOverride(in FrameOpContext context)
            {
                activePipelineOverrideScope?.Dispose();
                activePipelineOverrideScope = Engine.Rendering.State.PushRenderingPipelineOverride(context.PipelineInstance);
            }

            void EndActiveRenderPass()
            {
                if (!renderPassActive)
                    return;

                bool transitionSwapchainToPresent = activeDynamicRendering && activeTarget is null;
                if (activeDynamicRendering)
                {
                    Api!.CmdEndRendering(commandBuffer);

                    if (transitionSwapchainToPresent && swapChainImages is not null && imageIndex < swapChainImages.Length)
                    {
                        ImageMemoryBarrier presentBarrier = new()
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                            DstAccessMask = 0,
                            OldLayout = ImageLayout.ColorAttachmentOptimal,
                            NewLayout = ImageLayout.PresentSrcKhr,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = swapChainImages[imageIndex],
                            SubresourceRange = new ImageSubresourceRange
                            {
                                AspectMask = ImageAspectFlags.ColorBit,
                                BaseMipLevel = 0,
                                LevelCount = 1,
                                BaseArrayLayer = 0,
                                LayerCount = 1
                            }
                        };

                        Api!.CmdPipelineBarrier(
                            commandBuffer,
                            PipelineStageFlags.ColorAttachmentOutputBit,
                            PipelineStageFlags.BottomOfPipeBit,
                            0,
                            0,
                            null,
                            0,
                            null,
                            1,
                            &presentBarrier);
                    }
                }
                else
                {
                    // Update physical group layout tracking for FBO attachment images.
                    // The render pass transitions each attachment from initialLayout to
                    // finalLayout, so after CmdEndRenderPass the images are in their
                    // finalLayout. We update the tracked layout so that subsequent blit
                    // barriers use the correct OldLayout.
                    if (activeTarget is not null)
                    {
                        UpdatePhysicalGroupLayoutsForFbo(activeTarget);

                        // Record the finalLayout of each attachment so the NEXT render
                        // pass on this FBO can set initialLayout correctly and preserve
                        // content across pass boundaries.
                        var vkFbo = GenericToAPI<VkFrameBuffer>(activeTarget);
                        if (vkFbo is not null)
                            fboLayoutTracking[activeTarget] = vkFbo.GetFinalLayouts();
                    }

                    Api!.CmdEndRenderPass(commandBuffer);
                }

                if (renderPassLabelActive)
                {
                    CmdEndLabel(commandBuffer);
                    renderPassLabelActive = false;
                }
                renderPassActive = false;
                activeDynamicRendering = false;
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
                    bool useDynamicRendering = SupportsDynamicRendering &&
                        swapChainImageViews is not null &&
                        swapChainImages is not null &&
                        imageIndex < swapChainImageViews.Length &&
                        imageIndex < swapChainImages.Length;

                    CmdBeginLabel(commandBuffer, useDynamicRendering ? "Rendering:Swapchain" : "RenderPass:Swapchain");
                    renderPassLabelActive = true;

                    if (useDynamicRendering)
                    {
                        // On the first frame for a given swapchain image, it starts in UNDEFINED
                        // (never been presented). Use Undefined as old layout to avoid validation errors.
                        // If we already rendered to the swapchain this frame (re-entry after compute
                        // dispatch etc.), the image was transitioned to PresentSrcKhr by EndActiveRenderPass.
                        bool imageEverPresented = _swapchainImageEverPresented is not null &&
                            imageIndex < _swapchainImageEverPresented.Length &&
                            _swapchainImageEverPresented[imageIndex];

                        // Re-entry: the image is in PresentSrcKhr from the previous EndActiveRenderPass barrier.
                        // First entry: use PresentSrcKhr if the image has been presented before, else Undefined.
                        ImageLayout colorOldLayout = swapchainClearedThisFrame
                            ? ImageLayout.PresentSrcKhr
                            : (swapchainWrittenOutsideRenderPass
                                ? ImageLayout.ColorAttachmentOptimal
                                : (imageEverPresented ? ImageLayout.PresentSrcKhr : ImageLayout.Undefined));

                        // Preserve swapchain contents on re-entry so composited scene is not wiped.
                        AttachmentLoadOp colorLoadOp = (swapchainClearedThisFrame || swapchainWrittenOutsideRenderPass)
                            ? AttachmentLoadOp.Load
                            : AttachmentLoadOp.Clear;

                        // Depth can always re-clear on re-entry; only the color contents
                        // (the composited scene) need to survive across render pass restarts.
                        AttachmentLoadOp depthLoadOp = AttachmentLoadOp.Clear;

                        ImageMemoryBarrier colorBarrier = new()
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = 0,
                            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                            OldLayout = colorOldLayout,
                            NewLayout = ImageLayout.ColorAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = swapChainImages![imageIndex],
                            SubresourceRange = new ImageSubresourceRange
                            {
                                AspectMask = ImageAspectFlags.ColorBit,
                                BaseMipLevel = 0,
                                LevelCount = 1,
                                BaseArrayLayer = 0,
                                LayerCount = 1
                            }
                        };

                        ImageMemoryBarrier depthBarrier = new()
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = 0,
                            DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                            OldLayout = ImageLayout.Undefined,
                            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = _swapchainDepthImage,
                            SubresourceRange = new ImageSubresourceRange
                            {
                                AspectMask = _swapchainDepthAspect,
                                BaseMipLevel = 0,
                                LevelCount = 1,
                                BaseArrayLayer = 0,
                                LayerCount = 1
                            }
                        };

                        ImageMemoryBarrier* preRenderingBarriers = stackalloc ImageMemoryBarrier[2];
                        preRenderingBarriers[0] = colorBarrier;
                        preRenderingBarriers[1] = depthBarrier;

                        Api!.CmdPipelineBarrier(
                            commandBuffer,
                            PipelineStageFlags.TopOfPipeBit,
                            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                            0,
                            0,
                            null,
                            0,
                            null,
                            2,
                            preRenderingBarriers);

                        ClearValue* dynamicClearValues = stackalloc ClearValue[2];
                        _state.WriteClearValues(dynamicClearValues, 2);

                        RenderingAttachmentInfo colorAttachment = new()
                        {
                            SType = StructureType.RenderingAttachmentInfo,
                            ImageView = swapChainImageViews![imageIndex],
                            ImageLayout = ImageLayout.ColorAttachmentOptimal,
                            LoadOp = colorLoadOp,
                            StoreOp = AttachmentStoreOp.Store,
                            ClearValue = dynamicClearValues[0],
                        };

                        RenderingAttachmentInfo depthAttachment = new()
                        {
                            SType = StructureType.RenderingAttachmentInfo,
                            ImageView = _swapchainDepthView,
                            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                            LoadOp = depthLoadOp,
                            StoreOp = AttachmentStoreOp.DontCare,
                            ClearValue = dynamicClearValues[1],
                        };

                        RenderingInfo renderingInfo = new()
                        {
                            SType = StructureType.RenderingInfo,
                            RenderArea = new Rect2D
                            {
                                Offset = new Offset2D(0, 0),
                                Extent = swapChainExtent
                            },
                            LayerCount = 1,
                            ColorAttachmentCount = 1,
                            PColorAttachments = &colorAttachment,
                            PDepthAttachment = &depthAttachment,
                        };

                        Api!.CmdBeginRendering(commandBuffer, &renderingInfo);

                        renderPassActive = true;
                        activeDynamicRendering = true;
                        activeTarget = null;
                        activeRenderPass = default;
                        activeFramebuffer = default;
                        activeRenderArea = renderingInfo.RenderArea;
                        swapchainClearedThisFrame = true;
                        return;
                    }

                    // Fallback: traditional render pass path.
                    // Use _renderPassLoad (LoadOp.Load) on re-entry to preserve contents.
                    RenderPass selectedRenderPass = (swapchainClearedThisFrame || swapchainWrittenOutsideRenderPass)
                        ? _renderPassLoad
                        : _renderPass;

                    RenderPassBeginInfo renderPassInfo = new()
                    {
                        SType = StructureType.RenderPassBeginInfo,
                        RenderPass = selectedRenderPass,
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
                    activeDynamicRendering = false;
                    activeTarget = null;
                    activeRenderPass = selectedRenderPass;
                    activeFramebuffer = swapChainFramebuffers![imageIndex];
                    activeRenderArea = renderPassInfo.RenderArea;
                    swapchainClearedThisFrame = true;
                    return;
                }

                var vkFrameBuffer = GenericToAPI<VkFrameBuffer>(target) ?? throw new InvalidOperationException("Failed to resolve Vulkan framebuffer for target.");
                vkFrameBuffer.Generate();

                string fboName = string.IsNullOrWhiteSpace(target.Name)
                    ? $"FBO[{target.GetHashCode()}]"
                    : target.Name!;
                CmdBeginLabel(commandBuffer, $"RenderPass:{fboName}");
                renderPassLabelActive = true;

                // Look up tracked layouts from a previous render pass on this FBO
                // within the current frame.  If present, the render pass will use
                // those as initialLayout (preserving content from earlier passes)
                // instead of Undefined (which discards content).
                fboLayoutTracking.TryGetValue(target, out ImageLayout[]? trackedLayouts);
                RenderPass passRenderPass = vkFrameBuffer.ResolveRenderPassForPass(passIndex, context.PassMetadata, trackedLayouts);
                RenderPassBeginInfo fboPassInfo = new()
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = passRenderPass,
                    Framebuffer = vkFrameBuffer.FrameBuffer,
                    RenderArea = new Rect2D
                    {
                        Offset = new Offset2D(0, 0),
                        // Use the actual VkFramebuffer dimensions (may be smaller when
                        // targeting a mip level > 0, e.g. bloom downsample FBOs).
                        Extent = new Extent2D(
                            vkFrameBuffer.FramebufferWidth > 0 ? vkFrameBuffer.FramebufferWidth : Math.Max(target.Width, 1u),
                            vkFrameBuffer.FramebufferHeight > 0 ? vkFrameBuffer.FramebufferHeight : Math.Max(target.Height, 1u))
                    }
                };

                uint attachmentCountFbo = Math.Max(vkFrameBuffer.AttachmentCount, 1u);
                ClearValue* clearValuesFbo = stackalloc ClearValue[(int)attachmentCountFbo];
                vkFrameBuffer.WriteClearValues(clearValuesFbo, attachmentCountFbo);
                fboPassInfo.ClearValueCount = attachmentCountFbo;
                fboPassInfo.PClearValues = clearValuesFbo;

                Api!.CmdBeginRenderPass(commandBuffer, &fboPassInfo, SubpassContents.Inline);

                renderPassActive = true;
                activeDynamicRendering = false;
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

                // Ensure first-use physical-group images are transitioned out of UNDEFINED
                // before any planned pass consumes them.
                EmitInitialImageBarriersForUnknownPass(commandBuffer);

                // Emit per-pass memory barriers registered during the frame.
                EMemoryBarrierMask perPassMask = _state.DrainMemoryBarrierForPass(passIndex);
                if (perPassMask != EMemoryBarrierMask.None)
                    EmitMemoryBarrierMask(commandBuffer, perPassMask);

                var imageBarriers = _barrierPlanner.GetBarriersForPass(passIndex);
                var bufferBarriers = _barrierPlanner.GetBufferBarriersForPass(passIndex);

                // If the barrier planner doesn't recognise this pass at all, it has no planned
                // layout transitions. Emit a conservative full-pipeline memory barrier so that
                // all prior writes are visible to subsequent reads. We intentionally do NOT
                // substitute image barriers from another pass because those barriers carry
                // OldLayout values that may not match the images' actual layouts, causing
                // undefined behaviour (observed as CmdBlitImage segfaults on NVIDIA drivers).
                // Ops that need specific image layout transitions (e.g. blits) handle them
                // internally via TransitionForBlit.
                if (!_barrierPlanner.HasKnownPass(passIndex))
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.UnknownPassBarrier.{passIndex}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Pass {0} is unknown to the barrier planner. Emitting conservative memory + image barriers.",
                        passIndex);

                    // Emit image layout transitions for any physical-group images that
                    // are still in UNDEFINED.  Without this, the first draw that
                    // references these images triggers a validation error because the
                    // barrier planner never planned a transition for them.
                    EmitInitialImageBarriersForUnknownPass(commandBuffer);

                    MemoryBarrier safetyBarrier = new()
                    {
                        SType = StructureType.MemoryBarrier,
                        SrcAccessMask = AccessFlags.MemoryWriteBit,
                        DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                    };

                    Api!.CmdPipelineBarrier(
                        commandBuffer,
                        PipelineStageFlags.AllCommandsBit,
                        PipelineStageFlags.AllCommandsBit,
                        DependencyFlags.None,
                        1,
                        &safetyBarrier,
                        0,
                        null,
                        0,
                        null);

                    return;
                }

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

            try
            {
                for (int opIndex = 0; opIndex < ops.Length; opIndex++)
                {
                    var op = ops[opIndex];
                    try
                    {
                        if (!hasActiveContext || !Equals(activeContext, op.Context))
                        {
                            // When the context changes but both the active render pass and the
                            // incoming op target the swapchain (target == null), keep the render
                            // pass alive.  Ending and re-beginning the swapchain render pass
                            // causes a storeOp → layout transition → loadOp cycle that can lose
                            // composited content (e.g. the skybox turns black).
                            bool canPreserveSwapchainPass = renderPassActive &&
                                activeTarget is null &&
                                VulkanRenderGraphCompiler.OpTargetsSwapchain(op);

                            if (!canPreserveSwapchainPass)
                            {
                                EndActiveRenderPass();
                            }

                            if (passIndexLabelActive)
                            {
                                CmdEndLabel(commandBuffer);
                                passIndexLabelActive = false;
                            }

                            activeContext = op.Context;
                            hasActiveContext = true;
                            ApplyPipelineOverride(activeContext);

                            // Only rebuild the resource planner when the new context has a
                            // valid pipeline.  Null-pipeline contexts (e.g. ops emitted
                            // outside the render pipeline scope) cannot provide valid resource
                            // metadata and rebuilding would destroy physical images that may
                            // still be in use by a previous frame's in-flight command buffer.
                            if (activeContext.PipelineInstance is not null &&
                                (!hasPlannerContext || RequiresResourcePlannerRebuild(plannerContext, activeContext)))
                            {
                                UpdateResourcePlannerFromContext(activeContext);
                                plannerContext = activeContext;
                                hasPlannerContext = true;
                            }

                            activePassIndex = int.MinValue;
                            activeSchedulingIdentity = int.MinValue;
                        }

                        int opPassIndex = op.PassIndex == int.MinValue && activePassIndex != int.MinValue
                            ? activePassIndex
                            : EnsureValidPassIndex(op.PassIndex, op.GetType().Name, op.Context.PassMetadata);

                        if (opPassIndex == int.MinValue)
                        {
                            Debug.VulkanWarningEvery(
                                $"Vulkan.OpDroppedNoPass.{op.GetType().Name}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Dropping op '{0}' because no valid render-graph pass index could be resolved.",
                                op.GetType().Name);
                            continue;
                        }

                        if (skipUiPipelineOps && op.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                        {
                            droppedFrameOps++;
                            if (op is MeshDrawOp or IndirectDrawOp)
                                droppedDrawOps++;

                            Debug.VulkanEvery(
                                $"Vulkan.SkipUiPipeline.{GetHashCode()}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Skipping UI pipeline op {0} pass={1} pipe={2} due to XRE_SKIP_UI_PIPELINE=1.",
                                op.GetType().Name,
                                opPassIndex,
                                op.Context.PipelineIdentity);
                            continue;
                        }

                        // Diagnostic: log the first few ops with invalid pass index per frame
                        if (op.PassIndex == int.MinValue)
                        {
                            Debug.VulkanWarningEvery(
                                $"Vulkan.OpInvalidPass.{op.GetType().Name}",
                                TimeSpan.FromSeconds(2),
                                "[Vulkan] Op[{0}] {1} had PassIndex=MinValue (resolved to {2}). " +
                                "CtxPipeline={3} CtxMetadataCount={4} CtxViewport={5}",
                                opIndex,
                                op.GetType().Name,
                                opPassIndex,
                                op.Context.PipelineIdentity,
                                op.Context.PassMetadata?.Count ?? -1,
                                op.Context.ViewportIdentity);
                        }

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
                        if (secondaryBucketByStart is not null &&
                            secondaryBucketByStart.TryGetValue(opIndex, out VulkanRenderGraphCompiler.SecondaryRecordingBucket blitBucket) &&
                            TryRecordSecondaryBucket(primaryCommandBuffer: commandBuffer, imageIndex, ops, opIndex, blitBucket, "BlitBatch"))
                        {
                            if (blit.OutFbo is null && (blit.ColorBit || blit.DepthBit || blit.StencilBit))
                                swapchainWrittenOutsideRenderPass = true;
                            opIndex = opIndex + blitBucket.Count - 1;
                        }
                        else
                        {
                            CmdBeginLabel(commandBuffer, "Blit");
                            RecordBlitOp(commandBuffer, imageIndex, blit);
                            CmdEndLabel(commandBuffer);
                            if (blit.OutFbo is null && (blit.ColorBit || blit.DepthBit || blit.StencilBit))
                                swapchainWrittenOutsideRenderPass = true;
                        }
                        break;

                    case ClearOp clear:
                        if (!renderPassActive || activeTarget != clear.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(clear.Target, opPassIndex, activeContext);
                        }

                        // Skip explicit color clears on the swapchain after the first render pass.
                        // CmdClearAttachments would erase scene content composited by an earlier pipeline.
                        // Depth/stencil clears are still allowed since they don't affect composited color.
                        if (clear.Target is null && swapchainClearedThisFrame && clear.ClearColor)
                        {
                            if (clear.ClearDepth || clear.ClearStencil)
                            {
                                // Emit depth/stencil clear only — strip the color clear.
                                RecordClearOp(commandBuffer, imageIndex, clear with { ClearColor = false });
                            }
                            // else: pure color clear on swapchain after first pass → skip entirely
                        }
                        else
                        {
                            RecordClearOp(commandBuffer, imageIndex, clear);
                        }
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

                        drawOp.Draw.Renderer.RecordDraw(
                            commandBuffer,
                            drawOp.Draw,
                            activeRenderPass,
                            activeDynamicRendering && drawOp.Target is null,
                            swapChainImageFormat,
                            _swapchainDepthFormat);
                        break;

                    case IndirectDrawOp indirectOp:
                        EndActiveRenderPass();
                        if (secondaryBucketByStart is not null &&
                            secondaryBucketByStart.TryGetValue(opIndex, out VulkanRenderGraphCompiler.SecondaryRecordingBucket indirectBucket) &&
                            TryRecordSecondaryBucket(primaryCommandBuffer: commandBuffer, imageIndex, ops, opIndex, indirectBucket, "IndirectDrawBatch"))
                        {
                            bool usedParallel = _enableParallelSecondaryCommandBufferRecording &&
                                indirectBucket.Count >= Math.Max(_parallelSecondaryIndirectRunThreshold, 2);

                            Engine.Rendering.Stats.RecordVulkanIndirectRecordingMode(
                                usedSecondary: true,
                                usedParallel,
                                opCount: indirectBucket.Count);
                            opIndex = opIndex + indirectBucket.Count - 1;
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
                        if (secondaryBucketByStart is not null &&
                            secondaryBucketByStart.TryGetValue(opIndex, out VulkanRenderGraphCompiler.SecondaryRecordingBucket computeBucket) &&
                            TryRecordSecondaryBucket(primaryCommandBuffer: commandBuffer, imageIndex, ops, opIndex, computeBucket, "ComputeDispatch"))
                        {
                            opIndex = opIndex + computeBucket.Count - 1;
                        }
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
                        droppedFrameOps++;
                        if (op is MeshDrawOp or IndirectDrawOp)
                            droppedDrawOps++;

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

                        throw new InvalidOperationException(
                            $"[Vulkan] Frame op recording failed for {op.GetType().Name} (pass={op.PassIndex}).",
                            opEx);
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
                    Debug.VulkanWarningEvery(
                        $"Vulkan.NoSwapchainWrites.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] No swapchain write commands were recorded this frame (clears={0}, draws={1}, blits={2}, computes={3}). Presenting without debug triangle fallback.",
                        clearCount,
                        drawCount,
                        blitCount,
                        computeCount);
                }

                bool forceMagentaSwapchain = string.Equals(
                    Environment.GetEnvironmentVariable("XRE_FORCE_SWAPCHAIN_MAGENTA"),
                    "1",
                    StringComparison.Ordinal);
                if (forceMagentaSwapchain)
                {
                    ClearAttachment magentaAttachment = new()
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        ColorAttachment = 0,
                        ClearValue = new ClearValue
                        {
                            Color = new ClearColorValue(1f, 0f, 1f, 1f)
                        }
                    };

                    ClearRect clearRect = new()
                    {
                        Rect = new Rect2D
                        {
                            Offset = new Offset2D(0, 0),
                            Extent = swapChainExtent
                        },
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    };

                    Api!.CmdClearAttachments(commandBuffer, 1, &magentaAttachment, 1, &clearRect);
                    swapchainWriteCount++;
                    swapchainClearWrites++;
                    MarkSwapchainWriter("ForceMagenta", activePassIndex, ops.Length, hasActiveContext ? activeContext.PipelineIdentity : 0);

                    Debug.VulkanEvery(
                        $"Vulkan.ForceSwapchainMagenta.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Forced magenta swapchain clear due to XRE_FORCE_SWAPCHAIN_MAGENTA=1.");
                }

                bool skipImGui = string.Equals(
                    Environment.GetEnvironmentVariable("XRE_SKIP_IMGUI"),
                    "1",
                    StringComparison.Ordinal);

                if (SupportsImGui && !skipImGui)
                {
                    CmdBeginLabel(commandBuffer, "ImGui");
                    RenderImGui(commandBuffer, imageIndex);
                    CmdEndLabel(commandBuffer);
                    MarkSwapchainWriter("ImGui", activePassIndex, ops.Length, hasActiveContext ? activeContext.PipelineIdentity : 0);
                    LogSwapchainWritersByPipeline("PostOverlay");
                }
                else if (SupportsImGui && skipImGui)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.SkipImGui.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Skipping ImGui overlay due to XRE_SKIP_IMGUI=1.");
                }

                string pipelineLabel = hasActiveContext
                    ? (!string.IsNullOrWhiteSpace(activeContext.PipelineInstance?.Pipeline?.GetType().Name)
                        ? $"{activeContext.PipelineInstance!.Pipeline!.GetType().Name}#{activeContext.PipelineIdentity}"
                        : $"Pipeline#{activeContext.PipelineIdentity}")
                    : "None";
                ColorF4 clearColor = GetClearColorValue();
                string swapchainWriterSummary =
                    $"{swapchainLastWriter}@p{swapchainLastWriterPass}:w{swapchainWriteCount}(C{swapchainClearWrites}D{swapchainDrawWrites}B{swapchainBlitWrites}) ops={ops.Length} fboD={drawCount - swapchainDrawWrites} fboB={blitCount - swapchainBlitWrites} comp={computeCount}";
                UpdateVulkanOnScreenDiagnostic(pipelineLabel, clearColor, droppedDrawOps, droppedFrameOps, swapchainWriterSummary);

                EndActiveRenderPass();

                EndFrameTimingQueries(commandBuffer, currentFrame);

                CmdEndLabel(commandBuffer);

                if (Api!.EndCommandBuffer(commandBuffer) != Result.Success)
                    throw new Exception("Failed to record command buffer.");
            }
            finally
            {
                activePipelineOverrideScope?.Dispose();
                activePipelineOverrideScope = null;
            }
        }

        private bool TryRecordSecondaryBucket(
            CommandBuffer primaryCommandBuffer,
            uint imageIndex,
            FrameOp[] ops,
            int startIndex,
            VulkanRenderGraphCompiler.SecondaryRecordingBucket bucket,
            string label)
        {
            if (!_enableSecondaryCommandBuffers || bucket.Count <= 0)
                return false;

            bool useParallelSecondary =
                _enableParallelSecondaryCommandBufferRecording &&
                bucket.Count >= Math.Max(_parallelSecondaryIndirectRunThreshold, 2);

            if (bucket.Count > 1 && useParallelSecondary)
            {
                ExecuteSecondaryCommandBufferBatchParallel(
                    primaryCommandBuffer,
                    $"{label}Batch",
                    bucket.Count,
                    imageIndex,
                    (relativeIndex, secondary) =>
                    {
                        FrameOp runOp = ops[startIndex + relativeIndex];
                        RecordFrameOpInSecondary(secondary, imageIndex, runOp);
                    });
                return true;
            }

            for (int relativeIndex = 0; relativeIndex < bucket.Count; relativeIndex++)
            {
                FrameOp runOp = ops[startIndex + relativeIndex];
                ExecuteSecondaryCommandBuffer(
                    primaryCommandBuffer,
                    label,
                    imageIndex,
                    secondary => RecordFrameOpInSecondary(secondary, imageIndex, runOp));
            }

            return true;
        }

        private void RecordFrameOpInSecondary(CommandBuffer secondaryCommandBuffer, uint imageIndex, FrameOp runOp)
        {
            using IDisposable? _ = Engine.Rendering.State.PushRenderingPipelineOverride(runOp.Context.PipelineInstance);
            switch (runOp)
            {
                case BlitOp blitOp:
                    RecordBlitOp(secondaryCommandBuffer, imageIndex, blitOp);
                    break;
                case IndirectDrawOp indirectDrawOp:
                    RecordIndirectDrawOp(secondaryCommandBuffer, indirectDrawOp);
                    break;
                case ComputeDispatchOp computeDispatchOp:
                    RecordComputeDispatchOp(secondaryCommandBuffer, imageIndex, computeDispatchOp);
                    break;
            }
        }

        private void RecordClearOp(CommandBuffer commandBuffer, uint imageIndex, ClearOp op)
        {
            _ = imageIndex;

            Rect2D clearArea = ClampRectToExtent(
                op.Rect,
                op.Target is null
                    ? swapChainExtent
                    : new Extent2D(Math.Max(op.Target.Width, 1u), Math.Max(op.Target.Height, 1u)));

            // Vulkan validation requires non-zero extent for vkCmdClearAttachments.
            if (clearArea.Extent.Width == 0 || clearArea.Extent.Height == 0)
                return;

            ClearRect clearRect = new()
            {
                Rect = clearArea,
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

        private static Rect2D ClampRectToExtent(Rect2D rect, Extent2D extent)
        {
            int extentWidth = (int)Math.Max(extent.Width, 1u);
            int extentHeight = (int)Math.Max(extent.Height, 1u);

            int x = Math.Clamp(rect.Offset.X, 0, extentWidth);
            int y = Math.Clamp(rect.Offset.Y, 0, extentHeight);

            int maxWidth = Math.Max(extentWidth - x, 0);
            int maxHeight = Math.Max(extentHeight - y, 0);

            int width = Math.Clamp((int)rect.Extent.Width, 0, maxWidth);
            int height = Math.Clamp((int)rect.Extent.Height, 0, maxHeight);

            return new Rect2D
            {
                Offset = new Offset2D(x, y),
                Extent = new Extent2D((uint)width, (uint)height)
            };
        }

        private void RecordBlitOp(CommandBuffer commandBuffer, uint imageIndex, BlitOp op)
        {
            void ExecuteSingleBlit(in BlitImageInfo source, in BlitImageInfo destination, Filter filter)
            {
                if (!TryResolveLiveBlitImage(source, out BlitImageInfo resolvedSource) ||
                    !TryResolveLiveBlitImage(destination, out BlitImageInfo resolvedDestination))
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Blit.UnresolvedLiveHandle",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Blit skipped: source/destination image could not be resolved to a live handle.");
                    return;
                }

                // Validate image handles before issuing Vulkan commands.
                // A stale/destroyed handle causes a native access violation (0xC0000005) in the driver.
                if (resolvedSource.Image.Handle == 0 || resolvedDestination.Image.Handle == 0)
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Blit.NullHandle",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Blit skipped: null image handle. Src=0x{0:X} Dst=0x{1:X} SrcFmt={2} DstFmt={3}",
                        resolvedSource.Image.Handle,
                        resolvedDestination.Image.Handle,
                        resolvedSource.Format,
                        resolvedDestination.Format);
                    return;
                }

                // Validate blit region dimensions — zero-sized regions can crash some drivers.
                if (op.InW == 0 || op.InH == 0 || op.OutW == 0 || op.OutH == 0)
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Blit.ZeroRegion",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Blit skipped: zero-sized region. In={0}x{1} Out={2}x{3}",
                        op.InW, op.InH, op.OutW, op.OutH);
                    return;
                }

                ImageBlit region = BuildImageBlit(resolvedSource, resolvedDestination, op.InX, op.InY, op.InW, op.InH, op.OutX, op.OutY, op.OutW, op.OutH);

                // Derive post-blit target layouts.  PreferredLayout may be Undefined
                // for newly-created dedicated images whose tracked layout hasn't been
                // set yet.  In that case, fall back to the attachment-optimal layout
                // based on the image's aspect mask.
                static ImageLayout DerivePostBlitLayout(in BlitImageInfo info)
                {
                    if (info.PreferredLayout != ImageLayout.Undefined)
                        return info.PreferredLayout;
                    return IsDepthOrStencilAspect(info.AspectMask)
                        ? ImageLayout.DepthStencilAttachmentOptimal
                        : ImageLayout.ColorAttachmentOptimal;
                }

                ImageLayout srcPostLayout = DerivePostBlitLayout(resolvedSource);
                ImageLayout dstPostLayout = DerivePostBlitLayout(resolvedDestination);

                // Pre-blit: transition from ACTUAL current layout (PreferredLayout)
                // to Transfer-optimal.  For newly-created images this is Undefined,
                // which is a valid OldLayout (content is discarded, which is fine for
                // the destination; for the source, reading from Undefined gives
                // undefined content but won't crash or cause validation errors).
                TransitionForBlit(
                    commandBuffer,
                    resolvedSource,
                    resolvedSource.PreferredLayout,
                    ImageLayout.TransferSrcOptimal,
                    resolvedSource.AccessMask,
                    AccessFlags.TransferReadBit,
                    resolvedSource.StageMask,
                    PipelineStageFlags.TransferBit);

                TransitionForBlit(
                    commandBuffer,
                    resolvedDestination,
                    resolvedDestination.PreferredLayout,
                    ImageLayout.TransferDstOptimal,
                    resolvedDestination.AccessMask,
                    AccessFlags.TransferWriteBit,
                    resolvedDestination.StageMask,
                    PipelineStageFlags.TransferBit);

                Debug.VulkanEvery(
                    "Vulkan.Blit.Record",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] CmdBlitImage: src=0x{0:X}({1}) dst=0x{2:X}({3}) region={4},{5}+{6}x{7}→{8},{9}+{10}x{11} filter={12}",
                    resolvedSource.Image.Handle, resolvedSource.Format,
                    resolvedDestination.Image.Handle, resolvedDestination.Format,
                    op.InX, op.InY, op.InW, op.InH,
                    op.OutX, op.OutY, op.OutW, op.OutH,
                    filter);

                Api!.CmdBlitImage(
                    commandBuffer,
                    resolvedSource.Image,
                    ImageLayout.TransferSrcOptimal,
                    resolvedDestination.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &region,
                    filter);

                // Post-blit: transition back to the attachment-optimal layout.
                TransitionForBlit(
                    commandBuffer,
                    resolvedSource,
                    ImageLayout.TransferSrcOptimal,
                    srcPostLayout,
                    AccessFlags.TransferReadBit,
                    resolvedSource.AccessMask,
                    PipelineStageFlags.TransferBit,
                    resolvedSource.StageMask);

                TransitionForBlit(
                    commandBuffer,
                    resolvedDestination,
                    ImageLayout.TransferDstOptimal,
                    dstPostLayout,
                    AccessFlags.TransferWriteBit,
                    resolvedDestination.AccessMask,
                    PipelineStageFlags.TransferBit,
                    resolvedDestination.StageMask);
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

            if (!op.Program.TryBuildAndBindComputeDescriptorSets(commandBuffer, imageIndex, op.Snapshot, out _, out var tempBuffers))
            {
                foreach ((Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) in tempBuffers)
                {
                    if (buffer.Handle != 0)
                        Api!.DestroyBuffer(device, buffer, null);
                    if (memory.Handle != 0)
                        Api!.FreeMemory(device, memory, null);
                }

                // Descriptor binding failed (e.g. a required storage image lacks STORAGE_BIT).
                // Dispatching without valid descriptors causes GPU faults → device lost.
                Debug.VulkanWarningEvery(
                    $"Vulkan.ComputeDispatch.NoDescriptors.{op.Program.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Skipping compute dispatch for '{0}' — descriptor binding failed.",
                    op.Program.Data.Name ?? "UnnamedProgram");
                return;
            }

            RegisterComputeTransientUniformBuffers(imageIndex, tempBuffers);
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

        /// <summary>
        /// After ending a render pass for an FBO target, update the tracked layout
        /// on each physical image group backing the FBO's attachments. The render
        /// pass will have transitioned each attachment to its <c>finalLayout</c>
        /// (color → ColorAttachmentOptimal, depth → DepthStencilAttachmentOptimal).
        /// </summary>
        private void UpdatePhysicalGroupLayoutsForFbo(XRFrameBuffer fbo)
        {
            var targets = fbo.Targets;
            if (targets is null)
                return;

            foreach (var (target, attachment, _, _) in targets)
            {
                if (target is not XRRenderBuffer rb)
                    continue;

                if (GetOrCreateAPIRenderObject(rb, true) is not VkRenderBuffer vkRb)
                    continue;

                if (vkRb.PhysicalGroup is not { } group)
                    continue;

                // The render pass finalLayout matches the BuildAttachmentSignature logic:
                // color → ColorAttachmentOptimal, depth/stencil → DepthStencilAttachmentOptimal.
                // However, some resources are sampled/storage-oriented and can appear in FBO targets
                // without having attachment usage bits; in that case, keep tracking to a legal layout.
                bool isColor = attachment >= EFrameBufferAttachment.ColorAttachment0 &&
                               attachment <= EFrameBufferAttachment.ColorAttachment31;
                bool isDepthAttachment = attachment is EFrameBufferAttachment.DepthAttachment
                    or EFrameBufferAttachment.DepthStencilAttachment
                    or EFrameBufferAttachment.StencilAttachment;

                ImageLayout fallbackLayout = ResolveInitialPhysicalGroupLayout(
                    group.Usage,
                    VulkanResourceAllocator.IsDepthStencilFormat(group.Format));

                if (isColor)
                {
                    group.LastKnownLayout = (group.Usage & ImageUsageFlags.ColorAttachmentBit) != 0
                        ? ImageLayout.ColorAttachmentOptimal
                        : fallbackLayout;
                }
                else if (isDepthAttachment)
                {
                    group.LastKnownLayout = (group.Usage & ImageUsageFlags.DepthStencilAttachmentBit) != 0
                        ? ImageLayout.DepthStencilAttachmentOptimal
                        : fallbackLayout;
                }
            }
        }

        /// <summary>
        /// When the barrier planner has no known passes, emit image memory barriers to
        /// transition any physical-group images still in <see cref="ImageLayout.Undefined"/>
        /// to a usable layout inside the current command buffer.  This is the in-CB
        /// counterpart of <see cref="TransitionNewPhysicalImagesToInitialLayout"/> (which
        /// runs one-shot commands outside the frame).  Both paths are necessary:
        /// the one-shot path handles newly-allocated images before recording starts,
        /// and this path covers images that became UNDEFINED due to mid-frame recreation
        /// or races with resource planner rebuilds.
        /// </summary>
        private void EmitInitialImageBarriersForUnknownPass(CommandBuffer commandBuffer)
        {
            foreach (VulkanPhysicalImageGroup group in _resourceAllocator.EnumeratePhysicalGroups())
            {
                if (!group.IsAllocated || group.LastKnownLayout != ImageLayout.Undefined)
                    continue;

                bool isDepth = VulkanResourceAllocator.IsDepthStencilFormat(group.Format);
                ImageLayout targetLayout = ResolveInitialPhysicalGroupLayout(group.Usage, isDepth);
                ImageAspectFlags aspect = isDepth
                    ? ImageAspectFlags.DepthBit | (HasStencilComponent(group.Format) ? ImageAspectFlags.StencilBit : 0)
                    : ImageAspectFlags.ColorBit;

                ImageMemoryBarrier barrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = targetLayout,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = group.Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = aspect,
                        BaseMipLevel = 0,
                        LevelCount = Vk.RemainingMipLevels,
                        BaseArrayLayer = 0,
                        LayerCount = Math.Max(group.Template.Layers, 1u),
                    },
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                };

                Api!.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.AllCommandsBit,
                    DependencyFlags.None,
                    0, null, 0, null,
                    1, &barrier);

                group.LastKnownLayout = targetLayout;
            }
        }

        private void EmitPlannedImageBarriers(CommandBuffer commandBuffer, IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier>? plannedBarriers)
        {
            if (plannedBarriers is null || plannedBarriers.Count == 0)
                return;

            foreach (var planned in plannedBarriers)
            {
                planned.Group.EnsureAllocated(this);

                // The barrier planner pre-computes OldLayout from the logical resource
                // dependency graph. Only substitute the group's tracked layout when the
                // planner has no concrete prior layout (UNDEFINED); otherwise keep the
                // planned value so we don't accidentally suppress required transitions.
                ImageLayout effectiveOldLayout = planned.Previous.Layout;
                ImageLayout groupLayout = planned.Group.LastKnownLayout;
                if (effectiveOldLayout == ImageLayout.Undefined && groupLayout != ImageLayout.Undefined)
                    effectiveOldLayout = groupLayout;

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
                    SrcAccessMask = FilterAccessFlagsForStages(planned.Previous.AccessMask, planned.Previous.StageMask),
                    DstAccessMask = FilterAccessFlagsForStages(planned.Next.AccessMask, planned.Next.StageMask),
                    OldLayout = effectiveOldLayout,
                    NewLayout = planned.Next.Layout,
                    SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                    Image = planned.Group.Image,
                    SubresourceRange = range
                };

                PipelineStageFlags srcStages = NormalizePipelineStages(planned.Previous.StageMask);
                PipelineStageFlags dstStages = NormalizePipelineStages(planned.Next.StageMask);

                Api!.CmdPipelineBarrier(
                    commandBuffer,
                    srcStages,
                    dstStages,
                    DependencyFlags.None,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);

                // Update the group's tracked layout so subsequent barriers and blit
                // operations use the correct OldLayout.
                planned.Group.LastKnownLayout = planned.Next.Layout;
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
                    SrcAccessMask = FilterAccessFlagsForStages(planned.Previous.AccessMask, planned.Previous.StageMask),
                    DstAccessMask = FilterAccessFlagsForStages(planned.Next.AccessMask, planned.Next.StageMask),
                    SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                    Buffer = buffer,
                    Offset = 0,
                    Size = size > 0 ? size : Vk.WholeSize
                };

                PipelineStageFlags srcStages = NormalizePipelineStages(planned.Previous.StageMask);
                PipelineStageFlags dstStages = NormalizePipelineStages(planned.Next.StageMask);

                Api!.CmdPipelineBarrier(
                    commandBuffer,
                    srcStages,
                    dstStages,
                    DependencyFlags.None,
                    0,
                    null,
                    1,
                    &barrier,
                    0,
                    null);
            }
        }

        private static PipelineStageFlags NormalizePipelineStages(PipelineStageFlags stageMask)
            => stageMask == 0 ? PipelineStageFlags.AllCommandsBit : stageMask;

        private static AccessFlags FilterAccessFlagsForStages(AccessFlags accessMask, PipelineStageFlags stageMask)
        {
            if (accessMask == 0)
                return 0;

            if ((stageMask & (PipelineStageFlags.AllCommandsBit | PipelineStageFlags.AllGraphicsBit)) != 0)
                return accessMask;

            AccessFlags allowed = 0;

            if ((stageMask & PipelineStageFlags.TransferBit) != 0)
                allowed |= AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit;

            if ((stageMask & PipelineStageFlags.DrawIndirectBit) != 0)
                allowed |= AccessFlags.IndirectCommandReadBit;

            if ((stageMask & PipelineStageFlags.VertexInputBit) != 0)
                allowed |= AccessFlags.VertexAttributeReadBit | AccessFlags.IndexReadBit;

            if ((stageMask & (PipelineStageFlags.VertexShaderBit |
                              PipelineStageFlags.TessellationControlShaderBit |
                              PipelineStageFlags.TessellationEvaluationShaderBit |
                              PipelineStageFlags.GeometryShaderBit |
                              PipelineStageFlags.FragmentShaderBit |
                              PipelineStageFlags.ComputeShaderBit |
                              PipelineStageFlags.TaskShaderBitNV |
                              PipelineStageFlags.MeshShaderBitNV)) != 0)
            {
                allowed |= AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.UniformReadBit;
            }

            if ((stageMask & PipelineStageFlags.ColorAttachmentOutputBit) != 0)
                allowed |= AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;

            if ((stageMask & (PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit)) != 0)
                allowed |= AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;

            if ((stageMask & PipelineStageFlags.HostBit) != 0)
                allowed |= AccessFlags.HostReadBit | AccessFlags.HostWriteBit;

            if (allowed == 0)
                return accessMask;

            return accessMask & allowed;
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
                    {
                        Api!.FreeCommandBuffers(device, pool, 1, ref secondary);
                        RemoveCommandBufferBindState(secondary);
                    }
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
                                    RemoveCommandBufferBindState(secondary);
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
                    {
                        Api!.FreeCommandBuffers(device, ownerPools[i], 1, ref secondaryBuffers[i]);
                        RemoveCommandBufferBindState(secondaryBuffers[i]);
                    }
                }

                CmdEndLabel(primaryCommandBuffer);
            }
        }

        public class CommandScope : IDisposable
        {
            private readonly VulkanRenderer _api;
            private readonly bool _useTransferQueue;

            public CommandScope(VulkanRenderer api, CommandBuffer cmd, bool useTransferQueue)
            {
                _api = api;
                CommandBuffer = cmd;
                _useTransferQueue = useTransferQueue;
            }

            public CommandBuffer CommandBuffer { get; }

            public void Dispose()
            {
                _api.CommandsStop(CommandBuffer, _useTransferQueue);
                GC.SuppressFinalize(this);
            }
        }

        private CommandScope NewCommandScope()
            => new(this, CommandsStart(useTransferQueue: false), useTransferQueue: false);

        private CommandScope NewTransferCommandScope()
            => new(this, CommandsStart(useTransferQueue: true), useTransferQueue: true);

        private CommandBuffer CommandsStart(bool useTransferQueue)
        {
            CommandPool pool = useTransferQueue
                ? GetThreadTransferCommandPool()
                : GetThreadCommandPool();

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
                _oneTimeCommandPools[commandBuffer.Handle] = new OneTimeCommandOwner(pool, useTransferQueue);

            return commandBuffer;
        }

        private void CommandsStop(CommandBuffer commandBuffer, bool useTransferQueue)
        {
            Api!.EndCommandBuffer(commandBuffer);

            // Use a per-submission fence instead of QueueWaitIdle so we wait only
            // on this specific submission and avoid stalling unrelated GPU work on
            // the same queue.  Also allows correct error handling — if the fence
            // wait fails (e.g. device lost) we skip freeing the still-pending CB.
            FenceCreateInfo fenceCreateInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = 0,
            };
            Fence submitFence;
            Result fenceResult = Api!.CreateFence(device, ref fenceCreateInfo, null, &submitFence);
            if (fenceResult != Result.Success)
            {
                Debug.VulkanWarning($"[Vulkan] Failed to create one-shot submit fence (result={fenceResult}). Falling back to QueueWaitIdle.");
                submitFence = default;
            }

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };


            bool waitSucceeded;
            lock (_oneTimeSubmitLock)
            {
                Queue submitQueue = SelectOneTimeSubmitQueue(useTransferQueue);
                Result submitResult = Api!.QueueSubmit(submitQueue, 1, ref submitInfo, submitFence);
                if (submitResult != Result.Success)
                {
                    Debug.VulkanWarning($"[Vulkan] One-shot QueueSubmit failed (result={submitResult}). Skipping command buffer free.");
                    if (submitFence.Handle != 0)
                        Api!.DestroyFence(device, submitFence, null);
                    RemoveCommandBufferBindState(commandBuffer);
                    return;
                }

                if (submitFence.Handle != 0)
                {
                    Result waitResult = Api!.WaitForFences(device, 1, &submitFence, true, ulong.MaxValue);
                    waitSucceeded = waitResult == Result.Success;
                    if (!waitSucceeded)
                        Debug.VulkanWarning($"[Vulkan] WaitForFences for one-shot submit failed (result={waitResult}). Command buffer will not be freed to avoid use-after-free.");
                }
                else
                {
                    // Fence creation failed — fall back to QueueWaitIdle.
                    Result waitResult = Api!.QueueWaitIdle(submitQueue);
                    waitSucceeded = waitResult == Result.Success;
                    if (!waitSucceeded)
                        Debug.VulkanWarning($"[Vulkan] QueueWaitIdle fallback failed (result={waitResult}). Command buffer will not be freed.");
                }
            }

            if (submitFence.Handle != 0)
                Api!.DestroyFence(device, submitFence, null);

            if (!waitSucceeded)
            {
                // Do not free the command buffer — it may still be in flight.
                RemoveCommandBufferBindState(commandBuffer);
                return;
            }

            CommandPool pool = useTransferQueue ? GetThreadTransferCommandPool() : GetThreadCommandPool();
            lock (_oneTimeCommandPoolsLock)
            {
                if (_oneTimeCommandPools.Remove(commandBuffer.Handle, out OneTimeCommandOwner owner) && owner.Pool.Handle != 0)
                {
                    pool = owner.Pool;
                    useTransferQueue = owner.UseTransferQueue;
                }
            }

            Api!.FreeCommandBuffers(device, pool, 1, ref commandBuffer);
            RemoveCommandBufferBindState(commandBuffer);
        }

        private Queue SelectOneTimeSubmitQueue(bool useTransferQueue)
        {
            if (useTransferQueue)
                return transferQueue;

            // Keep default graphics submission behavior unless OpenXR is actively running.
            if (!HasSecondaryGraphicsQueue || !Engine.VRState.IsOpenXRActive)
                return graphicsQueue;

            // Alternate between primary and secondary graphics queues.
            int submitIndex = Interlocked.Increment(ref _oneTimeGraphicsSubmitCounter);
            return (submitIndex & 1) == 0 ? secondaryGraphicsQueue : graphicsQueue;
        }

        private void AllocateCommandBufferDirtyFlags()
        {
            if (_commandBuffers is null)
            {
                _commandBufferDirtyFlags = null;
                _commandBufferFrameOpSignatures = null;
                _commandBufferPlannerRevisions = null;
                return;
            }

            _commandBufferDirtyFlags = new bool[_commandBuffers.Length];
            _commandBufferFrameOpSignatures = new ulong[_commandBuffers.Length];
            _commandBufferPlannerRevisions = new ulong[_commandBuffers.Length];
            for (int i = 0; i < _commandBufferDirtyFlags.Length; i++)
            {
                _commandBufferDirtyFlags[i] = true;
                _commandBufferFrameOpSignatures[i] = ulong.MaxValue;
                _commandBufferPlannerRevisions[i] = ulong.MaxValue;
            }
        }
    }
}
