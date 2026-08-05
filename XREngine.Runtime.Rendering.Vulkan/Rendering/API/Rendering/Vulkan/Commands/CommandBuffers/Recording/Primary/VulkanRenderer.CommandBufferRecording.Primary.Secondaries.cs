using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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

        private bool TryResolveMeshSecondaryInheritance(scoped ref PrimaryCommandBufferRecordingState recordingState,
            XRFrameBuffer? target,
            int passIndex,
            in FrameOpContext context,
            out bool inheritedDynamicRendering,
            out RenderPass inheritedRenderPass,
            out Framebuffer inheritedFramebuffer,
            out DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
            out FrameBufferAttachmentSignature[]? inheritedFboAttachmentSignature,
            out bool inheritedDepthStencilReadOnly,
            out SampleCountFlags inheritedSamples,
            out DynamicRenderingLocalReadSignature inheritedLocalReadSignature,
            out RenderingFlags inheritedRenderingFlags)
        {
            inheritedDynamicRendering = false;
            inheritedRenderPass = default;
            inheritedFramebuffer = default;
            inheritedDynamicRenderingFormats = default;
            inheritedFboAttachmentSignature = null;
            inheritedDepthStencilReadOnly = false;
            inheritedSamples = SampleCountFlags.Count1Bit;
            inheritedLocalReadSignature = default;
            inheritedRenderingFlags = 0;

            if (target is null)
            {
                bool useDynamicRendering = UseDynamicRenderingRenderTargets &&
                    recordingState.SwapchainTarget.IsValid;

                if (useDynamicRendering)
                {
                    inheritedDynamicRendering = true;
                    inheritedDynamicRenderingFormats = CreateSwapchainDynamicRenderingFormatSignature(recordingState.SwapchainTarget.ImageFormat, recordingState.SwapchainTarget.DepthFormat);
                    inheritedDepthStencilReadOnly = false;
                    inheritedSamples = SampleCountFlags.Count1Bit;
                    return true;
                }

                if (swapChainFramebuffers is null || recordingState.ImageIndex >= swapChainFramebuffers.Length)
                {
                    LogCommandChainSecondaryInheritanceMismatch(
                        "mesh",
                        null,
                        passIndex,
                        "legacy swapchain framebuffer is unavailable");
                    return false;
                }

                inheritedRenderPass = (recordingState.SwapchainClearedThisFrame || recordingState.SwapchainWrittenOutsideRenderPass)
                    ? _renderPassLoad
                    : _renderPass;
                inheritedFramebuffer = swapChainFramebuffers[recordingState.ImageIndex];
                if (inheritedRenderPass.Handle == 0 || inheritedFramebuffer.Handle == 0)
                {
                    LogCommandChainSecondaryInheritanceMismatch(
                        "mesh",
                        null,
                        passIndex,
                        $"legacy swapchain inheritance unavailable renderPass=0x{inheritedRenderPass.Handle:X} framebuffer=0x{inheritedFramebuffer.Handle:X}");
                    return false;
                }

                return true;
            }

            var vkFrameBuffer = GenericToAPI<VkFrameBuffer>(target);
            if (vkFrameBuffer is null)
            {
                LogCommandChainSecondaryInheritanceMismatch(
                    "mesh",
                    target,
                    passIndex,
                    "target does not have a Vulkan framebuffer");
                return false;
            }

            vkFrameBuffer.EnsureCurrent();

            bool targetReenteredThisCommandBuffer = recordingState.FboLayoutTracking.ContainsKey(target);
            ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(target, vkFrameBuffer);
            FrameBufferAttachmentSignature[] fboSignature = vkFrameBuffer.ResolveAttachmentSignatureForPass(
                passIndex,
                context.PassMetadata,
                trackedLayouts,
                CompiledRenderGraph.Synchronization,
                preserveTrackedClearLoads: targetReenteredThisCommandBuffer);

            inheritedDepthStencilReadOnly = VkFrameBuffer.UsesReadOnlyDepthStencil(fboSignature);

            if (UseDynamicRenderingRenderTargets)
            {
                inheritedDynamicRendering = true;
                uint fboViewMask = vkFrameBuffer.MultiviewViewMask;
                inheritedDynamicRenderingFormats = CreateDynamicRenderingFormatSignature(
                    fboSignature,
                    fboViewMask,
                    VulkanDynamicRenderingUtilities.ResolveLayerCount(vkFrameBuffer.FramebufferLayers, fboViewMask));
                inheritedFboAttachmentSignature = fboSignature;
                inheritedSamples = ResolveDynamicRenderingSamples(fboSignature);
                return true;
            }

            inheritedRenderPass = vkFrameBuffer.ResolveRenderPassForPass(
                passIndex,
                context.PassMetadata,
                trackedLayouts,
                CompiledRenderGraph.Synchronization,
                preserveTrackedClearLoads: targetReenteredThisCommandBuffer);
            inheritedFramebuffer = vkFrameBuffer.FrameBuffer;
            if (inheritedRenderPass.Handle == 0 || inheritedFramebuffer.Handle == 0)
            {
                LogCommandChainSecondaryInheritanceMismatch(
                    "mesh",
                    target,
                    passIndex,
                    $"legacy FBO inheritance unavailable renderPass=0x{inheritedRenderPass.Handle:X} framebuffer=0x{inheritedFramebuffer.Handle:X}");
                return false;
            }

            return true;
        }

        private static SampleCountFlags ResolveDynamicRenderingSamples(FrameBufferAttachmentSignature[]? signatures)
        {
            if (signatures is { Length: > 0 })
            {
                for (int i = 0; i < signatures.Length; i++)
                {
                    if (signatures[i].Role == AttachmentRole.Color)
                        return signatures[i].Samples;
                }

                return signatures[0].Samples;
            }

            return SampleCountFlags.Count1Bit;
        }

        private bool ActiveMeshSecondaryInheritanceMatches(scoped ref PrimaryCommandBufferRecordingState recordingState,
            string secondaryKind,
            XRFrameBuffer? target,
            int passIndex,
            bool expectedDynamicRendering,
            RenderPass expectedRenderPass,
            Framebuffer expectedFramebuffer,
            DynamicRenderingFormatSignature expectedDynamicRenderingFormats,
            bool expectedDepthStencilReadOnly,
            SampleCountFlags expectedSamples,
            in DynamicRenderingLocalReadSignature expectedLocalReadSignature,
            RenderingFlags expectedRenderingFlags)
        {
            string? reason = null;
            if (!recordingState.RenderScope.IsActive)
            {
                reason = "no render scope is active";
            }
            else if (recordingState.ActivePassIndex != passIndex)
            {
                reason = $"active pass {recordingState.ActivePassIndex} differs from expected pass {passIndex}";
            }
            else if (recordingState.RenderScope.Target != target)
            {
                reason = $"active target '{recordingState.RenderScope.Target?.Name ?? "<swapchain>"}' differs from expected target '{target?.Name ?? "<swapchain>"}'";
            }
            else if (recordingState.RenderScope.UsesDynamicRendering != expectedDynamicRendering)
            {
                reason = $"active dynamic-rendering mode {recordingState.RenderScope.UsesDynamicRendering} differs from expected mode {expectedDynamicRendering}";
            }
            else if (expectedDynamicRendering)
            {
                SampleCountFlags activeSamples = ResolveDynamicRenderingSamples(recordingState.RenderScope.AttachmentSignature);
                if (!recordingState.RenderScope.DynamicRenderingFormats.Equals(expectedDynamicRenderingFormats) ||
                    recordingState.RenderScope.DepthStencilReadOnly != expectedDepthStencilReadOnly ||
                    activeSamples != expectedSamples ||
                    !recordingState.RenderScope.LocalReadSignature.Equals(
                        expectedLocalReadSignature) ||
                    recordingState.RenderScope.InheritanceRenderingFlags !=
                        expectedRenderingFlags)
                {
                    reason =
                        $"active dynamic inheritance colors=[{recordingState.RenderScope.DynamicRenderingFormats.DescribeColorFormats()}] " +
                        $"depth={recordingState.RenderScope.DynamicRenderingFormats.DepthAttachmentFormat} stencil={recordingState.RenderScope.DynamicRenderingFormats.StencilAttachmentFormat} " +
                        $"viewMask=0x{recordingState.RenderScope.DynamicRenderingFormats.ViewMask:X} layers={recordingState.RenderScope.DynamicRenderingFormats.LayerCount} samples={activeSamples} depthReadOnly={recordingState.RenderScope.DepthStencilReadOnly} localRead=0x{recordingState.RenderScope.LocalReadSignature.GetHashCode():X8} flags={recordingState.RenderScope.InheritanceRenderingFlags} " +
                        $"differs from expected colors=[{expectedDynamicRenderingFormats.DescribeColorFormats()}] " +
                        $"depth={expectedDynamicRenderingFormats.DepthAttachmentFormat} stencil={expectedDynamicRenderingFormats.StencilAttachmentFormat} " +
                        $"viewMask=0x{expectedDynamicRenderingFormats.ViewMask:X} layers={expectedDynamicRenderingFormats.LayerCount} samples={expectedSamples} depthReadOnly={expectedDepthStencilReadOnly} localRead=0x{expectedLocalReadSignature.GetHashCode():X8} flags={expectedRenderingFlags}";
                }
            }
            else if (recordingState.RenderScope.RenderPass.Handle != expectedRenderPass.Handle ||
                recordingState.RenderScope.Framebuffer.Handle != expectedFramebuffer.Handle)
            {
                reason =
                    $"active legacy inheritance renderPass=0x{recordingState.RenderScope.RenderPass.Handle:X} framebuffer=0x{recordingState.RenderScope.Framebuffer.Handle:X} " +
                    $"differs from expected renderPass=0x{expectedRenderPass.Handle:X} framebuffer=0x{expectedFramebuffer.Handle:X}";
            }

            if (reason is null)
                return true;

            LogCommandChainSecondaryInheritanceMismatch(
                secondaryKind,
                target,
                passIndex,
                reason);
            return false;
        }

        internal bool TryExecuteIndirectCommandChainSecondaryRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, int runCount, int passIndex, IndirectDrawOp firstDraw)
        {
            EVulkanIndirectSecondaryEligibility eligibility =
                EvaluateIndirectSecondaryRecordingContract(firstDraw);
            if (eligibility !=
                EVulkanIndirectSecondaryEligibility.
                    EligibleProducerComplete)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.
                    RecordVulkanIndirectSecondaryEligibility(eligibility);
                return false;
            }

            if (!CommandChainsEnabledForCurrentRecording ||
                !_enableSecondaryCommandBuffers ||
                runCount <= 0)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.
                    RecordVulkanIndirectSecondaryEligibility(
                        runCount <= 0
                            ? EVulkanIndirectSecondaryEligibility.
                                ResourcePreparationFailed
                            : EVulkanIndirectSecondaryEligibility.
                                CommandChainsDisabled);
                return false;
            }

            if (!TryResolveMeshSecondaryInheritance(ref recordingState,
                    firstDraw.Target,
                    passIndex,
                    firstDraw.Context,
                    out bool inheritedDynamicRendering,
                    out RenderPass inheritedRenderPass,
                    out Framebuffer inheritedFramebuffer,
                    out DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
                    out _,
                    out bool inheritedDepthStencilReadOnly,
                    out SampleCountFlags inheritedSamples,
                    out DynamicRenderingLocalReadSignature inheritedLocalReadSignature,
                    out RenderingFlags inheritedRenderingFlags))
            {
                RuntimeEngine.Rendering.Stats.Vulkan.
                    RecordVulkanIndirectSecondaryEligibility(
                        EVulkanIndirectSecondaryEligibility.
                            UnsupportedInheritance,
                        runCount);
                return false;
            }

            CommandBuffer[] secondaryBuffers = ArrayPool<CommandBuffer>.Shared.Rent(runCount);
            CommandChain[] secondaryChains = ArrayPool<CommandChain>.Shared.Rent(runCount);
            int[] uniformSlots = ArrayPool<int>.Shared.Rent(runCount);
            VkMeshRenderer.IndirectDrawRecordingState[] recordingStates = ArrayPool<VkMeshRenderer.IndirectDrawRecordingState>.Shared.Rent(runCount);
            bool[] recordingStatePrepared = ArrayPool<bool>.Shared.Rent(runCount);
            Exception? firstError = null;

            bool indirectLabelActive = false;
            if (CanRecordCommandBufferDebugLabels)
            {
                indirectLabelActive = CmdBeginLabel(recordingState.CommandBuffer, $"IndirectCommandChainSecondary[{runCount}]");
            }

            try
            {
                EmitIndirectDrawRunReadBarrier(ref recordingState);

                for (int i = 0; i < runCount; i++)
                {
                    secondaryBuffers[i] = default;
                    secondaryChains[i] = null!;
                    recordingStates[i] = default;
                    recordingStatePrepared[i] = false;
                    IndirectDrawOp indirectOp = (IndirectDrawOp)recordingState.Ops[startIndex + i];
                    uniformSlots[i] = GetMeshDrawUniformSlot(ref recordingState,
                        startIndex + i,
                        indirectOp.MeshRenderer,
                        indirectOp.Context,
                        indirectOp.Draw);
                }

                for (int i = 0; i < runCount; i++)
                {
                    IndirectDrawOp indirectOp = (IndirectDrawOp)recordingState.Ops[startIndex + i];
                    indirectOp.MeshRenderer.EnsureUniformDrawSlotCapacity(uniformSlots[i] + 1);
                }

                for (int i = 0; i < runCount; i++)
                {
                    IndirectDrawOp indirectOp = (IndirectDrawOp)recordingState.Ops[startIndex + i];
                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(indirectOp.Context.PipelineInstance);
                    using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(indirectOp.Context);
                    if (!indirectOp.MeshRenderer.TryPrepareIndirectDrawRecordingState(
                            recordingState.FrameDataImageIndex,
                            indirectOp.Draw,
                            inheritedRenderPass,
                            inheritedDynamicRendering,
                            inheritedDynamicRenderingFormats,
                            passIndex,
                            indirectOp.Context.PassMetadata,
                            inheritedDepthStencilReadOnly,
                            indirectOp.Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                            uniformSlots[i],
                            out recordingStates[i],
                            out string prepareReason))
                    {
                        Debug.VulkanWarningEvery(
                            $"Vulkan.IndirectSecondary.PrepareFailed.{GetHashCode()}.{indirectOp.MeshRenderer.GetHashCode()}.{prepareReason}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Indirect secondary pre-worker state capture failed. mesh='{0}' target='{1}' slot={2} reason={3}",
                            indirectOp.MeshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                            indirectOp.Target?.Name ?? "<swapchain>",
                            uniformSlots[i],
                            prepareReason);
                        RuntimeEngine.Rendering.Stats.Vulkan.
                            RecordVulkanIndirectSecondaryEligibility(
                                EVulkanIndirectSecondaryEligibility.
                                    ResourcePreparationFailed,
                                runCount);
                        return false;
                    }

                    recordingStatePrepared[i] = true;
                }

                Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(recordingState.FrameDataImageIndex);
                for (int i = 0; i < runCount; i++)
                {
                    IndirectDrawOp indirectOp = (IndirectDrawOp)recordingState.Ops[startIndex + i];
                    int primaryOwnedChainOrdinal = HashCode.Combine(startIndex, i, recordingState.CommandBuffer.Handle, 0x494E4452);
                    CommandChainKey chainKey = new(
                        recordingState.CommandBufferImageSlot,
                        BuildRenderViewKey(indirectOp, dynamicOverlay: false),
                        passIndex,
                        ResolveCommandChainTargetIdentity(indirectOp),
                        0UL,
                        false,
                        primaryOwnedChainOrdinal);
                    CommandChain chain = GetOrCreateCommandChain(commandChainCache, chainKey);
                    if (!TryEnsureMutableCommandChainSecondaryCommandBuffer(chain, recordingState.FrameDataImageIndex, recordingState.ExecutedCommandChainSecondaryHandles, out CommandBuffer secondary))
                    {
                        RuntimeEngine.Rendering.Stats.Vulkan.
                            RecordVulkanIndirectSecondaryEligibility(
                                EVulkanIndirectSecondaryEligibility.
                                    ResourcePreparationFailed,
                                runCount);
                        return false;
                    }

                    secondaryChains[i] = chain;
                    secondaryBuffers[i] = secondary;
                }

                VulkanRecordedCommandInheritance secondaryInheritance = new(
                    inheritedDynamicRendering,
                    inheritedRenderPass,
                    inheritedFramebuffer,
                    inheritedDynamicRenderingFormats,
                    inheritedDepthStencilReadOnly,
                    inheritedSamples,
                    inheritedLocalReadSignature,
                    inheritedRenderingFlags);

                for (int i = 0; i < runCount; i++)
                {
                    Exception? recordingError = RecordIndirectCommandChainSecondary(
                        recordingState.Ops,
                        startIndex,
                        i,
                        passIndex,
                        secondaryBuffers,
                        secondaryChains,
                        uniformSlots,
                        recordingStates,
                        in secondaryInheritance);
                    firstError ??= recordingError;
                }

                if (firstError is not null)
                    throw firstError;

                BeginRenderPassForTarget(ref recordingState, firstDraw.Target, passIndex, firstDraw.Context, secondaryContents: true);
                if (!ActiveMeshSecondaryInheritanceMatches(ref recordingState,
                        "indirect-mesh",
                        firstDraw.Target,
                        passIndex,
                        inheritedDynamicRendering,
                        inheritedRenderPass,
                        inheritedFramebuffer,
                        inheritedDynamicRenderingFormats,
                        inheritedDepthStencilReadOnly,
                        inheritedSamples,
                        inheritedLocalReadSignature,
                        inheritedRenderingFlags))
                {
                    for (int i = 0; i < runCount; i++)
                    {
                        if (secondaryChains[i] is { } chain)
                            MarkCommandChainSecondaryCommandBufferInvalid(
                                chain,
                                EVulkanRecordedCommandArtifactInvalidationReason.InheritanceMismatch);
                    }

                    RuntimeEngine.Rendering.Stats.Vulkan.
                        RecordVulkanIndirectSecondaryEligibility(
                            EVulkanIndirectSecondaryEligibility.
                                UnsupportedInheritance,
                            runCount);
                    return false;
                }

                fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                    CmdExecuteCommandsTracked(recordingState.CommandBuffer, (uint)runCount, secondaryPtr);
                for (int i = 0; i < runCount; i++)
                {
                    if (secondaryBuffers[i].Handle != 0)
                    {
                        recordingState.ExecutedCommandChainSecondaryHandles.Add(secondaryBuffers[i].Handle);
                        if (secondaryChains[i] is { } chain)
                            recordingState.ExecutedCommandChainSecondaryArtifactSequence.Add(chain);
                    }
                }

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(secondaryCommandBuffers: runCount);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectRecordingMode(
                    usedSecondary: true,
                    usedParallel: false,
                    opCount: runCount);
                RuntimeEngine.Rendering.Stats.Vulkan.
                    RecordVulkanIndirectSecondaryEligibility(
                        EVulkanIndirectSecondaryEligibility.
                            EligibleProducerComplete,
                        runCount);
                return true;
            }
            finally
            {
                EndActiveRenderPass(ref recordingState);

                Array.Clear(secondaryBuffers, 0, runCount);
                Array.Clear(secondaryChains, 0, runCount);
                Array.Clear(uniformSlots, 0, runCount);
                for (int i = 0; i < runCount; i++)
                {
                    if (recordingStatePrepared[i])
                        VkMeshRenderer.ReturnIndirectDrawRecordingStateBuffers(recordingStates[i]);
                }

                Array.Clear(recordingStates, 0, runCount);
                Array.Clear(recordingStatePrepared, 0, runCount);
                ArrayPool<CommandBuffer>.Shared.Return(secondaryBuffers);
                ArrayPool<CommandChain>.Shared.Return(secondaryChains);
                ArrayPool<int>.Shared.Return(uniformSlots);
                ArrayPool<VkMeshRenderer.IndirectDrawRecordingState>.Shared.Return(recordingStates);
                ArrayPool<bool>.Shared.Return(recordingStatePrepared);

                if (indirectLabelActive)
                    CmdEndLabel(recordingState.CommandBuffer);
            }
        }

        private Exception? RecordIndirectCommandChainSecondary(
            FrameOp[] ops,
            int startIndex,
            int relativeIndex,
            int passIndex,
            CommandBuffer[] secondaryBuffers,
            CommandChain[] secondaryChains,
            int[] uniformSlots,
            VkMeshRenderer.IndirectDrawRecordingState[] recordingStates,
            scoped in VulkanRecordedCommandInheritance inheritance)
        {
            CommandChain chain = secondaryChains[relativeIndex];
            CommandBuffer secondary = secondaryBuffers[relativeIndex];

            try
            {
                MarkCommandChainSecondaryCommandBufferInvalid(chain);
                ResetVulkanCommandBufferTracked(secondary);

                CommandBufferInheritanceInfo inheritanceInfo = new()
                {
                    SType = StructureType.CommandBufferInheritanceInfo,
                    RenderPass = inheritance.DynamicRendering ? default : inheritance.RenderPass,
                    Subpass = 0,
                    Framebuffer = inheritance.DynamicRendering ? default : inheritance.Framebuffer,
                    OcclusionQueryEnable = Vk.False,
                    QueryFlags = QueryControlFlags.None,
                    PipelineStatistics = QueryPipelineStatisticFlags.None
                };

                Format* colorAttachmentFormats = stackalloc Format[
                    (int)Math.Max(inheritance.DynamicRenderingFormats.ColorAttachmentCount, 1u)];
                CommandBufferInheritanceRenderingInfo renderingInheritanceInfo = default;
                if (inheritance.DynamicRendering)
                {
                    inheritance.DynamicRenderingFormats.CopyColorAttachmentFormats(
                        colorAttachmentFormats,
                        inheritance.DynamicRenderingFormats.ColorAttachmentCount);

                    renderingInheritanceInfo = new CommandBufferInheritanceRenderingInfo
                    {
                        SType = StructureType.CommandBufferInheritanceRenderingInfo,
                        Flags = inheritance.RenderingFlags,
                        ViewMask = inheritance.DynamicRenderingFormats.ViewMask,
                        ColorAttachmentCount = inheritance.DynamicRenderingFormats.ColorAttachmentCount,
                        PColorAttachmentFormats = inheritance.DynamicRenderingFormats.ColorAttachmentCount > 0
                            ? colorAttachmentFormats
                            : null,
                        DepthAttachmentFormat = inheritance.DynamicRenderingFormats.DepthAttachmentFormat,
                        StencilAttachmentFormat = inheritance.DynamicRenderingFormats.StencilAttachmentFormat,
                        RasterizationSamples = inheritance.Samples
                    };
                    RenderingAttachmentLocationInfo localReadAttachmentLocations = default;
                    RenderingInputAttachmentIndexInfo localReadInputIndices = default;
                    uint* colorAttachmentLocations = stackalloc uint[
                        (int)Math.Max(inheritance.DynamicRenderingFormats.ColorAttachmentCount, 1u)];
                    uint* colorInputAttachmentIndices = stackalloc uint[
                        (int)Math.Max(inheritance.DynamicRenderingFormats.ColorAttachmentCount, 1u)];
                    uint* depthInputAttachmentIndex = stackalloc uint[1];
                    uint* stencilInputAttachmentIndex = stackalloc uint[1];
                    void* localReadInheritancePNext = renderingInheritanceInfo.PNext;
                    DynamicRenderingLocalReadSignature localReadSignature =
                        inheritance.LocalReadSignature;
                    TryAppendDynamicRenderingLocalReadInheritancePNext(
                        in localReadSignature,
                        inheritance.DynamicRenderingFormats.ColorAttachmentCount,
                        ref localReadInheritancePNext,
                        &localReadAttachmentLocations,
                        &localReadInputIndices,
                        colorAttachmentLocations,
                        colorInputAttachmentIndices,
                        depthInputAttachmentIndex,
                        stencilInputAttachmentIndex);
                    renderingInheritanceInfo.PNext = localReadInheritancePNext;
                    inheritanceInfo.PNext = &renderingInheritanceInfo;
                }

                CommandBufferInheritanceDescriptorHeapInfoEXTNative descriptorHeapInheritanceInfo = default;
                BindHeapInfoEXTNative inheritedSamplerHeapInfo = default;
                BindHeapInfoEXTNative inheritedResourceHeapInfo = default;
                TryAppendDescriptorHeapInheritancePNext(
                    ref inheritanceInfo,
                    &descriptorHeapInheritanceInfo,
                    &inheritedSamplerHeapInfo,
                    &inheritedResourceHeapInfo);

                CommandBufferBeginInfo beginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.RenderPassContinueBit |
                            CommandBufferUsageFlags.SimultaneousUseBit,
                    PInheritanceInfo = &inheritanceInfo
                };

                if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                    throw new Exception("Failed to begin Vulkan indirect secondary command buffer.");

                ResetCommandBufferBindState(secondary);
                MarkCommandChainSecondaryRecording(chain, secondary);

                IndirectDrawOp indirectOp = (IndirectDrawOp)ops[startIndex + relativeIndex];
                using (RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                           indirectOp.Context.PipelineInstance))
                {
                    RecordIndirectDrawIntoSecondaryCommandBuffer(
                        secondary,
                        indirectOp,
                        recordingStates[relativeIndex],
                        passIndex,
                        inheritance.DynamicRendering,
                        inheritance.RenderPass,
                        inheritance.DynamicRenderingFormats,
                        inheritance.DepthStencilReadOnly,
                        uniformSlots[relativeIndex]);
                }

                if (EndCommandBufferTracked(secondary) != Result.Success)
                    throw new Exception("Failed to end Vulkan indirect secondary command buffer.");

                StoreCommandChainSecondaryInheritance(
                    chain,
                    inheritance.DynamicRendering,
                    inheritance.RenderPass,
                    inheritance.Framebuffer,
                    inheritance.DynamicRenderingFormats,
                    inheritance.DepthStencilReadOnly,
                    inheritance.Samples,
                    inheritance.LocalReadSignature,
                    inheritance.RenderingFlags);
                MarkCommandChainSecondaryCommandBufferRecorded(chain);
                return null;
            }
            catch (Exception ex)
            {
                DestroyCommandChainSecondaryCommandBuffer(chain);
                secondaryBuffers[relativeIndex] = default;
                return ex;
            }
        }

        private bool TryGetScheduledCommandChainForOp(scoped ref PrimaryCommandBufferRecordingState recordingState, int opIndex, out CommandChain chain, out CommandChainKey key)
        {
            chain = null!;
            key = default;
            if (recordingState.ScheduledCommandChainKeysByOpIndex is null ||
                recordingState.ScheduledCommandChainCache is null ||
                (uint)opIndex >= (uint)recordingState.ScheduledCommandChainKeysByOpIndex.Length)
            {
                return false;
            }

            key = recordingState.ScheduledCommandChainKeysByOpIndex[opIndex];
            if (key.ChainOrdinal == -1)
                return false;

            if (!recordingState.ScheduledCommandChainCache.TryGetValue(key, out CommandChain? scheduledChain))
                return false;

            if (scheduledChain.SourceStartIndex < 0 ||
                scheduledChain.SourceCount <= 0 ||
                opIndex < scheduledChain.SourceStartIndex ||
                opIndex >= scheduledChain.SourceStartIndex + scheduledChain.SourceCount)
                return false;

            chain = scheduledChain;
            return true;
        }

        private bool ScheduledCommandChainSecondaryNeedsRecording(
            CommandChain chain,
            bool dynamicRendering,
            RenderPass renderPass,
            Framebuffer framebuffer,
            DynamicRenderingFormatSignature dynamicRenderingFormats,
            bool depthStencilReadOnly,
            SampleCountFlags samples,
            in DynamicRenderingLocalReadSignature localReadSignature,
            RenderingFlags renderingFlags)
        {
            if (chain.SecondaryCommandBuffer.Handle == 0)
                return true;

            if (!chain.SecondaryCommandBufferExecutable)
                return true;

            if (chain.State == CommandChainState.FrameDataRefreshed &&
                chain.FrameDataRefreshTouchedDescriptors)
                return true;

            if (!CommandChainSecondaryInheritanceMatches(
                    chain,
                    dynamicRendering,
                    renderPass,
                    framebuffer,
                    dynamicRenderingFormats,
                    depthStencilReadOnly,
                    samples,
                    localReadSignature,
                    renderingFlags))
            {
                return true;
            }

            return chain.State is not (CommandChainState.Reused or CommandChainState.FrameDataRefreshed);
        }

        internal bool TryExecuteScheduledMeshCommandChainSecondaryRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, int runCount, int passIndex, MeshDrawOp firstDraw)
        {
            // An explicit schedule may be supplied for an external OpenXR
            // target even though the ordinary desktop eligibility gate is
            // false inside that render scope. The builder has already
            // applied the appropriate policy for this recording.
            if (!_enableSecondaryCommandBuffers ||
                recordingState.ScheduledCommandChainKeysByOpIndex is null ||
                recordingState.ScheduledCommandChainCache is null ||
                runCount <= 0 ||
                recordingState.ActiveInlineQuery is not null ||
                firstDraw.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
            {
                return false;
            }

            // Query-bracket contents are intentionally absent from the command-chain
            // schedule. Preflight the complete run before closing the current render
            // scope so an unscheduled draw cannot terminate rendering as a side effect.
            for (int i = 0; i < runCount; i++)
            {
                int opIndex = startIndex + i;
                if (recordingState.Ops[opIndex] is not MeshDrawOp drawOp ||
                    drawOp.PassIndex != passIndex ||
                    drawOp.Target != firstDraw.Target ||
                    !TryGetScheduledCommandChainForOp(ref recordingState, opIndex, out _, out _))
                {
                    return false;
                }
            }

            EndActiveRenderPass(ref recordingState);

            if (!TryResolveMeshSecondaryInheritance(ref recordingState,
                    firstDraw.Target,
                    passIndex,
                    firstDraw.Context,
                    out bool inheritedDynamicRendering,
                    out RenderPass inheritedRenderPass,
                    out Framebuffer inheritedFramebuffer,
                    out DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
                    out _,
                    out bool inheritedDepthStencilReadOnly,
                    out SampleCountFlags inheritedSamples,
                    out DynamicRenderingLocalReadSignature inheritedLocalReadSignature,
                    out RenderingFlags inheritedRenderingFlags))
            {
                return false;
            }

            CommandChainRecordingBatch batch = _commandChainRecordingBatch;
            batch.EnsureCapacity(runCount);
            batch.PreparedFrame.Begin(recordingState.CommandBufferImageSlot, VulkanFrameCounter);
            batch.PreparedFrame.AddPrimaryPlan(recordingState.PrimaryCommandPlan);
            CommandBuffer[] secondaryBuffers = batch.SecondaryBuffers;
            CommandChain[] secondaryChains = batch.Chains;
            int[] recordJobChainIndices = batch.RecordJobChainIndices;
            int[] recordJobWorkerIndices = batch.RecordJobWorkerIndices;
            int[] uniformSlots = batch.UniformSlots;
            Array.Clear(secondaryBuffers, 0, runCount);
            Array.Clear(secondaryChains, 0, runCount);
            int secondaryCount = 0;
            int scheduledOpCount = 0;
            int recordJobCount = 0;
            bool meshLabelActive = false;

            if (CanRecordCommandBufferDebugLabels)
                meshLabelActive = CmdBeginLabel(recordingState.CommandBuffer, "ScheduledMeshCommandChainSecondary");

            try
            {
                if (!TryCollectScheduledMeshCommandChainUniformSlots(
                        ref recordingState,
                        startIndex,
                        runCount,
                        passIndex,
                        firstDraw,
                        uniformSlots))
                {
                    return false;
                }
                for (int i = 0; i < runCount; i++)
                {
                    int opIndex = startIndex + i;
                    _ = TryGetScheduledCommandChainForOp(ref recordingState, opIndex, out CommandChain chain, out _);
                    if (opIndex != chain.SourceStartIndex)
                        continue;

                    if (chain.SourceCount > runCount - i)
                        return false;

                    secondaryChains[secondaryCount] = chain;

                    ulong currentUniformSlotSignature = ComputeCommandChainUniformSlotSignature(
                        uniformSlots,
                        chain.SourceStartIndex - startIndex,
                        chain.SourceCount);
                    bool uniformSlotMappingChanged =
                        chain.RecordedUniformSlotSignature != currentUniformSlotSignature;
                    bool benchmarkForcedRerecord =
                        CommandChainBenchmarkForceRerecord;
                    bool secondaryNeedsRecording =
                        ScheduledCommandChainSecondaryNeedsRecording(
                            chain,
                            inheritedDynamicRendering,
                            inheritedRenderPass,
                            inheritedFramebuffer,
                            inheritedDynamicRenderingFormats,
                            inheritedDepthStencilReadOnly,
                            inheritedSamples,
                            inheritedLocalReadSignature,
                            inheritedRenderingFlags);
                    bool needsRecording = ResolveCommandChainNeedsRecording(
                        benchmarkForcedRerecord,
                        secondaryNeedsRecording,
                        uniformSlotMappingChanged);
                    if (benchmarkForcedRerecord)
                        chain.DirtyReason |= CommandChainDirtyReason.BenchmarkForced;
                    if (uniformSlotMappingChanged && chain.SecondaryCommandBuffer.Handle != 0)
                    {
                        // Dynamic UBO offsets are baked into the secondary.
                        // Refreshing bytes at a newly assigned occurrence slot
                        // cannot make an old offset valid; re-record the chain.
                        chain.State = CommandChainState.Recorded;
                        chain.DirtyReason |= CommandChainDirtyReason.FrameDataRefreshFailed;
                    }

                    if (!needsRecording)
                    {
                        for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
                        {
                            int refreshOpIndex =
                                chain.SourceStartIndex + drawIndex;
                            if (recordingState
                                    .ScheduledCommandChainFrameDataRefreshedByOpIndex[
                                        refreshOpIndex])
                            {
                                continue;
                            }

                            MeshDrawOp refreshDraw =
                                (MeshDrawOp)recordingState.Ops[refreshOpIndex];
                            int refreshUniformSlot =
                                uniformSlots[refreshOpIndex - startIndex];
                            long descriptorSetContentUpdateGeneration =
                                SnapshotDescriptorSetContentUpdateGeneration();
                            bool refreshedFrameData =
                                refreshDraw.Draw.Renderer.TryRefreshReusableCommandBufferFrameData(
                                    recordingState.FrameDataImageIndex,
                                    refreshDraw.Draw,
                                    refreshUniformSlot,
                                    out string refreshReason,
                                    refreshMaterialUniforms: true,
                                    descriptorResourcesCapturedByFrameSignature: true);
                            bool descriptorsInvalidated =
                                HaveDescriptorSetContentsUpdatedSince(descriptorSetContentUpdateGeneration);
                            if (!refreshedFrameData || descriptorsInvalidated)
                            {
                                _lastReusableFrameDataRefreshFailureReason =
                                    $"scheduled chain op={refreshOpIndex}/{recordingState.Ops.Length} mesh='{refreshDraw.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' slot={refreshUniformSlot}: " +
                                    (descriptorsInvalidated
                                        ? "descriptor contents changed without UPDATE_AFTER_BIND"
                                        : refreshReason);
                                if (FrameDataReuseDiagnosticsEnabled)
                                {
                                    Debug.VulkanEvery(
                                        $"Vulkan.FrameDataReuse.ScheduledChain.{GetHashCode()}",
                                        TimeSpan.FromSeconds(1),
                                        "[Vulkan] Scheduled command-chain frame-data refresh failed image={0} op={1}/{2} mesh='{3}' drawSlot={4}: {5}",
                                        recordingState.FrameDataImageIndex,
                                        refreshOpIndex,
                                        recordingState.Ops.Length,
                                        refreshDraw.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                                        refreshUniformSlot,
                                        descriptorsInvalidated
                                            ? "descriptor contents changed without UPDATE_AFTER_BIND"
                                            : refreshReason);
                                }
                                needsRecording = true;
                                break;
                            }
                        }
                    }

                    if (needsRecording)
                    {
                        recordJobChainIndices[recordJobCount++] = secondaryCount;
                    }
                    else
                    {
                        chain.WorkerEligibility =
                            EVulkanCommandChainWorkerEligibility.NotEvaluated;
                        CommandBuffer reusable = chain.SecondaryCommandBuffer;
                        if (reusable.Handle == 0 || !chain.SecondaryCommandBufferExecutable)
                            return false;
                        secondaryBuffers[secondaryCount] = reusable;
                    }

                    secondaryCount++;
                    scheduledOpCount += chain.SourceCount;
                }

                if (secondaryCount == 0 || scheduledOpCount != runCount)
                    return false;

                if (recordJobCount == 0)
                {
                    // Every secondary is already executable. Do not materialize
                    // placeholder prepared draws or initialize worker state for
                    // an empty recording batch; both scale with every caster in
                    // every cascade even though no draw command will be encoded.
                    using VulkanCpuStageScope mergeStage =
                        new(EVulkanCpuStage.SecondaryMerge);
                    long mergeStart = Stopwatch.GetTimestamp();
                    for (int i = 0; i < secondaryCount; i++)
                    {
                        CommandChain chain = secondaryChains[i];
                        if (!CommandChainSecondaryInheritanceMatches(
                                chain,
                                inheritedDynamicRendering,
                                inheritedRenderPass,
                                inheritedFramebuffer,
                                inheritedDynamicRenderingFormats,
                                inheritedDepthStencilReadOnly,
                                inheritedSamples,
                                inheritedLocalReadSignature,
                                inheritedRenderingFlags))
                        {
                            MarkCommandChainSecondaryCommandBufferInvalid(chain);
                            return false;
                        }

                    }

                    TransitionSecondaryDescriptorImagesForExecution(
                        recordingState.CommandBuffer,
                        secondaryBuffers,
                        secondaryCount);

                    BeginRenderPassForTarget(
                        ref recordingState,
                        firstDraw.Target,
                        passIndex,
                        firstDraw.Context,
                        secondaryContents: true);
                    if (!ActiveMeshSecondaryInheritanceMatches(
                            ref recordingState,
                            "scheduled-mesh-reuse",
                            firstDraw.Target,
                            passIndex,
                            inheritedDynamicRendering,
                            inheritedRenderPass,
                            inheritedFramebuffer,
                            inheritedDynamicRenderingFormats,
                            inheritedDepthStencilReadOnly,
                            inheritedSamples,
                            inheritedLocalReadSignature,
                            inheritedRenderingFlags))
                    {
                        for (int i = 0; i < secondaryCount; i++)
                        {
                            MarkCommandChainSecondaryCommandBufferInvalid(
                                secondaryChains[i],
                                EVulkanRecordedCommandArtifactInvalidationReason
                                    .InheritanceMismatch);
                        }

                        return false;
                    }

                    fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                        CmdExecuteCommandsTracked(
                            recordingState.CommandBuffer,
                            (uint)secondaryCount,
                            secondaryPtr);
                    for (int i = 0; i < secondaryCount; i++)
                    {
                        if (secondaryBuffers[i].Handle != 0)
                        {
                            recordingState.ExecutedCommandChainSecondaryHandles
                                .Add(secondaryBuffers[i].Handle);
                            recordingState.ExecutedCommandChainSecondaryArtifactSequence
                                .Add(secondaryChains[i]);
                        }
                    }

                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanCommandChainMetrics(
                            secondaryCommandBuffers: secondaryCount);
                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanCommandChainWorkerMetrics(
                            reusedChains: secondaryCount,
                            mergeTime: Stopwatch.GetElapsedTime(mergeStart));
                    return true;
                }

                int preparedMeshDrawCount;
                using (VulkanCpuStageScope preparedDrawStage =
                       new(EVulkanCpuStage.PreparedDrawConstruction))
                {
                    if (!TryPrepareScheduledMeshCommandChainDraws(
                            ref recordingState,
                            startIndex,
                            passIndex,
                            batch,
                            secondaryChains,
                            secondaryBuffers,
                            secondaryCount,
                            uniformSlots,
                            inheritedRenderPass,
                            inheritedDynamicRendering,
                            inheritedDynamicRenderingFormats,
                            inheritedDepthStencilReadOnly,
                            out preparedMeshDrawCount))
                    {
                        return false;
                    }
                }

                batch.ChainCount = secondaryCount;
                batch.Chains = secondaryChains;
                batch.SecondaryBuffers = secondaryBuffers;
                batch.RecordJobChainIndices = recordJobChainIndices;
                batch.RecordJobWorkerIndices = recordJobWorkerIndices;
                batch.UniformSlots = uniformSlots;
                batch.StartIndex = startIndex;
                batch.JobCount = recordJobCount;
                batch.ActiveWorkerMask = 0;
                batch.Error = null;

                int workerEligibleJobCount = 0;
                for (int jobIndex = 0; jobIndex < recordJobCount; jobIndex++)
                {
                    CommandChain chain = secondaryChains[recordJobChainIndices[jobIndex]];
                    if (EvaluatePreparedCommandChainWorkerEncodability(batch, chain) ==
                        EVulkanCommandChainWorkerEligibility.Eligible)
                    {
                        workerEligibleJobCount++;
                    }
                }

                EVulkanCommandChainWorkerEligibility workerDomainEligibility =
                    PrepareCommandChainRecordingWorkers(
                    workerEligibleJobCount,
                    recordingState.FrameDataImageIndex,
                    out CommandChainRecordingWorkerState[] workers,
                    out int workerCount,
                    out int workerFrameSlot);
                bool useWorkers =
                    workerDomainEligibility ==
                    EVulkanCommandChainWorkerEligibility.Eligible;
                int schedulingConflictCount = 0;
                for (int jobIndex = 0; jobIndex < recordJobCount; jobIndex++)
                {
                    int chainIndex = recordJobChainIndices[jobIndex];
                    CommandChain chain = secondaryChains[chainIndex];
                    // Invalidate the whole dirty batch before any worker is
                    // released. If one worker fails, chains that had not yet
                    // begun recording cannot retain an executable old state.
                    MarkCommandChainSecondaryCommandBufferInvalid(
                        chain,
                        EVulkanRecordedCommandArtifactInvalidationReason.InheritanceMismatch);
                    VulkanCommandChainWorkerEligibilityResult assignment = useWorkers
                        ? AssignCommandChainRecordingWorker(batch, chain, workerCount)
                        : new VulkanCommandChainWorkerEligibilityResult(
                            workerDomainEligibility);
                    chain.WorkerEligibility = assignment.Reason;
                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanCommandChainWorkerEligibility(
                            assignment.Reason);
                    int recordingWorkerIndex = assignment.IsEligible
                        ? assignment.WorkerIndex
                        : -1;
                    if (useWorkers && recordingWorkerIndex < 0)
                        schedulingConflictCount++;
                    recordJobWorkerIndices[jobIndex] = recordingWorkerIndex;
                    if (recordingWorkerIndex >= 0)
                        batch.ActiveWorkerMask |= 1u << recordingWorkerIndex;

                    bool allocated = recordingWorkerIndex >= 0
                        ? TryEnsureMutableCommandChainSecondaryCommandBufferFromWorkerPool(
                            chain,
                            recordingState.FrameDataImageIndex,
                            workers[recordingWorkerIndex].Arena,
                            recordingState.ExecutedCommandChainSecondaryHandles,
                            out CommandBuffer secondary)
                        : TryEnsureMutableCommandChainSecondaryCommandBuffer(
                            chain,
                            recordingState.FrameDataImageIndex,
                            recordingState.ExecutedCommandChainSecondaryHandles,
                            out secondary);
                    if (!allocated)
                        throw new InvalidOperationException("Failed to allocate Vulkan scheduled mesh command-chain secondary command buffer.");

                    secondaryBuffers[chainIndex] = secondary;
                }

                VulkanRecordedCommandInheritance preparedInheritance = new(
                    inheritedDynamicRendering,
                    inheritedRenderPass,
                    inheritedFramebuffer,
                    inheritedDynamicRenderingFormats,
                    inheritedDepthStencilReadOnly,
                    inheritedSamples,
                    inheritedLocalReadSignature,
                    inheritedRenderingFlags);
                for (int chainIndex = 0;
                     chainIndex < secondaryCount;
                     chainIndex++)
                {
                    CommandChain chain = secondaryChains[chainIndex];
                    int preparedDrawStartIndex =
                        chain.SourceStartIndex - startIndex;
                    int preparedChainIndex =
                        batch.PreparedFrame.AddCommandChain(
                            new VulkanPreparedCommandChain(
                                chain.Key,
                                chain.SourceStartIndex,
                                chain.SourceCount,
                                preparedDrawStartIndex,
                                preparedInheritance,
                                chain.DependencySignature,
                                chain.RecordedArtifact.CreateReference(),
                                chain.WorkerEligibility));
                    if (preparedChainIndex != chainIndex)
                    {
                        throw new InvalidOperationException(
                            "Prepared Vulkan command chains lost schedule ordering.");
                    }
                }

                batch.PreparedFrame.Freeze();
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPreparedMeshDraws(
                    preparedMeshDrawCount);

                int serialRecordedCount = 0;
                int conflictCount = schedulingConflictCount;
                if (useWorkers)
                {
                    CommandChainWorkerTiming timing = DispatchCommandChainRecordingWorkers(batch, workers, workerCount);
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerMetrics(
                        queuedChains: timing.QueuedChains,
                        workersStarted: timing.WorkersStarted,
                        workersCompleted: timing.WorkersCompleted,
                        peakConcurrentWorkers: timing.PeakConcurrentWorkers,
                        queueDelay: timing.QueueDelay,
                        workerRecordTime: timing.WorkerRecordTime,
                        workerActiveSpan: timing.WorkerActiveSpan,
                        workerOverlapTime: timing.WorkerOverlapTime,
                        waitForWorkersTime: timing.WaitForWorkersTime);
                }

                using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.SecondaryRecording))
                {
                    for (int jobIndex = 0; jobIndex < recordJobCount; jobIndex++)
                    {
                        if (recordJobWorkerIndices[jobIndex] < 0)
                        {
                            RecordScheduledMeshCommandChainWorker(batch, recordJobChainIndices[jobIndex]);
                            serialRecordedCount++;
                        }
                    }
                }

                using (VulkanCpuStageScope mergeStage =
                       new(EVulkanCpuStage.SecondaryMerge))
                {
                    long mergeStart = Stopwatch.GetTimestamp();
                    for (int i = 0; i < secondaryCount; i++)
                    {
                        CommandChain chain = secondaryChains[i];
                        if (CommandChainSecondaryInheritanceMatches(
                                chain,
                                inheritedDynamicRendering,
                                inheritedRenderPass,
                                inheritedFramebuffer,
                                inheritedDynamicRenderingFormats,
                                inheritedDepthStencilReadOnly,
                                inheritedSamples,
                                inheritedLocalReadSignature,
                                inheritedRenderingFlags))
                        {
                            continue;
                        }

                        MarkCommandChainSecondaryCommandBufferInvalid(chain);
                        LogCommandChainSecondaryInheritanceMismatch(
                            "scheduled-mesh",
                            firstDraw.Target,
                            passIndex,
                            $"secondary command buffer 0x{secondaryBuffers[i].Handle:X} did not publish the resolved inheritance before execution");
                        return false;
                    }

                    TransitionSecondaryDescriptorImagesForExecution(
                        recordingState.CommandBuffer,
                        secondaryBuffers,
                        secondaryCount);

                    BeginRenderPassForTarget(ref recordingState, firstDraw.Target, passIndex, firstDraw.Context, secondaryContents: true);
                    if (!ActiveMeshSecondaryInheritanceMatches(ref recordingState,
                        "scheduled-mesh",
                        firstDraw.Target,
                        passIndex,
                            inheritedDynamicRendering,
                            inheritedRenderPass,
                            inheritedFramebuffer,
                            inheritedDynamicRenderingFormats,
                            inheritedDepthStencilReadOnly,
                            inheritedSamples,
                            inheritedLocalReadSignature,
                            inheritedRenderingFlags))
                    {
                        for (int i = 0; i < secondaryCount; i++)
                            MarkCommandChainSecondaryCommandBufferInvalid(
                                secondaryChains[i],
                                EVulkanRecordedCommandArtifactInvalidationReason.InheritanceMismatch);

                        return false;
                    }

                    fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                        CmdExecuteCommandsTracked(recordingState.CommandBuffer, (uint)secondaryCount, secondaryPtr);
                    for (int i = 0; i < secondaryCount; i++)
                    {
                        if (secondaryBuffers[i].Handle != 0)
                        {
                            recordingState.ExecutedCommandChainSecondaryHandles.Add(secondaryBuffers[i].Handle);
                            recordingState.ExecutedCommandChainSecondaryArtifactSequence.Add(secondaryChains[i]);
                        }
                    }

                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(secondaryCommandBuffers: secondaryCount);
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerMetrics(
                        seriallyRecordedChains: serialRecordedCount,
                        reusedChains: secondaryCount - recordJobCount,
                        conflictChains: conflictCount,
                        mergeTime: Stopwatch.GetElapsedTime(mergeStart));
                }
                return true;
            }
            finally
            {
                EndActiveRenderPass(ref recordingState);

                if (!batch.Abandoned)
                    batch.ClearReferences();

                if (meshLabelActive)
                    CmdEndLabel(recordingState.CommandBuffer);
            }
        }

        private bool TryCollectScheduledMeshCommandChainUniformSlots(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            int startIndex,
            int runCount,
            int passIndex,
            MeshDrawOp firstDraw,
            int[] uniformSlots)
        {
            for (int relativeIndex = 0; relativeIndex < runCount; relativeIndex++)
            {
                int opIndex = startIndex + relativeIndex;
                if (recordingState.Ops[opIndex] is not MeshDrawOp drawOp ||
                    drawOp.PassIndex != passIndex ||
                    drawOp.Target != firstDraw.Target ||
                    !TryGetScheduledCommandChainForOp(
                        ref recordingState,
                        opIndex,
                        out _,
                        out _))
                {
                    return false;
                }

                int uniformSlot =
                    recordingState.MeshDrawUniformSlotsByOpIndex[opIndex];
                if (uniformSlot < 0)
                {
                    uniformSlot = GetMeshDrawUniformSlot(
                        ref recordingState,
                        opIndex,
                        drawOp.Draw.Renderer,
                        drawOp.Context,
                        drawOp.Draw);
                }
                uniformSlots[relativeIndex] = uniformSlot;
            }

            return true;
        }

        private bool TryPrepareScheduledMeshCommandChainDraws(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            int startIndex,
            int passIndex,
            CommandChainRecordingBatch batch,
            CommandChain[] secondaryChains,
            CommandBuffer[] secondaryBuffers,
            int secondaryCount,
            int[] uniformSlots,
            RenderPass inheritedRenderPass,
            bool inheritedDynamicRendering,
            in DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
            bool inheritedDepthStencilReadOnly,
            out int preparedMeshDrawCount)
        {
            preparedMeshDrawCount = 0;
            int reservedDrawStart =
                batch.PreparedFrame.ReserveMeshDrawSlots(
                    secondaryChains[secondaryCount - 1].SourceStartIndex +
                    secondaryChains[secondaryCount - 1].SourceCount -
                    startIndex);
            if (reservedDrawStart != 0)
            {
                throw new InvalidOperationException(
                    "Prepared Vulkan mesh draw storage was not empty at the start of a scheduled batch.");
            }

            for (int chainIndex = 0; chainIndex < secondaryCount; chainIndex++)
            {
                CommandChain chain = secondaryChains[chainIndex];
                bool needsRecording = secondaryBuffers[chainIndex].Handle == 0;
                if (!needsRecording)
                    continue;

                for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
                {
                    int opIndex = chain.SourceStartIndex + drawIndex;
                    int relativeIndex = opIndex - startIndex;
                    MeshDrawOp drawOp = (MeshDrawOp)recordingState.Ops[opIndex];
                    using var pipelineScope =
                        RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                            drawOp.Context.PipelineInstance);
                    using var plannerScope =
                        EnterFrameOpResourcePlannerReadbackScope(drawOp.Context);
                    if (!drawOp.Draw.Renderer.TryPrepareMeshDrawRecordingState(
                            recordingState.CommandBufferImageSlot,
                            drawOp.Draw,
                            inheritedRenderPass,
                            inheritedDynamicRendering,
                            inheritedDynamicRenderingFormats,
                            passIndex,
                            drawOp.Context.PassMetadata,
                            inheritedDepthStencilReadOnly,
                            drawOp.Context.PipelineInstance?.DebugName ??
                                "<no pipeline>",
                            uniformSlots[relativeIndex],
                            out VulkanPreparedMeshDrawState preparedState,
                            out string preparedStateReason))
                    {
                        _lastReusableFrameDataRefreshFailureReason =
                            $"prepared mesh draw op={opIndex}/{recordingState.Ops.Length}: {preparedStateReason}";
                        return false;
                    }

                    if (!VkPreparedMeshDraw.TryCreateOwned(
                            opIndex,
                            drawOp.Draw,
                            drawOp.Context,
                            uniformSlots[relativeIndex],
                            preparedState,
                            out VkPreparedMeshDraw preparedDraw,
                            out string preparedDrawReason))
                    {
                        VkMeshRenderer.ReturnPreparedMeshDrawStateBuffers(
                            preparedState);
                        _lastReusableFrameDataRefreshFailureReason =
                            $"prepared mesh draw op={opIndex}/{recordingState.Ops.Length}: {preparedDrawReason}";
                        return false;
                    }

                    int preparedIndex;
                    try
                    {
                        preparedIndex = batch.PreparedFrame.SetMeshDraw(
                            relativeIndex,
                            preparedDraw);
                    }
                    catch
                    {
                        preparedDraw.Release();
                        throw;
                    }

                    preparedMeshDrawCount++;
                    drawOp.Draw.Renderer.TryTransitionPreparedDescriptorImagesForSampling(
                        recordingState.CommandBuffer,
                        preparedState,
                        drawOp.Target);

                    if (preparedIndex != relativeIndex)
                    {
                        throw new InvalidOperationException(
                            "Prepared Vulkan mesh draws lost source ordering.");
                    }
                }
            }

            return true;
        }

        internal bool TryExecuteMeshCommandChainSecondaryRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, int runCount, int passIndex, MeshDrawOp firstDraw)
        {
            const int minMeshDrawsPerSecondaryChain = MinMeshDrawsPerRenderPacket;

            if (!CommandChainsEnabledForCurrentRecording ||
                !_enableSecondaryCommandBuffers ||
                runCount < minMeshDrawsPerSecondaryChain ||
                recordingState.ActiveInlineQuery is not null ||
                firstDraw.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
            {
                return false;
            }

            EndActiveRenderPass(ref recordingState);

            if (!TryResolveMeshSecondaryInheritance(ref recordingState,
                    firstDraw.Target,
                    passIndex,
                    firstDraw.Context,
                    out bool inheritedDynamicRendering,
                    out RenderPass inheritedRenderPass,
                    out Framebuffer inheritedFramebuffer,
                    out DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
                    out FrameBufferAttachmentSignature[]? inheritedFboAttachmentSignature,
                    out bool inheritedDepthStencilReadOnly,
                    out SampleCountFlags inheritedSamples,
                    out DynamicRenderingLocalReadSignature inheritedLocalReadSignature,
                    out RenderingFlags inheritedRenderingFlags))
            {
                return false;
            }

            bool meshSecondaryNoOp = IsCommandChainFlagEnabled(XREngineEnvironmentVariables.VulkanCommandChainMeshSecondaryNoop);
            // A recorded primary command buffer bakes the secondary handle it executes.
            // Keep secondary ownership per primary variant so re-recording one variant
            // cannot invalidate another variant that still references its old secondary.
            int primaryOwnedChainOrdinal = HashCode.Combine(startIndex, recordingState.CommandBuffer.Handle);
            CommandChainKey chainKey = new(
                recordingState.CommandBufferImageSlot,
                BuildRenderViewKey(firstDraw, dynamicOverlay: false),
                passIndex,
                ResolveCommandChainTargetIdentity(firstDraw),
                0UL,
                false,
                primaryOwnedChainOrdinal);
            CommandChain chain = GetOrCreateCommandChain(GetCommandChainCache(recordingState.FrameDataImageIndex), chainKey);
            CommandBuffer secondary = chain.SecondaryCommandBuffer;
            bool executedInPrimary = false;
            bool meshLabelActive = false;
            bool secondaryRecordingFinished = false;
            int[]? drawUniformSlots = null;

            if (CanRecordCommandBufferDebugLabels)
                meshLabelActive = CmdBeginLabel(recordingState.CommandBuffer, $"MeshCommandChainSecondary[{runCount}]");

            try
            {
                if (secondary.Handle != 0 && chain.SecondaryCommandPool.Handle == 0)
                {
                    LogCommandChainSecondaryInheritanceMismatch(
                        "mesh",
                        firstDraw.Target,
                        passIndex,
                        $"chain-owned secondary has no owner command pool key={chainKey}");
                    DestroyCommandChainSecondaryCommandBuffer(chain);
                    secondary = default;
                }

                if (!TryEnsureMutableCommandChainSecondaryCommandBuffer(chain, recordingState.FrameDataImageIndex, recordingState.ExecutedCommandChainSecondaryHandles, out secondary))
                    return false;

                drawUniformSlots = ArrayPool<int>.Shared.Rent(runCount);
                for (int i = 0; i < runCount; i++)
                {
                    MeshDrawOp drawOp = (MeshDrawOp)recordingState.Ops[startIndex + i];
                    int drawUniformSlot = GetMeshDrawUniformSlot(ref recordingState,
                        startIndex + i,
                        drawOp.Draw.Renderer,
                        drawOp.Context,
                        drawOp.Draw);
                    drawUniformSlots[i] = drawUniformSlot;

                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                        drawOp.Context.PipelineInstance);
                    using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(drawOp.Context);
                    drawOp.Draw.Renderer.TryTransitionPreparedDescriptorImagesForSampling(
                        recordingState.CommandBuffer,
                        drawOp.Draw,
                        drawUniformSlot,
                        recordingState.CommandBufferImageSlot,
                        drawOp.Target);

                    drawOp.Draw.Renderer.EnsureUniformDrawSlotCapacity(drawUniformSlot + 1);
                }

                MarkCommandChainSecondaryCommandBufferInvalid(chain);
                ResetVulkanCommandBufferTracked(secondary);

                CommandBufferInheritanceInfo inheritanceInfo = new()
                {
                    SType = StructureType.CommandBufferInheritanceInfo,
                    RenderPass = inheritedDynamicRendering ? default : inheritedRenderPass,
                    Subpass = 0,
                    Framebuffer = inheritedDynamicRendering ? default : inheritedFramebuffer,
                    OcclusionQueryEnable = Vk.False,
                    QueryFlags = QueryControlFlags.None,
                    PipelineStatistics = QueryPipelineStatisticFlags.None
                };

                Format* colorAttachmentFormats = stackalloc Format[(int)Math.Max(inheritedDynamicRenderingFormats.ColorAttachmentCount, 1u)];
                CommandBufferInheritanceRenderingInfo renderingInheritanceInfo = default;
                if (inheritedDynamicRendering)
                {
                    inheritedDynamicRenderingFormats.CopyColorAttachmentFormats(
                        colorAttachmentFormats,
                        inheritedDynamicRenderingFormats.ColorAttachmentCount);

                    renderingInheritanceInfo = new CommandBufferInheritanceRenderingInfo
                    {
                        SType = StructureType.CommandBufferInheritanceRenderingInfo,
                        Flags = inheritedRenderingFlags,
                        ViewMask = inheritedDynamicRenderingFormats.ViewMask,
                        ColorAttachmentCount = inheritedDynamicRenderingFormats.ColorAttachmentCount,
                        PColorAttachmentFormats = inheritedDynamicRenderingFormats.ColorAttachmentCount > 0 ? colorAttachmentFormats : null,
                        DepthAttachmentFormat = inheritedDynamicRenderingFormats.DepthAttachmentFormat,
                        StencilAttachmentFormat = inheritedDynamicRenderingFormats.StencilAttachmentFormat,
                        RasterizationSamples = inheritedSamples
                    };
                    RenderingAttachmentLocationInfo localReadAttachmentLocations = default;
                    RenderingInputAttachmentIndexInfo localReadInputIndices = default;
                    uint* colorAttachmentLocations = stackalloc uint[(int)Math.Max(inheritedDynamicRenderingFormats.ColorAttachmentCount, 1u)];
                    uint* colorInputAttachmentIndices = stackalloc uint[(int)Math.Max(inheritedDynamicRenderingFormats.ColorAttachmentCount, 1u)];
                    uint* depthInputAttachmentIndex = stackalloc uint[1];
                    uint* stencilInputAttachmentIndex = stackalloc uint[1];
                    void* localReadInheritancePNext = renderingInheritanceInfo.PNext;
                    TryAppendDynamicRenderingLocalReadInheritancePNext(
                        in inheritedLocalReadSignature,
                        inheritedDynamicRenderingFormats.ColorAttachmentCount,
                        ref localReadInheritancePNext,
                        &localReadAttachmentLocations,
                        &localReadInputIndices,
                        colorAttachmentLocations,
                        colorInputAttachmentIndices,
                        depthInputAttachmentIndex,
                        stencilInputAttachmentIndex);
                    renderingInheritanceInfo.PNext = localReadInheritancePNext;
                    inheritanceInfo.PNext = &renderingInheritanceInfo;
                }

                CommandBufferInheritanceDescriptorHeapInfoEXTNative descriptorHeapInheritanceInfo = default;
                BindHeapInfoEXTNative inheritedSamplerHeapInfo = default;
                BindHeapInfoEXTNative inheritedResourceHeapInfo = default;
                TryAppendDescriptorHeapInheritancePNext(
                    ref inheritanceInfo,
                    &descriptorHeapInheritanceInfo,
                    &inheritedSamplerHeapInfo,
                    &inheritedResourceHeapInfo);

                CommandBufferBeginInfo beginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.OneTimeSubmitBit,
                    PInheritanceInfo = &inheritanceInfo
                };

                if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                    throw new Exception("Failed to begin Vulkan mesh command-chain secondary command buffer.");

                ResetCommandBufferBindState(secondary);
                MarkCommandChainSecondaryRecording(chain, secondary);

                bool savedActiveDynamicRendering = recordingState.RenderScope.UsesDynamicRendering;
                RenderPass savedActiveRenderPass = recordingState.RenderScope.RenderPass;
                Framebuffer savedActiveFramebuffer = recordingState.RenderScope.Framebuffer;
                DynamicRenderingFormatSignature savedActiveDynamicRenderingFormats = recordingState.RenderScope.DynamicRenderingFormats;
                FrameBufferAttachmentSignature[]? savedActiveFboAttachmentSignature = recordingState.RenderScope.AttachmentSignature;
                bool savedActiveDepthStencilReadOnly = recordingState.RenderScope.DepthStencilReadOnly;
                DynamicRenderingLocalReadSignature savedActiveLocalReadSignature =
                    recordingState.RenderScope.LocalReadSignature;
                RenderingFlags savedActiveRenderingFlags =
                    recordingState.RenderScope.InheritanceRenderingFlags;
                XRFrameBuffer? savedActiveTarget = recordingState.RenderScope.Target;

                recordingState.RenderScope.UsesDynamicRendering = inheritedDynamicRendering;
                recordingState.RenderScope.RenderPass = inheritedRenderPass;
                recordingState.RenderScope.Framebuffer = inheritedFramebuffer;
                recordingState.RenderScope.DynamicRenderingFormats = inheritedDynamicRenderingFormats;
                recordingState.RenderScope.AttachmentSignature = inheritedFboAttachmentSignature;
                recordingState.RenderScope.DepthStencilReadOnly = inheritedDepthStencilReadOnly;
                recordingState.RenderScope.LocalReadSignature =
                    inheritedLocalReadSignature;
                recordingState.RenderScope.InheritanceRenderingFlags =
                    inheritedRenderingFlags;
                recordingState.RenderScope.Target = firstDraw.Target;

                try
                {
                    for (int i = startIndex; !meshSecondaryNoOp && i < startIndex + runCount; i++)
                    {
                        MeshDrawOp drawOp = (MeshDrawOp)recordingState.Ops[i];
                        using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(drawOp.Context.PipelineInstance);
                        RecordMeshDrawIntoCommandBuffer(ref recordingState, secondary, drawOp, passIndex, drawUniformSlots[i - startIndex]);
                    }
                }
                finally
                {
                    recordingState.RenderScope.UsesDynamicRendering = savedActiveDynamicRendering;
                    recordingState.RenderScope.RenderPass = savedActiveRenderPass;
                    recordingState.RenderScope.Framebuffer = savedActiveFramebuffer;
                    recordingState.RenderScope.DynamicRenderingFormats = savedActiveDynamicRenderingFormats;
                    recordingState.RenderScope.AttachmentSignature = savedActiveFboAttachmentSignature;
                    recordingState.RenderScope.DepthStencilReadOnly = savedActiveDepthStencilReadOnly;
                    recordingState.RenderScope.LocalReadSignature =
                        savedActiveLocalReadSignature;
                    recordingState.RenderScope.InheritanceRenderingFlags =
                        savedActiveRenderingFlags;
                    recordingState.RenderScope.Target = savedActiveTarget;
                }

                if (EndCommandBufferTracked(secondary) != Result.Success)
                    throw new Exception("Failed to end Vulkan mesh command-chain secondary command buffer.");

                StoreCommandChainSecondaryInheritance(
                    chain,
                    inheritedDynamicRendering,
                    inheritedRenderPass,
                    inheritedFramebuffer,
                    inheritedDynamicRenderingFormats,
                    inheritedDepthStencilReadOnly,
                    inheritedSamples,
                    inheritedLocalReadSignature,
                    inheritedRenderingFlags);
                MarkCommandChainSecondaryCommandBufferRecorded(chain);
                secondaryRecordingFinished = true;
                BeginRenderPassForTarget(ref recordingState, firstDraw.Target, passIndex, firstDraw.Context, secondaryContents: true);
                if (!ActiveMeshSecondaryInheritanceMatches(ref recordingState,
                        "mesh",
                        firstDraw.Target,
                        passIndex,
                        inheritedDynamicRendering,
                        inheritedRenderPass,
                        inheritedFramebuffer,
                        inheritedDynamicRenderingFormats,
                        inheritedDepthStencilReadOnly,
                        inheritedSamples,
                        inheritedLocalReadSignature,
                        inheritedRenderingFlags))
                {
                    MarkCommandChainSecondaryCommandBufferInvalid(
                        chain,
                        EVulkanRecordedCommandArtifactInvalidationReason.InheritanceMismatch);
                    return false;
                }

                CmdExecuteCommandsTracked(recordingState.CommandBuffer, 1, &secondary);
                if (secondary.Handle != 0)
                {
                    recordingState.ExecutedCommandChainSecondaryHandles.Add(secondary.Handle);
                    recordingState.ExecutedCommandChainSecondaryArtifactSequence.Add(chain);
                }
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(secondaryCommandBuffers: 1);
                executedInPrimary = true;
                return true;
            }
            finally
            {
                EndActiveRenderPass(ref recordingState);

                if (!executedInPrimary && !secondaryRecordingFinished)
                    DestroyCommandChainSecondaryCommandBuffer(chain);

                if (drawUniformSlots is not null)
                {
                    Array.Clear(drawUniformSlots, 0, runCount);
                    ArrayPool<int>.Shared.Return(drawUniformSlots);
                }

                if (meshLabelActive)
                    CmdEndLabel(recordingState.CommandBuffer);
            }
        }
    }
}
