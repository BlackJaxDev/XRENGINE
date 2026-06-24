using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        internal const uint CommonPushConstantSize = 16;
        internal const ShaderStageFlags CommonPushConstantStageFlags =
            ShaderStageFlags.VertexBit |
            ShaderStageFlags.TessellationControlBit |
            ShaderStageFlags.TessellationEvaluationBit |
            ShaderStageFlags.GeometryBit |
            ShaderStageFlags.FragmentBit |
            ShaderStageFlags.ComputeBit;
        private const int PrimaryCommandBufferVariantCapacity = 64;

        private CommandBuffer[]? _commandBuffers;
        private CommandBuffer[]? _activeCommandBuffers;
        private List<CommandBufferCacheVariant>[]? _commandBufferVariants;
        private CommandBuffer[]? _dynamicUiBatchTextSecondaryCommandBuffers;
        private CommandBuffer[]? _dynamicUiBatchTextOverlayCommandBuffers;
        private int[]? _dynamicUiBatchTextSecondaryOpCounts;
        private ulong[]? _dynamicUiBatchTextSecondarySignatures;
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
        private readonly Dictionary<ulong, int> _commandBufferImageIndices = new();
        private bool _enableSecondaryCommandBuffers = true;
        private bool _enableParallelSecondaryCommandBufferRecording = !IsParallelSecondaryCommandBufferRecordingDisabled();
        private int _parallelSecondaryIndirectRunThreshold = 4;
        private static readonly int FrameOpSignatureDiffLogLimit = ReadFrameOpSignatureDiffLogLimit();
        private static readonly bool FrameOpSignatureDiffDiagnosticsEnabled =
            string.Equals(Environment.GetEnvironmentVariable("XRE_VULKAN_FRAMEOP_SIGNATURE_DIFF"), "1", StringComparison.Ordinal);
        private static readonly bool FrameDataReuseDiagnosticsEnabled =
            string.Equals(Environment.GetEnvironmentVariable("XRE_VULKAN_FRAME_DATA_REUSE_DIAG"), "1", StringComparison.Ordinal);
        private static readonly bool CommandRecordingDiagnosticsEnabled =
            string.Equals(Environment.GetEnvironmentVariable("XRE_VULKAN_RECORDING_DIAG"), "1", StringComparison.Ordinal);
        private static readonly bool CommandRecordingDetailProfilingEnabled =
            string.Equals(Environment.GetEnvironmentVariable("XRE_VULKAN_RECORDING_PROFILE_DETAIL"), "1", StringComparison.Ordinal);
        private FrameOpSignatureDebugPart[][]? _commandBufferFrameOpSignatureDebugParts;
        private int _frameOpSignatureDiffLogCount;
        private string? _vulkanDiagnosticBaseWindowTitle;
        private string? _vulkanDiagnosticLastTitle;
        private int _vulkanLastFrameDroppedDrawOps;
        private int _vulkanLastFrameDroppedOps;
        private readonly Dictionary<int, VulkanRenderGraphCompiler.SecondaryRecordingBucket> _secondaryBucketByStartScratch = new();
        private readonly Dictionary<int, int> _swapchainWritesByPipelineScratch = new();
        private readonly Dictionary<int, string> _swapchainWriterLabelByPipelineScratch = new();
        private readonly Dictionary<int, string> _swapchainWriterDetailByPipelineScratch = new();
        private readonly Dictionary<int, FrameOp> _swapchainWriterOpByPipelineScratch = new();
        private readonly Dictionary<int, int> _swapchainWriterDynamicUiDrawCountByPipelineScratch = new();
        private readonly Dictionary<int, int> _swapchainWriterPassByPipelineScratch = new();
        private readonly Dictionary<int, int> _swapchainWriterOpIndexByPipelineScratch = new();
        private readonly Dictionary<int, string> _pipelineNameByIdentityScratch = new();
        private readonly Dictionary<VkMeshRenderer, int> _recordMeshDrawSlotsByRendererScratch = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VkMeshRenderer, int> _refreshMeshDrawSlotsByRendererScratch = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VkMeshRenderer, int> _dynamicUiMeshDrawSlotsByRendererScratch = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<XRFrameBuffer, ImageLayout[]> _fboLayoutTrackingScratch = new(ReferenceEqualityComparer.Instance);
        private readonly List<KeyValuePair<int, int>> _swapchainWriterCountSortScratch = new();
        private readonly StringBuilder _swapchainWriterSummaryBuilder = new(256);
        private bool _lastEnsureCommandBufferRecordedPrimary;
        private int _secondaryBucketByStartCapacityHint = 1;
        private int _recordSwapchainWriterCapacityHint = 1;
        private int _recordPipelineNameCapacityHint = 1;
        private int _recordMeshDrawSlotCapacityHint = 1;
        private int _recordFboLayoutCapacityHint = 1;
        private int _refreshMeshDrawSlotCapacityHint = 1;
        private int _dynamicUiMeshDrawSlotCapacityHint = 1;
        private string? _lastReusableFrameDataRefreshFailureReason;
        private static readonly bool BloomVulkanDiagnosticsEnabled =
            string.Equals(Environment.GetEnvironmentVariable("XRE_BLOOM_DIAG"), "1", StringComparison.Ordinal);

        private readonly record struct FrameOpFailureSnapshot(
            string OpType,
            int PassIndex,
            int PipelineIdentity,
            int ViewportIdentity,
            string TargetName,
            string MaterialName,
            string ShaderName,
            string Message);

        private readonly record struct FrameOpSignatureDebugPart(
            int OpIndex,
            string OpType,
            string Component,
            ulong Signature,
            string Detail);

        private sealed class CommandBufferCacheVariant
        {
            public CommandBufferCacheVariant(
                CommandBuffer primaryCommandBuffer,
                CommandBuffer dynamicUiSecondaryCommandBuffer,
                bool ownsPrimaryCommandBuffer,
                bool ownsDynamicUiSecondaryCommandBuffer)
            {
                PrimaryCommandBuffer = primaryCommandBuffer;
                DynamicUiSecondaryCommandBuffer = dynamicUiSecondaryCommandBuffer;
                OwnsPrimaryCommandBuffer = ownsPrimaryCommandBuffer;
                OwnsDynamicUiSecondaryCommandBuffer = ownsDynamicUiSecondaryCommandBuffer;
            }

            public CommandBuffer PrimaryCommandBuffer { get; }
            public CommandBuffer DynamicUiSecondaryCommandBuffer { get; }
            public bool OwnsPrimaryCommandBuffer { get; }
            public bool OwnsDynamicUiSecondaryCommandBuffer { get; }
            public bool Dirty { get; set; } = true;
            public ulong FrameOpsSignature { get; set; } = ulong.MaxValue;
            public ulong DynamicUiSignature { get; set; } = ulong.MaxValue;
            public int DynamicUiOpCount { get; set; } = -1;
            public bool DynamicUiSecondaryRecorded { get; set; }
            public bool PreserveSwapchainForOverlay { get; set; }
            public ImageLayout RecordedSwapchainFinalLayout { get; set; } = ImageLayout.PresentSrcKhr;
            public ulong CommandChainScheduleSignature { get; set; } = ulong.MaxValue;
            public ulong CommandChainPrimaryGroupSignature { get; set; } = ulong.MaxValue;
            public int CommandChainPrimaryGroupCount { get; set; } = -1;
            public ulong PlannerRevision { get; set; } = ulong.MaxValue;
            public bool GpuProfilerActive { get; set; }
            public int GpuProfilerFrameSlot { get; set; } = -1;
            public VulkanGpuProfilerPendingScope[]? GpuProfilerScopes { get; set; }
            public int GpuProfilerQueryCount { get; set; }
            public ulong LastUsedFrameId { get; set; }
            public FrameOpSignatureDebugPart[]? SignatureDebugParts { get; set; }
        }

        private sealed class ComputeTransientResources
        {
            public object SyncRoot { get; } = new();
            public DescriptorPool ActiveDescriptorPool;
            public ulong ActiveDescriptorPoolSignature;
            public List<DescriptorPool> DescriptorPools { get; } = [];
            public List<ulong> DescriptorPoolSignatures { get; } = [];
            public List<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)> UniformBuffers { get; } = [];
            public bool DescriptorPoolsInitialized;
        }

        private readonly struct DeferredSecondaryCommandBuffer(CommandPool pool, CommandBuffer commandBuffer)
        {
            public CommandPool Pool { get; } = pool;
            public CommandBuffer CommandBuffer { get; } = commandBuffer;
        }

        private readonly struct OneTimeCommandOwner(CommandPool pool, bool useTransferQueue)
        {
            public CommandPool Pool { get; } = pool;
            public bool UseTransferQueue { get; } = useTransferQueue;
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

        private void RegisterCommandBufferImageIndex(CommandBuffer commandBuffer, uint imageIndex)
        {
            if (commandBuffer.Handle == 0)
                return;

            int resolvedImageIndex = unchecked((int)Math.Min(imageIndex, int.MaxValue));
            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
                _commandBufferImageIndices[key] = resolvedImageIndex;
        }

        internal int ResolveCommandBufferImageIndex(CommandBuffer commandBuffer)
        {
            if (commandBuffer.Handle == 0)
                return -1;

            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
                return _commandBufferImageIndices.TryGetValue(key, out int imageIndex)
                    ? imageIndex
                    : -1;
        }

        internal void RemoveCommandBufferBindState(CommandBuffer commandBuffer)
        {
            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
            {
                _commandBindStates.Remove(key);
                _commandBufferImageIndices.Remove(key);
            }
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
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(pipelineBindSkips: 1);
                return;
            }

            Api!.CmdBindPipeline(commandBuffer, bindPoint, pipeline);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(pipelineBinds: 1);
        }

        internal void BindDescriptorSetsTracked(
            CommandBuffer commandBuffer,
            PipelineBindPoint bindPoint,
            PipelineLayout layout,
            uint firstSet,
            DescriptorSet[] sets)
            => BindDescriptorSetsTracked(commandBuffer, bindPoint, layout, firstSet, (ReadOnlySpan<DescriptorSet>)sets, ReadOnlySpan<uint>.Empty);

        internal void BindDescriptorSetsTracked(
            CommandBuffer commandBuffer,
            PipelineBindPoint bindPoint,
            PipelineLayout layout,
            uint firstSet,
            ReadOnlySpan<DescriptorSet> sets,
            ReadOnlySpan<uint> dynamicOffsets)
        {
            if (sets.Length == 0)
                return;

            HashCode hash = new();
            hash.Add((int)bindPoint);
            hash.Add(layout.Handle);
            hash.Add(firstSet);
            for (int i = 0; i < sets.Length; i++)
                hash.Add(sets[i].Handle);
            for (int i = 0; i < dynamicOffsets.Length; i++)
                hash.Add(dynamicOffsets[i]);

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
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(descriptorBindSkips: 1);
                return;
            }

            fixed (DescriptorSet* setPtr = sets)
            fixed (uint* offsetPtr = dynamicOffsets)
                Api!.CmdBindDescriptorSets(commandBuffer, bindPoint, layout, firstSet, (uint)sets.Length, setPtr, (uint)dynamicOffsets.Length, offsetPtr);

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(descriptorBinds: 1);
        }

        internal void PushConstantsTracked<T>(
            CommandBuffer commandBuffer,
            PipelineLayout layout,
            ShaderStageFlags stageFlags,
            uint offset,
            in T value) where T : unmanaged
        {
            if (layout.Handle == 0)
                return;

            T localValue = value;
            Api!.CmdPushConstants(
                commandBuffer,
                layout,
                stageFlags,
                offset,
                (uint)sizeof(T),
                &localValue);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(pushConstantWrites: 1);
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
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(vertexBufferBindSkips: 1);
                return;
            }

            fixed (Silk.NET.Vulkan.Buffer* bufferPtr = buffers)
            fixed (ulong* offsetPtr = offsets)
                Api!.CmdBindVertexBuffers(commandBuffer, firstBinding, (uint)buffers.Length, bufferPtr, offsetPtr);

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(vertexBufferBinds: 1);
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
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(indexBufferBindSkips: 1);
                return;
            }

            Api!.CmdBindIndexBuffer(commandBuffer, indexBuffer, offset, indexType);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(indexBufferBinds: 1);
        }

        private void DestroyCommandBuffers()
        {
            if (_commandBuffers is null)
                return;

            CancelCommandChainRecordingWorkers();
            DestroyComputeTransientResources();
            DestroyDeferredSecondaryCommandBuffers();
            DestroyCommandChainCaches();
            DestroyCommandBufferVariants();
            DestroyDynamicUiBatchTextSecondaryCommandBuffers();
            DestroyDynamicUiBatchTextOverlayCommandBuffers();
            DestroyComputeDescriptorCaches();
            DestroyImGuiOverlayCommandBuffers();

            if (_deviceLost)
            {
                foreach (CommandBuffer commandBuffer in _commandBuffers)
                    RemoveCommandBufferBindState(commandBuffer);

                _commandBuffers = null;
                _activeCommandBuffers = null;
                _dynamicUiBatchTextSecondaryCommandBuffers = null;
                _dynamicUiBatchTextOverlayCommandBuffers = null;
                _imguiOverlayCommandBuffers = null;
                _dynamicUiBatchTextSecondaryOpCounts = null;
                _dynamicUiBatchTextSecondarySignatures = null;
                _commandBufferDirtyFlags = null;
                _commandBufferFrameOpSignatures = null;
                _commandBufferFrameOpSignatureDebugParts = null;
                _commandBufferPlannerRevisions = null;
                _commandChainCaches = null;
                return;
            }

            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                if (_commandBuffers.Length > 0)
                    Api!.FreeCommandBuffers(device, commandPool, (uint)_commandBuffers.Length, commandBuffersPtr);
            }

            _commandBuffers = null;
            _activeCommandBuffers = null;
            _dynamicUiBatchTextSecondaryCommandBuffers = null;
            _dynamicUiBatchTextOverlayCommandBuffers = null;
            _imguiOverlayCommandBuffers = null;
            _dynamicUiBatchTextSecondaryOpCounts = null;
            _dynamicUiBatchTextSecondarySignatures = null;
            _commandBufferDirtyFlags = null;
            _commandBufferFrameOpSignatures = null;
            _commandBufferFrameOpSignatureDebugParts = null;
            _commandBufferPlannerRevisions = null;
            _commandChainCaches = null;
            _commandChainScheduleCache = null;
            _commandChainScheduleFastSignatures = null;
        }

        private void DestroyCommandBufferVariants()
        {
            if (_commandBufferVariants is null)
                return;

            foreach (List<CommandBufferCacheVariant>? variants in _commandBufferVariants)
            {
                if (variants is null)
                    continue;

                foreach (CommandBufferCacheVariant variant in variants)
                {
                    CommandBuffer primary = variant.PrimaryCommandBuffer;
                    if (primary.Handle != 0)
                    {
                        if (variant.OwnsPrimaryCommandBuffer && !_deviceLost)
                            Api!.FreeCommandBuffers(device, commandPool, 1, ref primary);
                        RemoveCommandBufferBindState(primary);
                    }

                    CommandBuffer secondary = variant.DynamicUiSecondaryCommandBuffer;
                    if (secondary.Handle != 0)
                    {
                        if (variant.OwnsDynamicUiSecondaryCommandBuffer && !_deviceLost)
                            Api!.FreeCommandBuffers(device, commandPool, 1, ref secondary);
                        RemoveCommandBufferBindState(secondary);
                    }
                }

                variants.Clear();
            }

            _commandBufferVariants = null;
            _activeCommandBuffers = null;
        }

        private void DestroyDeferredSecondaryCommandBuffers()
        {
            if (_deferredSecondaryCommandBuffers is null)
                return;

            for (int i = 0; i < _deferredSecondaryCommandBuffers.Length; i++)
                ReleaseDeferredSecondaryCommandBuffers((uint)i);

            _deferredSecondaryCommandBuffers = null;
        }

        private void DestroyCommandChainCaches()
        {
            if (_commandChainCaches is null)
                return;

            foreach (Dictionary<CommandChainKey, CommandChain>? cache in _commandChainCaches)
            {
                if (cache is null)
                    continue;

                foreach (CommandChain chain in cache.Values)
                {
                    CommandBuffer secondary = chain.SecondaryCommandBuffer;
                    if (secondary.Handle == 0)
                        continue;

                    if (_deviceLost || chain.SecondaryCommandPool.Handle == 0)
                    {
                        RemoveCommandBufferBindState(secondary);
                        chain.SecondaryCommandBuffer = default;
                        chain.SecondaryCommandPool = default;
                        continue;
                    }

                    CommandBuffer freedSecondary = secondary;
                    Api!.FreeCommandBuffers(device, chain.SecondaryCommandPool, 1, ref secondary);
                    RemoveCommandBufferBindState(freedSecondary);
                    chain.SecondaryCommandBuffer = default;
                    chain.SecondaryCommandPool = default;
                }

                cache.Clear();
            }

            _commandChainCaches = null;
            _commandChainScheduleCache = null;
            _commandChainScheduleFastSignatures = null;
        }

        private void DestroyDynamicUiBatchTextSecondaryCommandBuffers()
        {
            if (_dynamicUiBatchTextSecondaryCommandBuffers is null)
                return;

            if (_deviceLost)
            {
                foreach (CommandBuffer commandBuffer in _dynamicUiBatchTextSecondaryCommandBuffers)
                    RemoveCommandBufferBindState(commandBuffer);

                _dynamicUiBatchTextSecondaryCommandBuffers = null;
                _dynamicUiBatchTextSecondaryOpCounts = null;
                _dynamicUiBatchTextSecondarySignatures = null;
                return;
            }

            fixed (CommandBuffer* commandBuffersPtr = _dynamicUiBatchTextSecondaryCommandBuffers)
            {
                if (_dynamicUiBatchTextSecondaryCommandBuffers.Length > 0)
                    Api!.FreeCommandBuffers(device, commandPool, (uint)_dynamicUiBatchTextSecondaryCommandBuffers.Length, commandBuffersPtr);
            }

            foreach (CommandBuffer commandBuffer in _dynamicUiBatchTextSecondaryCommandBuffers)
                RemoveCommandBufferBindState(commandBuffer);

            _dynamicUiBatchTextSecondaryCommandBuffers = null;
            _dynamicUiBatchTextSecondaryOpCounts = null;
            _dynamicUiBatchTextSecondarySignatures = null;
        }

        private void DestroyDynamicUiBatchTextOverlayCommandBuffers()
        {
            if (_dynamicUiBatchTextOverlayCommandBuffers is null)
                return;

            if (_deviceLost)
            {
                foreach (CommandBuffer commandBuffer in _dynamicUiBatchTextOverlayCommandBuffers)
                    RemoveCommandBufferBindState(commandBuffer);

                _dynamicUiBatchTextOverlayCommandBuffers = null;
                return;
            }

            fixed (CommandBuffer* commandBuffersPtr = _dynamicUiBatchTextOverlayCommandBuffers)
            {
                if (_dynamicUiBatchTextOverlayCommandBuffers.Length > 0)
                    Api!.FreeCommandBuffers(device, commandPool, (uint)_dynamicUiBatchTextOverlayCommandBuffers.Length, commandBuffersPtr);
            }

            foreach (CommandBuffer commandBuffer in _dynamicUiBatchTextOverlayCommandBuffers)
                RemoveCommandBufferBindState(commandBuffer);

            _dynamicUiBatchTextOverlayCommandBuffers = null;
        }

        private void DestroyImGuiOverlayCommandBuffers()
        {
            if (_imguiOverlayCommandBuffers is null)
                return;

            if (_deviceLost)
            {
                foreach (CommandBuffer commandBuffer in _imguiOverlayCommandBuffers)
                    RemoveCommandBufferBindState(commandBuffer);

                _imguiOverlayCommandBuffers = null;
                return;
            }

            fixed (CommandBuffer* commandBuffersPtr = _imguiOverlayCommandBuffers)
            {
                if (_imguiOverlayCommandBuffers.Length > 0)
                    Api!.FreeCommandBuffers(device, commandPool, (uint)_imguiOverlayCommandBuffers.Length, commandBuffersPtr);
            }

            foreach (CommandBuffer commandBuffer in _imguiOverlayCommandBuffers)
                RemoveCommandBufferBindState(commandBuffer);

            _imguiOverlayCommandBuffers = null;
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

                if (_deviceLost)
                {
                    RemoveCommandBufferBindState(secondary);
                    continue;
                }

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
                RemoveCommandBufferBindState(commandBuffer);
                return;
            }

            _deferredSecondaryCommandBuffers[imageIndex] ??= [];
            _deferredSecondaryCommandBuffers[imageIndex]!.Add(new DeferredSecondaryCommandBuffer(pool, commandBuffer));
        }

        /// <summary>
        /// Descriptor pool size tiers for transient compute pool allocation.
        /// Replaces the old uniform 8Ã— scaling with demand-aware sizing.
        /// </summary>
        private enum EDescriptorPoolSizeClass : byte
        {
            /// <summary>Simple shaders with few bindings (shadow, single-texture compute). Scale=4Ã—, base=16.</summary>
            Small = 0,
            /// <summary>Standard compute/material passes (3-8 bindings). Scale=8Ã—, base=32.</summary>
            Medium = 1,
            /// <summary>Complex passes with many bindings (deferred lighting, multi-texture). Scale=16Ã—, base=64.</summary>
            Large = 2,
        }

        private static (uint maxSetsBase, uint descriptorScale) GetPoolSizeClassParameters(EDescriptorPoolSizeClass sizeClass) => sizeClass switch
        {
            EDescriptorPoolSizeClass.Small => (16u, 4u),
            EDescriptorPoolSizeClass.Medium => (32u, 8u),
            EDescriptorPoolSizeClass.Large => (64u, 16u),
            _ => (32u, 8u),
        };

        private static EDescriptorPoolSizeClass InferPoolSizeClass(DescriptorPoolSize[] poolSizes, int setLayoutCount)
        {
            uint totalDescriptors = 0;
            for (int i = 0; i < poolSizes.Length; i++)
                totalDescriptors += poolSizes[i].DescriptorCount;

            if (poolSizes.Length > 8 || totalDescriptors > 16)
                return EDescriptorPoolSizeClass.Large;

            if (poolSizes.Length <= 2 && totalDescriptors <= 4)
                return EDescriptorPoolSizeClass.Small;

            return EDescriptorPoolSizeClass.Medium;
        }

        private void DestroyComputeTransientResources()
        {
            if (_computeTransientResources is null)
                return;

            for (int i = 0; i < _computeTransientResources.Length; i++)
                CleanupComputeTransientResources((uint)i, destroyPools: true);

            _computeTransientResources = null;
        }

        internal int ReleaseComputeTransientDescriptorReferencesForPhysicalResourceDestruction()
        {
            if (_computeTransientResources is null)
                return 0;

            int poolCount = 0;
            for (int i = 0; i < _computeTransientResources.Length; i++)
            {
                ComputeTransientResources? resources = _computeTransientResources[i];
                if (resources is null)
                    continue;

                lock (resources.SyncRoot)
                    poolCount += ReleaseComputeTransientDescriptorReferencesForPhysicalResourceDestruction(resources);
            }

            return poolCount;
        }

        private int ReleaseComputeTransientDescriptorReferencesForPhysicalResourceDestruction(ComputeTransientResources resources)
        {
            int poolCount = 0;
            for (int poolIndex = 0; poolIndex < resources.DescriptorPools.Count; poolIndex++)
            {
                DescriptorPool descriptorPool = resources.DescriptorPools[poolIndex];
                if (descriptorPool.Handle == 0)
                    continue;

                RetireDescriptorPool(descriptorPool);
                resources.DescriptorPools[poolIndex] = default;
                if (poolIndex < resources.DescriptorPoolSignatures.Count)
                    resources.DescriptorPoolSignatures[poolIndex] = 0;
                poolCount++;
            }

            resources.ActiveDescriptorPool = default;
            resources.ActiveDescriptorPoolSignature = 0;
            resources.DescriptorPoolsInitialized = false;
            resources.DescriptorPools.Clear();
            resources.DescriptorPoolSignatures.Clear();

            foreach ((Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) in resources.UniformBuffers)
                DestroyBuffer(buffer, memory);

            resources.UniformBuffers.Clear();
            return poolCount;
        }

        private void CleanupComputeTransientResources(uint imageIndex, bool destroyPools = false)
        {
            if (_computeTransientResources is null || imageIndex >= _computeTransientResources.Length)
                return;

            ComputeTransientResources? resources = _computeTransientResources[imageIndex];
            if (resources is null)
                return;

            for (int i = 0; i < resources.DescriptorPools.Count; i++)
            {
                DescriptorPool descriptorPool = resources.DescriptorPools[i];
                if (descriptorPool.Handle != 0)
                {
                    if (destroyPools)
                    {
                        Api!.DestroyDescriptorPool(device, descriptorPool, null);
                        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
                        resources.DescriptorPools[i] = default;
                        if (i < resources.DescriptorPoolSignatures.Count)
                            resources.DescriptorPoolSignatures[i] = 0;
                    }
                    else
                    {
                        Result resetResult = Api!.ResetDescriptorPool(device, descriptorPool, 0);
                        if (resetResult == Result.Success)
                        {
                            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolReset();
                        }
                        else
                        {
                            Api!.DestroyDescriptorPool(device, descriptorPool, null);
                            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
                            resources.DescriptorPools[i] = default;
                            if (i < resources.DescriptorPoolSignatures.Count)
                                resources.DescriptorPoolSignatures[i] = 0;
                        }
                    }
                }
            }

            resources.ActiveDescriptorPool = default;
            resources.ActiveDescriptorPoolSignature = 0;
            resources.DescriptorPoolsInitialized = !destroyPools && resources.DescriptorPools.Count > 0;
            if (destroyPools)
            {
                resources.DescriptorPools.Clear();
                resources.DescriptorPoolSignatures.Clear();
            }

            foreach ((Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) in resources.UniformBuffers)
                DestroyBuffer(buffer, memory);

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

            ComputeTransientResources? resources = _computeTransientResources[imageIndex];
            if (resources is null)
            {
                lock (_computeDescriptorCacheLock)
                    resources = _computeTransientResources[imageIndex] ??= new ComputeTransientResources();
            }

            lock (resources.SyncRoot)
            {
                ulong poolSignature = ComputeTransientDescriptorPoolSignature(poolSizes, requireUpdateAfterBind);

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

                if (resources.ActiveDescriptorPool.Handle != 0 &&
                    resources.ActiveDescriptorPoolSignature == poolSignature &&
                    TryAllocateFromPool(resources.ActiveDescriptorPool, out descriptorSets))
                {
                    return true;
                }

                if (resources.DescriptorPoolsInitialized)
                {
                    for (int i = 0; i < resources.DescriptorPools.Count; i++)
                    {
                        DescriptorPool pooledDescriptorPool = resources.DescriptorPools[i];
                        if (pooledDescriptorPool.Handle == 0)
                            continue;

                        if (i >= resources.DescriptorPoolSignatures.Count ||
                            resources.DescriptorPoolSignatures[i] != poolSignature)
                        {
                            continue;
                        }

                        resources.ActiveDescriptorPool = pooledDescriptorPool;
                        resources.ActiveDescriptorPoolSignature = poolSignature;
                        if (TryAllocateFromPool(resources.ActiveDescriptorPool, out descriptorSets))
                            return true;
                    }
                }

                // Infer pool size class from descriptor demand to avoid uniform over-allocation.
                EDescriptorPoolSizeClass sizeClass = InferPoolSizeClass(poolSizes, setLayouts.Length);
                (uint maxSetsBase, uint descriptorScale) = GetPoolSizeClassParameters(sizeClass);

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
                        MaxSets = Math.Max((uint)setLayouts.Length * descriptorScale, maxSetsBase),
                        PoolSizeCount = (uint)scaledPoolSizes.Length,
                        PPoolSizes = poolSizesPtr,
                    };

                    if (Api!.CreateDescriptorPool(device, ref poolInfo, null, out DescriptorPool descriptorPool) != Result.Success)
                        return false;

                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();
                    resources.DescriptorPools.Add(descriptorPool);
                    resources.DescriptorPoolSignatures.Add(poolSignature);
                    resources.ActiveDescriptorPool = descriptorPool;
                    resources.ActiveDescriptorPoolSignature = poolSignature;
                    resources.DescriptorPoolsInitialized = true;
                }

                return TryAllocateFromPool(resources.ActiveDescriptorPool, out descriptorSets);
            }
        }

        private static ulong ComputeTransientDescriptorPoolSignature(DescriptorPoolSize[] poolSizes, bool requireUpdateAfterBind)
        {
            HashCode hash = new();
            hash.Add(requireUpdateAfterBind);
            hash.Add(poolSizes.Length);

            Span<bool> consumed = poolSizes.Length <= 32
                ? stackalloc bool[poolSizes.Length]
                : new bool[poolSizes.Length];

            for (int sorted = 0; sorted < poolSizes.Length; sorted++)
            {
                int next = -1;
                int nextType = int.MaxValue;
                for (int i = 0; i < poolSizes.Length; i++)
                {
                    if (consumed[i])
                        continue;

                    int candidateType = (int)poolSizes[i].Type;
                    if (candidateType >= nextType)
                        continue;

                    next = i;
                    nextType = candidateType;
                }

                if (next < 0)
                    break;

                consumed[next] = true;
                hash.Add(nextType);
                hash.Add(poolSizes[next].DescriptorCount);
            }

            return unchecked((ulong)hash.ToHashCode());
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
                    DestroyBuffer(buffer, memory);

                return;
            }

            ComputeTransientResources resources = _computeTransientResources[imageIndex] ??= new ComputeTransientResources();
            resources.UniformBuffers.AddRange(uniformBuffers);
        }

    }
}
