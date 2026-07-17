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
        private readonly object _commandBindStateLock = new();
        private readonly Dictionary<ulong, CommandBufferBindState> _commandBindStates = new();
        private readonly Dictionary<ulong, int> _commandBufferImageIndices = new();
        private long _commandBufferRecordingGeneration;
        private readonly object _ownedCommandChainSecondaryPoolsLock = new();
        private readonly Dictionary<ulong, OwnedCommandChainSecondaryPool> _ownedCommandChainSecondaryPools = new();
        private bool _enableSecondaryCommandBuffers = true;
        private bool _enableParallelSecondaryCommandBufferRecording = !IsParallelSecondaryCommandBufferRecordingDisabled();
        private int _parallelSecondaryIndirectRunThreshold = 4;
        private static readonly int FrameOpSignatureDiffLogLimit = ReadFrameOpSignatureDiffLogLimit();
        private static readonly bool FrameOpSignatureDiffDiagnosticsEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanFrameOpSignatureDiff), "1", StringComparison.Ordinal);
        private static readonly bool FrameDataReuseDiagnosticsEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanFrameDataReuseDiag), "1", StringComparison.Ordinal);
        private static readonly bool CommandRecordingDiagnosticsEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanRecordingDiag), "1", StringComparison.Ordinal);
        private static readonly bool CommandRecordingDetailProfilingEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanRecordingProfileDetail), "1", StringComparison.Ordinal);
        private static readonly bool FrameOpTraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanFrameOpTrace), "1", StringComparison.Ordinal);
        private static readonly bool TargetTraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanTargetTrace), "1", StringComparison.Ordinal);
        private static readonly bool IndirectTraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanIndirectTrace), "1", StringComparison.Ordinal);
        private static readonly bool DescriptorTraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanDescriptorTrace), "1", StringComparison.Ordinal);
        private static readonly bool ParallelRecordingValidationEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanParallelRecordingValidate), "1", StringComparison.Ordinal);
        private static readonly bool OpenXrVulkanTraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanTrace), "1", StringComparison.Ordinal);
        private static readonly bool OpenXrVulkanPrimaryReuseEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanPrimaryReuse), "1", StringComparison.Ordinal);
        private static readonly bool? VulkanPrimaryCommandBufferReuseOverride =
            ReadOptionalBooleanEnvironmentOverride(XREngineEnvironmentVariables.VulkanPrimaryCommandBufferReuse);
        // Cached primaries can outlive mutable descriptor and GPU-publication
        // generations that are not yet represented in the variant key. Keep the
        // public setting and override intact for diagnostics, but do not execute a
        // cached primary until those generations participate in reuse validation.
        internal const bool VulkanPrimaryCommandBufferReuseSafe = false;
        private bool VulkanPrimaryCommandBufferReuseEnabled =>
            VulkanPrimaryCommandBufferReuseSafe &&
            (VulkanPrimaryCommandBufferReuseOverride ??
             RuntimeRenderingHostServices.Current.EnableVulkanPrimaryCommandBufferReuse);
        private static bool VulkanFrameDiagnosticsTraceEnabled =>
            CommandRecordingDiagnosticsEnabled ||
            XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
            XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw;

        private static bool? ReadOptionalBooleanEnvironmentOverride(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (value is "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Debug.VulkanWarning("[Vulkan] Ignoring invalid {0}='{1}'. Expected 0/1, false/true, no/yes, or off/on.", name, value);
            return null;
        }
        private FrameOpSignatureDebugPart[][]? _commandBufferFrameOpSignatureDebugParts;
        private int _frameOpSignatureDiffLogCount;
        private string? _vulkanDiagnosticBaseWindowTitle;
        private string? _vulkanDiagnosticLastTitle;
        private int _vulkanLastFrameDroppedDrawOps;
        private int _vulkanLastFrameDroppedOps;
        private readonly ThreadLocal<CommandBufferRecordingScratch> _commandBufferRecordingScratch =
            new(static () => new CommandBufferRecordingScratch());
        private readonly VulkanFrameWideMeshFrameDataReservationManifest _frameWideMeshFrameDataManifest = new();
        public ulong MeshFrameDataManifestGeneration => _frameWideMeshFrameDataManifest.Generation;
        public long MeshFrameDataManifestPublicationCount => _frameWideMeshFrameDataManifest.PublicationCount;
        public long MeshFrameDataManifestLateRegistrationCount => _frameWideMeshFrameDataManifest.LateRegistrationCount;
        public int MeshFrameDataManifestRendererCount => _frameWideMeshFrameDataManifest.PublishedRendererCount;
        public int MeshFrameDataManifestFamilyCount => _frameWideMeshFrameDataManifest.PublishedFamilyCount;
        public bool MeshFrameDataManifestIsSealed => _frameWideMeshFrameDataManifest.IsSealed;
        private readonly Dictionary<VkMeshRenderer, int> _refreshMeshDrawSlotsByRendererScratch = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VkMeshRenderer, int> _dynamicUiMeshDrawSlotsByRendererScratch = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> _refreshMeshDrawSlotsByRendererFamilyScratch =
            new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
        private readonly Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> _dynamicUiMeshDrawSlotsByRendererFamilyScratch =
            new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
        private bool _lastEnsureCommandBufferRecordedPrimary;
        private int _descriptorFrameSlotFrameCountOverride;
        internal int DescriptorFrameSlotFrameCount
        {
            get
            {
                int overrideCount = Volatile.Read(ref _descriptorFrameSlotFrameCountOverride);
                if (overrideCount > 0)
                    return overrideCount;

                return Math.Max(swapChainImages?.Length ?? 0, MAX_FRAMES_IN_FLIGHT);
            }
        }

        private bool EnsureDescriptorFrameSlotFrameCountFloor(int frameSlotCount)
        {
            if (frameSlotCount <= 0)
                return false;

            while (true)
            {
                int current = Volatile.Read(ref _descriptorFrameSlotFrameCountOverride);
                if (current >= frameSlotCount)
                    return false;

                if (Interlocked.CompareExchange(ref _descriptorFrameSlotFrameCountOverride, frameSlotCount, current) == current)
                {
                    MarkCommandBuffersDirty();
                    MarkOpenXrPrimaryCommandBufferVariantsDirty();
                    return true;
                }
            }
        }
        private int _refreshMeshDrawSlotCapacityHint = 1;
        private int _dynamicUiMeshDrawSlotCapacityHint = 1;
        private string? _lastReusableFrameDataRefreshFailureReason;
        private static readonly bool BloomVulkanDiagnosticsEnabled =
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.BloomDiag), "1", StringComparison.Ordinal);

        private readonly Dictionary<ulong, CameraPoseReuseState> _cameraPoseReuseStates = new(8);

        private bool TryGetCommandBufferDiagnosticMetadata(
            uint imageIndex,
            CommandBuffer commandBuffer,
            out ulong plannerRevision,
            out ulong frameOpContextId,
            out ulong resourceGeneration,
            out ulong descriptorGeneration)
        {
            plannerRevision = 0;
            frameOpContextId = 0;
            resourceGeneration = 0;
            descriptorGeneration = 0;
            if (_commandBufferVariants is null || imageIndex >= (uint)_commandBufferVariants.Length)
                return false;

            List<CommandBufferCacheVariant> variants = _commandBufferVariants[imageIndex];
            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                if (variant.PrimaryCommandBuffer.Handle != commandBuffer.Handle)
                    continue;

                plannerRevision = variant.PlannerRevision == ulong.MaxValue ? 0 : variant.PlannerRevision;
                frameOpContextId = variant.RecordedFrameOpContextId;
                resourceGeneration = variant.RecordedResourceGeneration;
                descriptorGeneration = variant.RecordedDescriptorGeneration;
                return true;
            }

            return false;
        }

        internal void ResetCommandBufferBindState(CommandBuffer commandBuffer)
        {
            ResetVulkanCommandBufferLifetime(commandBuffer);
            ulong key = (ulong)commandBuffer.Handle;
            CommandBufferBindState state = new()
            {
                RecordingGeneration = unchecked((ulong)Interlocked.Increment(ref _commandBufferRecordingGeneration)),
            };
            lock (_commandBindStateLock)
                _commandBindStates[key] = state;
            BeginCommandBufferTrackingBatch(commandBuffer);
            ResetRecordedImageLayoutState(commandBuffer);
        }

        private ulong ResolveCommandBufferRecordingGeneration(CommandBuffer commandBuffer)
        {
            if (commandBuffer.Handle == 0)
                return 0;

            ulong key = unchecked((ulong)commandBuffer.Handle);
            lock (_commandBindStateLock)
                return _commandBindStates.TryGetValue(key, out CommandBufferBindState state)
                    ? state.RecordingGeneration
                    : 0;
        }

        private void InvalidateDescriptorHeapBindingState(CommandBuffer commandBuffer)
        {
            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
            {
                _commandBindStates.TryGetValue(key, out CommandBufferBindState state);
                state.DescriptorHeapSignature = 0;
                _commandBindStates[key] = state;
            }
        }

        private void InvalidateDescriptorSetBindingState(CommandBuffer commandBuffer)
        {
            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
            {
                _commandBindStates.TryGetValue(key, out CommandBufferBindState state);
                state.GraphicsDescriptorSignature = 0;
                state.ComputeDescriptorSignature = 0;
                _commandBindStates[key] = state;
            }
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
            => RemoveCommandBufferBindStateByHandle(unchecked((ulong)commandBuffer.Handle));

        private void RemoveCommandBufferBindStateByHandle(ulong key)
        {
            lock (_commandBindStateLock)
            {
                _commandBindStates.Remove(key);
                _commandBufferImageIndices.Remove(key);
            }

            CommandBuffer commandBuffer = new() { Handle = unchecked((nint)key) };
            ReleaseRecordedImageLayoutState(commandBuffer);
            RemoveCommandBufferTrackingBatch(commandBuffer);
            RemoveVulkanCommandBufferLifetime(commandBuffer);
        }

        internal void BindPipelineTracked(CommandBuffer commandBuffer, PipelineBindPoint bindPoint, Pipeline pipeline)
        {
            if (pipeline.Handle == 0)
                return;

            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Pipeline,
                pipeline.Handle,
                "Pipeline.Bind");

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

            TryBindDescriptorHeapsTracked(commandBuffer);
            Api!.CmdBindPipeline(commandBuffer, bindPoint, pipeline);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(pipelineBinds: 1);
        }

        internal void SetViewportScissorTracked(
            CommandBuffer commandBuffer,
            in Viewport viewport,
            in Rect2D scissor)
        {
            ulong signature = ComputeViewportScissorSignature(viewport, scissor);
            if (!ShouldSetViewportScissor(commandBuffer, signature))
                return;

            Viewport viewportCopy = viewport;
            Rect2D scissorCopy = scissor;
            Api!.CmdSetViewport(commandBuffer, 0, 1, &viewportCopy);
            Api!.CmdSetScissor(commandBuffer, 0, 1, &scissorCopy);
        }

        internal void SetViewportScissorTracked(
            CommandBuffer commandBuffer,
            Viewport[] viewports,
            Rect2D[] scissors,
            uint count)
        {
            if (count == 0)
                return;

            ulong signature = ComputeViewportScissorSignature(viewports, scissors, count);
            if (!ShouldSetViewportScissor(commandBuffer, signature))
                return;

            fixed (Viewport* viewportPtr = viewports)
            fixed (Rect2D* scissorPtr = scissors)
            {
                Api!.CmdSetViewport(commandBuffer, 0, count, viewportPtr);
                Api!.CmdSetScissor(commandBuffer, 0, count, scissorPtr);
            }
        }

        private bool ShouldSetViewportScissor(CommandBuffer commandBuffer, ulong signature)
        {
            bool shouldSet;
            ulong key = (ulong)commandBuffer.Handle;
            lock (_commandBindStateLock)
            {
                _commandBindStates.TryGetValue(key, out CommandBufferBindState state);
                shouldSet = !state.HasViewportScissorState || state.ViewportScissorSignature != signature;
                if (shouldSet)
                {
                    state.ViewportScissorSignature = signature;
                    state.HasViewportScissorState = true;
                    _commandBindStates[key] = state;
                }
            }

            return shouldSet;
        }

        private static ulong ComputeViewportScissorSignature(in Viewport viewport, in Rect2D scissor)
        {
            HashCode hash = new();
            hash.Add(1);
            AddViewportSignature(ref hash, viewport);
            AddScissorSignature(ref hash, scissor);
            return unchecked((ulong)hash.ToHashCode());
        }

        private static ulong ComputeViewportScissorSignature(Viewport[] viewports, Rect2D[] scissors, uint count)
        {
            HashCode hash = new();
            hash.Add(count);
            int boundedCount = Math.Min((int)count, Math.Min(viewports.Length, scissors.Length));
            for (int i = 0; i < boundedCount; i++)
            {
                AddViewportSignature(ref hash, viewports[i]);
                AddScissorSignature(ref hash, scissors[i]);
            }

            return unchecked((ulong)hash.ToHashCode());
        }

        private static void AddViewportSignature(ref HashCode hash, in Viewport viewport)
        {
            hash.Add(BitConverter.SingleToInt32Bits(viewport.X));
            hash.Add(BitConverter.SingleToInt32Bits(viewport.Y));
            hash.Add(BitConverter.SingleToInt32Bits(viewport.Width));
            hash.Add(BitConverter.SingleToInt32Bits(viewport.Height));
            hash.Add(BitConverter.SingleToInt32Bits(viewport.MinDepth));
            hash.Add(BitConverter.SingleToInt32Bits(viewport.MaxDepth));
        }

        private static void AddScissorSignature(ref HashCode hash, in Rect2D scissor)
        {
            hash.Add(scissor.Offset.X);
            hash.Add(scissor.Offset.Y);
            hash.Add(scissor.Extent.Width);
            hash.Add(scissor.Extent.Height);
        }

        internal void BindDescriptorSetsTracked(
            CommandBuffer commandBuffer,
            PipelineBindPoint bindPoint,
            PipelineLayout layout,
            uint firstSet,
            DescriptorSet[] sets)
            => BindDescriptorSetsTracked(commandBuffer, bindPoint, layout, firstSet, (ReadOnlySpan<DescriptorSet>)sets, ReadOnlySpan<uint>.Empty);

        internal void BindDescriptorSetTracked(
            CommandBuffer commandBuffer,
            PipelineBindPoint bindPoint,
            PipelineLayout layout,
            uint firstSet,
            DescriptorSet descriptorSet)
        {
            Span<DescriptorSet> sets = stackalloc DescriptorSet[1];
            sets[0] = descriptorSet;
            BindDescriptorSetsTracked(
                commandBuffer,
                bindPoint,
                layout,
                firstSet,
                sets,
                ReadOnlySpan<uint>.Empty);
        }

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

            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.PipelineLayout,
                layout.Handle,
                "DescriptorSet.PipelineLayout");
            for (int i = 0; i < sets.Length; i++)
                TrackVulkanDescriptorSetBinding(commandBuffer, sets[i]);

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

            InvalidateDescriptorHeapBindingState(commandBuffer);
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

            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.PipelineLayout,
                layout.Handle,
                "PushConstants.PipelineLayout");

            T localValue = value;
            Api!.CmdPushConstants(
                commandBuffer,
                layout,
                stageFlags,
                offset,
                (uint)sizeof(T),
                &localValue);
            InvalidateDescriptorHeapBindingState(commandBuffer);
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

            for (int i = 0; i < buffers.Length; i++)
            {
                TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.Buffer,
                    buffers[i].Handle,
                    "VertexBuffer.Bind");
            }

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

        internal void BindVertexBufferTracked(
            CommandBuffer commandBuffer,
            uint binding,
            Silk.NET.Vulkan.Buffer buffer,
            ulong offset)
        {
            if (buffer.Handle == 0)
                return;

            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                buffer.Handle,
                "VertexBuffer.Bind");

            HashCode hash = new();
            hash.Add(binding);
            hash.Add(1);
            hash.Add(buffer.Handle);
            hash.Add(offset);

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

            Silk.NET.Vulkan.Buffer localBuffer = buffer;
            ulong localOffset = offset;
            Api!.CmdBindVertexBuffers(commandBuffer, binding, 1, &localBuffer, &localOffset);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(vertexBufferBinds: 1);
        }

        internal void BindIndexBufferTracked(CommandBuffer commandBuffer, Silk.NET.Vulkan.Buffer indexBuffer, ulong offset, IndexType indexType)
        {
            if (indexBuffer.Handle == 0)
                return;

            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                indexBuffer.Handle,
                "IndexBuffer.Bind");

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
            if (_commandBuffers is null &&
                _commandBufferVariants is null &&
                _dynamicUiBatchTextSecondaryCommandBuffers is null &&
                _dynamicUiBatchTextOverlayCommandBuffers is null &&
                _imguiOverlayCommandBuffers is null &&
                _commandChainCaches is null &&
                _externalCommandChainCaches is null &&
                !HasTrackedCommandChainSecondaryPools() &&
                _computeTransientResources is null &&
                _computeDescriptorCaches is null &&
                _deferredSecondaryCommandBuffers is null)
            {
                return;
            }

            CancelCommandChainRecordingWorkers();
            DestroySwapchainCommandBuffers(cancelCommandChainWorkers: false);
            DestroyComputeTransientResources();
            DestroyDeferredSecondaryCommandBuffers();
            DestroyExternalCommandChainCaches();
            DestroyTrackedCommandChainSecondaryPools();
            DestroyComputeDescriptorCaches();
            _externalCommandChainCaches = null;
            ClearTrackedCommandChainSecondaryPools();
        }

        private void DestroySwapchainCommandBuffers()
            => DestroySwapchainCommandBuffers(cancelCommandChainWorkers: true);

        private void DestroySwapchainCommandBuffers(bool cancelCommandChainWorkers)
        {
            if (_commandBuffers is null &&
                _commandBufferVariants is null &&
                _dynamicUiBatchTextSecondaryCommandBuffers is null &&
                _dynamicUiBatchTextOverlayCommandBuffers is null &&
                _imguiOverlayCommandBuffers is null &&
                _commandChainCaches is null)
            {
                return;
            }

            if (cancelCommandChainWorkers)
                CancelCommandChainRecordingWorkers();

            int indexedFrameSlotCount = _commandBuffers?.Length ?? 0;
            DestroyIndexedCommandChainCaches();
            for (int i = 0; i < indexedFrameSlotCount; i++)
                ReleaseDeferredSecondaryCommandBuffers(unchecked((uint)i));
            // Indexed worker pools own only buffers from the caches destroyed
            // above. Recreate the per-slot pools lazily so a swapchain image-
            // count change cannot retain an incompatible pool array.
            DestroyCommandChainRecordingWorkerPools();

            DestroyCommandBufferVariants();
            DestroyDynamicUiBatchTextSecondaryCommandBuffers();
            DestroyDynamicUiBatchTextOverlayCommandBuffers();
            DestroyImGuiOverlayCommandBuffers();

            if (_commandBuffers is not null)
            {
                if (_deviceLost)
                {
                    foreach (CommandBuffer commandBuffer in _commandBuffers)
                        RemoveCommandBufferBindState(commandBuffer);
                }
                else
                {
                    fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
                    {
                        if (_commandBuffers.Length > 0)
                        {
                            FreeVulkanCommandBuffersTracked(
                                commandPool,
                                (uint)_commandBuffers.Length,
                                commandBuffersPtr,
                                "CommandBuffers.DestroySwapchainPrimary");
                        }
                    }
                }
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
                            FreeVulkanCommandBufferTracked(commandPool, ref primary, "CommandBuffers.DestroyVariantPrimary");
                        RemoveCommandBufferBindState(primary);
                    }

                    CommandBuffer secondary = variant.DynamicUiSecondaryCommandBuffer;
                    if (secondary.Handle != 0)
                    {
                        if (variant.OwnsDynamicUiSecondaryCommandBuffer && !_deviceLost)
                            FreeVulkanCommandBufferTracked(commandPool, ref secondary, "CommandBuffers.DestroyVariantSecondary");
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
            DestroyIndexedCommandChainCaches();
            DestroyExternalCommandChainCaches();
        }

        private void DestroyIndexedCommandChainCaches()
        {
            if (_commandChainCaches is not null)
            {
                foreach (Dictionary<CommandChainKey, CommandChain>? cache in _commandChainCaches)
                {
                    if (cache is null)
                        continue;

                    foreach (CommandChain chain in cache.Values)
                        DestroyCommandChainSecondaryCommandBuffer(chain);

                    cache.Clear();
                }
            }

            _commandChainCaches = null;
            _commandChainScheduleCache = null;
            _commandChainScheduleFastSignatures = null;
        }

        private void DestroyExternalCommandChainCaches()
        {
            if (_externalCommandChainCaches is not null)
            {
                foreach (Dictionary<CommandChainKey, CommandChain> cache in _externalCommandChainCaches.Values)
                {
                    foreach (CommandChain chain in cache.Values)
                        DestroyCommandChainSecondaryCommandBuffer(chain);

                    cache.Clear();
                }

                _externalCommandChainCaches.Clear();
            }

            _externalCommandChainCaches = null;
        }

        private bool HasTrackedCommandChainSecondaryPools()
        {
            lock (_ownedCommandChainSecondaryPoolsLock)
                return _ownedCommandChainSecondaryPools.Count != 0;
        }

        private void TrackOwnedCommandChainSecondaryCommandBuffer(CommandPool pool, CommandBuffer commandBuffer)
        {
            if (pool.Handle == 0 || commandBuffer.Handle == 0)
                return;

            ulong poolHandle = pool.Handle;
            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            lock (_ownedCommandChainSecondaryPoolsLock)
            {
                if (!_ownedCommandChainSecondaryPools.TryGetValue(poolHandle, out OwnedCommandChainSecondaryPool? ownedPool))
                {
                    ownedPool = new OwnedCommandChainSecondaryPool(pool);
                    _ownedCommandChainSecondaryPools.Add(poolHandle, ownedPool);
                }

                ownedPool.CommandBuffers.Add(commandBufferHandle);
            }
        }

        private void UntrackOwnedCommandChainSecondaryCommandBuffer(CommandPool pool, CommandBuffer commandBuffer)
        {
            if (pool.Handle == 0 || commandBuffer.Handle == 0)
                return;

            if (IsCommandBufferPendingRetirement(commandBuffer))
                return;

            ulong poolHandle = pool.Handle;
            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            lock (_ownedCommandChainSecondaryPoolsLock)
            {
                if (_ownedCommandChainSecondaryPools.TryGetValue(poolHandle, out OwnedCommandChainSecondaryPool? ownedPool))
                    ownedPool.CommandBuffers.Remove(commandBufferHandle);
            }
        }

        private void UntrackOwnedCommandChainSecondaryPool(CommandPool pool)
        {
            if (pool.Handle == 0)
                return;

            lock (_ownedCommandChainSecondaryPoolsLock)
                _ownedCommandChainSecondaryPools.Remove(pool.Handle);
        }

        private void MarkOwnedCommandChainSecondaryPoolPendingDestroy(CommandPool pool)
        {
            if (pool.Handle == 0)
                return;

            bool destroyNow = false;
            lock (_ownedCommandChainSecondaryPoolsLock)
            {
                if (_ownedCommandChainSecondaryPools.TryGetValue(pool.Handle, out OwnedCommandChainSecondaryPool? ownedPool))
                {
                    ownedPool.PendingDestroy = true;
                    if (ownedPool.CommandBuffers.Count == 0)
                    {
                        _ownedCommandChainSecondaryPools.Remove(pool.Handle);
                        destroyNow = true;
                    }
                }
                else
                {
                    destroyNow = true;
                }
            }

            if (destroyNow)
                Api!.DestroyCommandPool(device, pool, null);
        }

        private void DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(CommandPool pool)
        {
            if (pool.Handle == 0)
                return;

            bool destroyNow = false;
            lock (_ownedCommandChainSecondaryPoolsLock)
            {
                if (_ownedCommandChainSecondaryPools.TryGetValue(pool.Handle, out OwnedCommandChainSecondaryPool? ownedPool) &&
                    ownedPool.PendingDestroy &&
                    ownedPool.CommandBuffers.Count == 0)
                {
                    _ownedCommandChainSecondaryPools.Remove(pool.Handle);
                    destroyNow = true;
                }
            }

            if (destroyNow)
                Api!.DestroyCommandPool(device, pool, null);
        }

        private void ClearTrackedCommandChainSecondaryPools()
        {
            lock (_ownedCommandChainSecondaryPoolsLock)
                _ownedCommandChainSecondaryPools.Clear();
        }

        private void DestroyTrackedCommandChainSecondaryPools()
        {
            OwnedCommandChainSecondaryPool[] ownedPools;
            lock (_ownedCommandChainSecondaryPoolsLock)
            {
                if (_ownedCommandChainSecondaryPools.Count == 0)
                    return;

                ownedPools = [.. _ownedCommandChainSecondaryPools.Values];
                _ownedCommandChainSecondaryPools.Clear();
            }

            int destroyedPoolCount = 0;
            int trackedCommandBufferCount = 0;
            foreach (OwnedCommandChainSecondaryPool ownedPool in ownedPools)
            {
                CommandPool pool = ownedPool.Pool;
                foreach (ulong commandBufferHandle in ownedPool.CommandBuffers)
                {
                    RemoveCommandBufferBindStateByHandle(commandBufferHandle);
                    trackedCommandBufferCount++;
                }

                DiscardDeferredSecondaryCommandBuffersForPool(pool);
                if (pool.Handle != 0)
                {
                    Api!.DestroyCommandPool(device, pool, null);
                    destroyedPoolCount++;
                }
            }

            if (destroyedPoolCount > 0 || trackedCommandBufferCount > 0)
            {
                Debug.Vulkan(
                    "[Vulkan.CommandChains] Destroyed {0} tracked command-chain secondary pools and cleared {1} command-buffer bind states during teardown.",
                    destroyedPoolCount,
                    trackedCommandBufferCount);
            }
        }

        private void DiscardDeferredSecondaryCommandBuffersForPool(CommandPool pool)
        {
            if (_deferredSecondaryCommandBuffers is null || pool.Handle == 0)
                return;

            ulong poolHandle = pool.Handle;
            for (int i = 0; i < _deferredSecondaryCommandBuffers.Length; i++)
            {
                List<DeferredSecondaryCommandBuffer>? deferred = _deferredSecondaryCommandBuffers[i];
                if (deferred is null || deferred.Count == 0)
                    continue;

                for (int j = deferred.Count - 1; j >= 0; j--)
                {
                    DeferredSecondaryCommandBuffer entry = deferred[j];
                    if (entry.Pool.Handle != poolHandle)
                        continue;

                    CommandBuffer secondary = entry.CommandBuffer;
                    RemoveCommandBufferBindState(secondary);
                    UntrackOwnedCommandChainSecondaryCommandBuffer(pool, secondary);
                    deferred.RemoveAt(j);
                }
            }
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
                    FreeVulkanCommandBuffersTracked(commandPool, (uint)_dynamicUiBatchTextSecondaryCommandBuffers.Length, commandBuffersPtr, "CommandBuffers.DestroyDynamicUiSecondary");
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
                    FreeVulkanCommandBuffersTracked(commandPool, (uint)_dynamicUiBatchTextOverlayCommandBuffers.Length, commandBuffersPtr, "CommandBuffers.DestroyDynamicUiOverlay");
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
                    FreeVulkanCommandBuffersTracked(commandPool, (uint)_imguiOverlayCommandBuffers.Length, commandBuffersPtr, "CommandBuffers.DestroyImGuiOverlay");
            }

            foreach (CommandBuffer commandBuffer in _imguiOverlayCommandBuffers)
                RemoveCommandBufferBindState(commandBuffer);

            _imguiOverlayCommandBuffers = null;
        }

        private void EnsureCommandBufferFrameDataSlotCapacity(int frameDataSlotCount)
        {
            if (frameDataSlotCount <= 0)
                return;

            if (_computeTransientResources is null)
            {
                _computeTransientResources = new ComputeTransientResources[frameDataSlotCount];
            }
            else if (_computeTransientResources.Length < frameDataSlotCount)
            {
                Array.Resize(ref _computeTransientResources, frameDataSlotCount);
            }

            if (_deferredSecondaryCommandBuffers is null)
            {
                _deferredSecondaryCommandBuffers = new List<DeferredSecondaryCommandBuffer>[frameDataSlotCount];
            }
            else if (_deferredSecondaryCommandBuffers.Length < frameDataSlotCount)
            {
                Array.Resize(ref _deferredSecondaryCommandBuffers, frameDataSlotCount);
            }

            EnsureComputeDescriptorCacheCapacity(frameDataSlotCount);
            EnsureDynamicUniformRingBufferCapacity(frameDataSlotCount);
            EnsureFrameTimingSlotCapacity(frameDataSlotCount);
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
                    UntrackOwnedCommandChainSecondaryCommandBuffer(entry.Pool, secondary);
                    DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(entry.Pool);
                    continue;
                }

                FreeVulkanCommandBufferTracked(entry.Pool, ref secondary, "CommandBuffers.DeferredSecondary");
                RemoveCommandBufferBindState(secondary);
                UntrackOwnedCommandChainSecondaryCommandBuffer(entry.Pool, entry.CommandBuffer);
                DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(entry.Pool);
            }

            deferred.Clear();
        }

        private void DeferSecondaryCommandBufferFree(uint imageIndex, CommandPool pool, CommandBuffer commandBuffer)
        {
            if (commandBuffer.Handle == 0 || pool.Handle == 0)
                return;

            if (_deferredSecondaryCommandBuffers is null || imageIndex >= _deferredSecondaryCommandBuffers.Length)
            {
                FreeVulkanCommandBufferTracked(pool, ref commandBuffer, "CommandBuffers.OwnedSecondary");
                RemoveCommandBufferBindState(commandBuffer);
                UntrackOwnedCommandChainSecondaryCommandBuffer(pool, commandBuffer);
                DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(pool);
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
                        RetireDescriptorPool(descriptorPool);
                        resources.DescriptorPools[i] = default;
                        if (i < resources.DescriptorPoolSignatures.Count)
                            resources.DescriptorPoolSignatures[i] = 0;
                    }
                    else
                    {
                        Result resetResult = ResetVulkanDescriptorPoolTracked(descriptorPool);
                        if (resetResult == Result.Success)
                        {
                            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolReset();
                        }
                        else
                        {
                            RetireDescriptorPool(descriptorPool);
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
                        {
                            RegisterVulkanDescriptorSets(
                                pool,
                                sets,
                                requireUpdateAfterBind,
                                "RendererOwned.DescriptorSet");
                            SetDebugDescriptorSetNames(sets, "RendererOwned.DescriptorSet");
                            RecordVulkanDescriptorTableGeneration("RendererOwnedDescriptorSets.Allocated");
                            return true;
                        }

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
