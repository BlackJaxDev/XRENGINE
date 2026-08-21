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
    internal sealed partial class VulkanCommandRuntime
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
                bool useDynamicRendering = recordingState.Policy.UseDynamicRendering &&
                    recordingState.SwapchainTarget.IsValid;

                if (useDynamicRendering)
                {
                    inheritedDynamicRendering = true;
                    inheritedDynamicRenderingFormats = CreateSwapchainDynamicRenderingFormatSignature(recordingState.SwapchainTarget.ImageFormat, recordingState.SwapchainTarget.DepthFormat);
                    inheritedDepthStencilReadOnly = false;
                    inheritedSamples = SampleCountFlags.Count1Bit;
                    return true;
                }

                if (recordingState.SwapchainTarget.Framebuffer.Handle == 0)
                {
                    LogCommandChainSecondaryInheritanceMismatch(
                        "mesh",
                        null,
                        passIndex,
                        "legacy swapchain framebuffer is unavailable");
                    return false;
                }

                inheritedRenderPass = (recordingState.SwapchainClearedThisFrame || recordingState.SwapchainWrittenOutsideRenderPass)
                    ? ResourceRuntime.SwapchainLoadRenderPass
                    : ResourceRuntime.SwapchainRenderPass;
                inheritedFramebuffer = recordingState.SwapchainTarget.Framebuffer;
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
            ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(
                target,
                vkFrameBuffer,
                recordingState.CommandBuffer);
            FrameBufferAttachmentSignature[] fboSignature = vkFrameBuffer.ResolveAttachmentSignatureForPass(
                passIndex,
                context.PassMetadata,
                trackedLayouts,
                recordingState.RenderGraphPlan.CompiledGraph.Synchronization,
                preserveTrackedClearLoads: targetReenteredThisCommandBuffer);

            inheritedDepthStencilReadOnly = VkFrameBuffer.UsesReadOnlyDepthStencil(fboSignature);

            if (recordingState.Policy.UseDynamicRendering)
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
                recordingState.RenderGraphPlan.CompiledGraph.Synchronization,
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

        internal unsafe bool TryExecuteIndirectCommandChainSecondaryRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, int runCount, int passIndex)
        {
            ref readonly IndirectDrawPayload firstDraw =
                ref recordingState.Ops.GetIndirectDraw(startIndex);
            XRFrameBuffer? firstTarget = recordingState.Ops.GetTarget(startIndex);
            FrameOpContext firstContext = recordingState.Ops.GetContext(startIndex);
            EVulkanIndirectSecondaryEligibility eligibility =
                _commandRuntime.EvaluateIndirectSecondaryRecordingContract(in firstDraw);
            if (eligibility !=
                EVulkanIndirectSecondaryEligibility.
                    EligibleProducerComplete)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.
                    RecordVulkanIndirectSecondaryEligibility(eligibility);
                return false;
            }

            if (recordingState.CommandChainSchedule is null ||
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
                    firstTarget,
                    passIndex,
                    firstContext,
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

            CommandBufferRecordingScratch batchScratch = recordingState.RecordingScratch;
            batchScratch.EnsureIndirectSecondaryCapacity(runCount);
            CommandBuffer[] secondaryBuffers = batchScratch.IndirectSecondaryBuffers;
            CommandChain[] secondaryChains = batchScratch.IndirectSecondaryChains;
            int[] uniformSlots = batchScratch.IndirectSecondaryUniformSlots;
            VkMeshRenderer.IndirectDrawRecordingState[] recordingStates = batchScratch.IndirectSecondaryRecordingStates;
            bool[] recordingStatePrepared = batchScratch.IndirectSecondaryRecordingStatePrepared;
            Exception? firstError = null;

            bool indirectLabelActive = false;
            if (_deviceContext.CanRecordCommandBufferDebugLabels)
            {
                indirectLabelActive = _deviceContext.CmdBeginLabel(recordingState.CommandBuffer, $"IndirectCommandChainSecondary[{runCount}]");
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
                    int opIndex = startIndex + i;
                    ref readonly IndirectDrawPayload indirect = ref recordingState.Ops.GetIndirectDraw(opIndex);
                    ref readonly FrameOpContext context = ref recordingState.Ops.GetContext(opIndex);
                    uniformSlots[i] = GetMeshDrawUniformSlot(ref recordingState,
                        opIndex,
                        indirect.MeshRenderer,
                        context,
                        indirect.Draw);
                }

                for (int i = 0; i < runCount; i++)
                {
                    ref readonly IndirectDrawPayload indirect =
                        ref recordingState.Ops.GetIndirectDraw(startIndex + i);
                    indirect.MeshRenderer.EnsureUniformDrawSlotCapacity(uniformSlots[i] + 1);
                }

                for (int i = 0; i < runCount; i++)
                {
                    int opIndex = startIndex + i;
                    ref readonly IndirectDrawPayload indirect = ref recordingState.Ops.GetIndirectDraw(opIndex);
                    ref readonly FrameOpContext context = ref recordingState.Ops.GetContext(opIndex);
                    PendingMeshDraw indirectDraw = indirect.Draw;
                    XRFrameBuffer? target = recordingState.Ops.GetTarget(opIndex);
                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(context.PipelineInstance);
                    if (!indirect.MeshRenderer.TryPrepareIndirectDrawRecordingState(
                            recordingState.FrameDataImageIndex,
                            indirect.Draw,
                            inheritedRenderPass,
                            inheritedDynamicRendering,
                            inheritedDynamicRenderingFormats,
                            passIndex,
                            context.PassMetadata,
                            inheritedDepthStencilReadOnly,
                            context.PipelineInstance?.DebugName ?? "<no pipeline>",
                            uniformSlots[i],
                            out recordingStates[i],
                            out string prepareReason))
                    {
                        Debug.VulkanWarningEvery(
                            $"Vulkan.IndirectSecondary.PrepareFailed.{GetHashCode()}.{indirect.MeshRenderer.GetHashCode()}.{prepareReason}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Indirect secondary pre-worker state capture failed. mesh='{0}' target='{1}' slot={2} reason={3}",
                            indirect.MeshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                            target?.Name ?? "<swapchain>",
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

                    // The primary owns image-layout transitions. Do this while
                    // preparation still owns the descriptor publication selected
                    // for this draw, before any secondary render scope begins.
                    indirect.MeshRenderer.TryTransitionPreparedDescriptorImagesForSampling(
                        recordingState.CommandBuffer,
                        indirect.Draw,
                        uniformSlots[i],
                        recordingState.CommandBufferImageSlot,
                        target,
                        recordingState.Ops.GetHeader(opIndex).PassIndex,
                        context.PassMetadata);
                }

                Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(recordingState.FrameDataImageIndex);
                for (int i = 0; i < runCount; i++)
                {
                    int opIndex = startIndex + i;
                    ref readonly IndirectDrawPayload indirect = ref recordingState.Ops.GetIndirectDraw(opIndex);
                    ref readonly FrameOpContext context = ref recordingState.Ops.GetContext(opIndex);
                    PendingMeshDraw indirectDraw = indirect.Draw;
                    // This chain owns a frozen indirect packet, not the transient
                    // primary that happens to execute it this frame. Keep its key
                    // stable across primary re-records so a producer-complete
                    // command/count topology can retain the secondary artifact.
                    int primaryOwnedChainOrdinal = int.MinValue + startIndex + i;
                    CommandChainKey chainKey = new(
                        recordingState.CommandBufferImageSlot,
                        BuildRenderViewKey(in indirectDraw, passIndex, in context, dynamicOverlay: false),
                        passIndex,
                        ResolveCommandChainTargetIdentity(recordingState.Ops.GetTarget(opIndex), in context),
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
                    ref readonly IndirectDrawPayload indirect =
                        ref recordingState.Ops.GetIndirectDraw(startIndex + i);
                    VulkanIndirectSecondaryRecordingContract contract =
                        indirect.SecondaryRecordingContract;
                    CommandChain chain = secondaryChains[i];
                    VulkanPreparedCommandChainKey preparedKey =
                        CapturePreparedIndirectCommandChainKey(
                            chain,
                            recordingStates[i]);
                    if (CanReuseIndirectCommandChainSecondary(
                            chain,
                            in contract,
                            uniformSlots[i],
                            recordingState.Policy.FreshSerialRecording,
                            in preparedKey,
                            in secondaryInheritance))
                    {
                        chain.State = CommandChainState.Reused;
                        chain.DirtyReason = CommandChainDirtyReason.None;
                        continue;
                    }

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

                BeginRenderPassForTarget(ref recordingState, firstTarget, passIndex, firstContext, secondaryContents: true);
                if (!ActiveMeshSecondaryInheritanceMatches(ref recordingState,
                        "indirect-mesh",
                        firstTarget,
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
                Array.Clear(recordingStates, 0, runCount);
                Array.Clear(recordingStatePrepared, 0, runCount);
                if (indirectLabelActive)
                    _deviceContext.CmdEndLabel(recordingState.CommandBuffer);
            }
        }

        private unsafe Exception? RecordIndirectCommandChainSecondary(
            FrameOperationSequence ops,
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

                uint colorAttachmentCount =
                    inheritance.DynamicRenderingFormats.ColorAttachmentCount;
                int attachmentScratchCount =
                    checked((int)Math.Max(colorAttachmentCount, 1u));
                VulkanSynchronizationThreadState nativeScratch =
                    Synchronization._synchronizationThreadWorkspace.Current;
                using VulkanNativeScratchReservation<Format> formatReservation =
                    nativeScratch.FormatScratch.Reserve(attachmentScratchCount);
                using VulkanNativeScratchReservation<uint> locationReservation =
                    nativeScratch.AttachmentLocationScratch.Reserve(attachmentScratchCount);
                using VulkanNativeScratchReservation<uint> inputIndexReservation =
                    nativeScratch.InputAttachmentIndexScratch.Reserve(attachmentScratchCount);
                Span<Format> colorAttachmentFormatSpan = formatReservation.Span;
                Span<uint> colorAttachmentLocationSpan = locationReservation.Span;
                Span<uint> colorInputAttachmentIndexSpan = inputIndexReservation.Span;
                fixed (Format* colorAttachmentFormats = colorAttachmentFormatSpan)
                fixed (uint* colorAttachmentLocations = colorAttachmentLocationSpan)
                fixed (uint* colorInputAttachmentIndices = colorInputAttachmentIndexSpan)
                {
                CommandBufferInheritanceRenderingInfo renderingInheritanceInfo = default;
                if (inheritance.DynamicRendering)
                {
                    inheritance.DynamicRenderingFormats.CopyColorAttachmentFormats(
                        colorAttachmentFormats,
                        colorAttachmentCount);

                    renderingInheritanceInfo = new CommandBufferInheritanceRenderingInfo
                    {
                        SType = StructureType.CommandBufferInheritanceRenderingInfo,
                        Flags = inheritance.RenderingFlags,
                        ViewMask = inheritance.DynamicRenderingFormats.ViewMask,
                        ColorAttachmentCount = colorAttachmentCount,
                        PColorAttachmentFormats = colorAttachmentCount > 0
                            ? colorAttachmentFormats
                            : null,
                        DepthAttachmentFormat = inheritance.DynamicRenderingFormats.DepthAttachmentFormat,
                        StencilAttachmentFormat = inheritance.DynamicRenderingFormats.StencilAttachmentFormat,
                        RasterizationSamples = inheritance.Samples
                    };
                    RenderingAttachmentLocationInfo localReadAttachmentLocations = default;
                    RenderingInputAttachmentIndexInfo localReadInputIndices = default;
                    uint* depthInputAttachmentIndex = stackalloc uint[1];
                    uint* stencilInputAttachmentIndex = stackalloc uint[1];
                    void* localReadInheritancePNext = renderingInheritanceInfo.PNext;
                    DynamicRenderingLocalReadSignature localReadSignature =
                        inheritance.LocalReadSignature;
                    TryAppendDynamicRenderingLocalReadInheritancePNext(
                        in localReadSignature,
                        colorAttachmentCount,
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

                ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.PrimarySecondaryRange");
                if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                    throw new Exception("Failed to begin Vulkan indirect secondary command buffer.");
                }

                ResetCommandBufferBindState(secondary);
                MarkCommandChainSecondaryRecording(chain, secondary);

                int opIndex = startIndex + relativeIndex;
                ref readonly IndirectDrawPayload indirect = ref ops.GetIndirectDraw(opIndex);
                ref readonly FrameOpContext context = ref ops.GetContext(opIndex);
                XRFrameBuffer? target = ops.GetTarget(opIndex);
                using (RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                           context.PipelineInstance))
                {
                    RecordIndirectDrawIntoSecondaryCommandBuffer(
                        secondary,
                        in indirect,
                        target,
                        in context,
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
                chain.RecordedIndirectSecondaryContract =
                    indirect.SecondaryRecordingContract;
                chain.PreparedKey = CapturePreparedIndirectCommandChainKey(
                    chain,
                    recordingStates[relativeIndex]);
                chain.RecordedUniformSlotSignature =
                    unchecked((ulong)(uint)uniformSlots[relativeIndex]);
                return null;
            }
            catch (Exception ex)
            {
                DestroyCommandChainSecondaryCommandBuffer(chain);
                secondaryBuffers[relativeIndex] = default;
                return ex;
            }
        }

        private bool CanReuseIndirectCommandChainSecondary(
            CommandChain chain,
            in VulkanIndirectSecondaryRecordingContract contract,
            int uniformSlot,
            bool freshSerialRecording,
            scoped in VulkanPreparedCommandChainKey preparedKey,
            scoped in VulkanRecordedCommandInheritance inheritance)
            => !freshSerialRecording &&
                chain.SecondaryCommandBufferExecutable &&
                preparedKey.IsComplete &&
                chain.PreparedKey.IsComplete &&
                chain.PreparedKey.Matches(in preparedKey) &&
                chain.RecordedIndirectSecondaryContract ==
                    contract &&
                chain.RecordedUniformSlotSignature == unchecked((ulong)(uint)uniformSlot) &&
                CommandChainSecondaryInheritanceMatches(
                    chain,
                    inheritance.DynamicRendering,
                    inheritance.RenderPass,
                    inheritance.Framebuffer,
                    inheritance.DynamicRenderingFormats,
                    inheritance.DepthStencilReadOnly,
                    inheritance.Samples,
                    inheritance.LocalReadSignature,
                    inheritance.RenderingFlags);

        /// <summary>
        /// Captures exactly the pipeline, layout, and published descriptor sets
        /// chosen during indirect-draw preparation. Reuse must not substitute
        /// live renderer state after the primary has transitioned those images.
        /// </summary>
        private VulkanPreparedCommandChainKey CapturePreparedIndirectCommandChainKey(
            CommandChain chain,
            in VkMeshRenderer.IndirectDrawRecordingState state)
        {
            if (state.Program is null ||
                state.Program.BindingId == 0u ||
                state.Program.LinkGeneration == 0UL ||
                state.Pipeline.Handle == 0UL ||
                state.PipelineLayout.Handle == 0UL)
            {
                return VulkanPreparedCommandChainKey.Incomplete;
            }

            ulong pipelineGeneration = GetCurrentVulkanResourceGeneration(
                ObjectType.Pipeline,
                state.Pipeline.Handle);
            ulong layoutGeneration = GetCurrentVulkanResourceGeneration(
                ObjectType.PipelineLayout,
                state.PipelineLayout.Handle);
            if (pipelineGeneration == 0UL || layoutGeneration == 0UL)
                return VulkanPreparedCommandChainKey.Incomplete;

            FrameOpSignatureHasher pipelineHash = new();
            pipelineHash.Add(state.Program.BindingId);
            pipelineHash.Add(state.Program.LinkGeneration);
            pipelineHash.Add(state.Pipeline.Handle);
            pipelineHash.Add(pipelineGeneration);
            pipelineHash.Add(state.PipelineLayout.Handle);
            pipelineHash.Add(layoutGeneration);

            DescriptorSet[]? descriptorSets = state.DescriptorSets;
            int descriptorSetCount = descriptorSets?.Length ?? 0;
            VulkanRecordedDescriptorSetIdentityBuffer exactDescriptorSets =
                CaptureRecordedDescriptorSetIdentities(descriptorSets, null);
            if (!exactDescriptorSets.IsComplete)
                return VulkanPreparedCommandChainKey.Incomplete;

            // Heap push payloads do not name stable native descriptor sets.
            if (state.DescriptorHeapPushData is not null)
                return VulkanPreparedCommandChainKey.Incomplete;

            VulkanRecordedProgramIdentityBuffer exactPrograms = default;
            exactPrograms.Initialize(1);
            exactPrograms.Set(
                0,
                new VulkanRecordedProgramIdentity(
                    state.Program.BindingId,
                    state.Program.LinkGeneration,
                    state.PipelineLayout.Handle,
                    layoutGeneration,
                    state.Pipeline.Handle,
                    pipelineGeneration));
            RecordedPacketKey preparedPacketKey =
                chain.DependencySignature.RecordedPacketKey with
                {
                    DescriptorSets = exactDescriptorSets,
                    Programs = exactPrograms,
                };

            return new VulkanPreparedCommandChainKey(
                pipelineHash.ToHash(),
                ComputeRecordedDescriptorSetIdentityHash(exactDescriptorSets),
                descriptorSetCount,
                preparedPacketKey,
                IsComplete: preparedPacketKey.IsComplete);
        }

        private bool TryGetScheduledCommandChainForOp(scoped ref PrimaryCommandBufferRecordingState recordingState, int opIndex, out CommandChain chain, out CommandChainKey key)
        {
            chain = null!;
            key = default;
            if (recordingState.ScheduledCommandChainKeysByOpIndex is null ||
                recordingState.ScheduledCommandChainsByOpIndex is null ||
                (uint)opIndex >= (uint)recordingState.Ops.Length)
            {
                return false;
            }

            key = recordingState.ScheduledCommandChainKeysByOpIndex[opIndex];
            if (key.ChainOrdinal == -1)
                return false;

            CommandChain? scheduledChain = recordingState.ScheduledCommandChainsByOpIndex[opIndex];
            if (scheduledChain is null)
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

        internal unsafe bool TryExecuteScheduledMeshCommandChainSecondaryRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, int runCount, int passIndex)
        {
            using VulkanCpuStageScope scheduledRunStage =
                new(_frameTelemetry, EVulkanCpuStage.ScheduledSecondaryRun);
            using var scheduledRunProfileScope =
                RuntimeRenderingHostServices.Profiling.StartProfileScope(
                    "Vulkan.RecordPrimary.ScheduledSecondaryRun");
            XRFrameBuffer? firstTarget = recordingState.Ops.GetTarget(startIndex);
            FrameOpContext firstContext = recordingState.Ops.GetContext(startIndex);
            // An explicit schedule may be supplied for an external OpenXR
            // target even though the ordinary desktop eligibility gate is
            // false inside that render scope. The builder has already
            // applied the appropriate policy for this recording.
            if (!_enableSecondaryCommandBuffers ||
                recordingState.ScheduledCommandChainKeysByOpIndex is null ||
                recordingState.ScheduledCommandChainCache is null ||
                runCount <= 0 ||
                recordingState.ActiveInlineQuery is not null ||
                firstContext.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
            {
                return false;
            }

            // Query-bracket contents are intentionally absent from the command-chain
            // schedule. Preflight the complete run before closing the current render
            // scope so an unscheduled draw cannot terminate rendering as a side effect.
            using (VulkanCpuStageScope preparationStage =
                   new(_frameTelemetry, EVulkanCpuStage.ScheduledSecondaryPreflight))
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.RecordPrimary.ScheduledSecondaryRun.Preflight"))
            {
                for (int i = 0; i < runCount; i++)
                {
                    int opIndex = startIndex + i;
                    if (recordingState.Ops.GetHeader(opIndex).OpCode != EVulkanPrimaryPlanNodeKind.MeshDraw ||
                        recordingState.Ops.GetHeader(opIndex).PassIndex != passIndex ||
                        recordingState.Ops.GetTarget(opIndex) != firstTarget ||
                        !TryGetScheduledCommandChainForOp(ref recordingState, opIndex, out _, out _))
                    {
                        return false;
                    }
                }
            }

            EndActiveRenderPass(ref recordingState);

            if (!TryResolveMeshSecondaryInheritance(ref recordingState,
                    firstTarget,
                    passIndex,
                    firstContext,
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

            VulkanCommandChainRecordingBatch batch = _commandChainRecordingBatch;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.RecordPrimary.ScheduledSecondaryRun.BatchSetup"))
            {
                batch.EnsureCapacity(runCount, runCount);
                FramePlan? framePlan = recordingState.FramePlan;
                batch.PreparedFrame.Begin(
                    framePlan?.FrameSlot ?? recordingState.CommandBufferImageSlot,
                    VulkanFrameCounter);
                if (framePlan is not null)
                    batch.PreparedFrame.AttachFramePlan(framePlan);
                batch.PreparedFrame.AddPrimaryPlan(recordingState.PrimaryCommandPlan);
            }
            VulkanCommandChainRecordingEntry[] entries = batch.Entries;
            VulkanCommandChainRecordingDraw[] draws = batch.Draws;
            CommandBuffer[] executionBuffers = batch.ExecutionBuffers;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.RecordPrimary.ScheduledSecondaryRun.ClearScratch"))
            {
                Array.Clear(entries, 0, runCount);
                Array.Clear(draws, 0, runCount);
                Array.Clear(executionBuffers, 0, runCount);
            }
            int secondaryCount = 0;
            int scheduledOpCount = 0;
            int recordJobCount = 0;
            int recordOperationCount = 0;
            int deferredRecordJobCount = 0;
            bool meshLabelActive = false;

            if (_deviceContext.CanRecordCommandBufferDebugLabels)
                meshLabelActive = _deviceContext.CmdBeginLabel(recordingState.CommandBuffer, "ScheduledMeshCommandChainSecondary");

            try
            {
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.RecordPrimary.ScheduledSecondaryRun.UniformSlots"))
                {
                    if (!TryCollectScheduledMeshCommandChainUniformSlots(
                            ref recordingState,
                            startIndex,
                            runCount,
                            passIndex,
                            draws))
                    {
                        return false;
                    }
                }

                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.RecordPrimary.ScheduledSecondaryRun.CollectChains"))
                {
                    for (int i = 0; i < runCount; i++)
                    {
                        int opIndex = startIndex + i;
                        _ = TryGetScheduledCommandChainForOp(
                            ref recordingState,
                            opIndex,
                            out CommandChain chain,
                            out _);
                        if (opIndex != chain.SourceStartIndex)
                            continue;

                        if (chain.SourceCount > runCount - i)
                            return false;

                        ref VulkanCommandChainRecordingEntry entry = ref entries[secondaryCount++];
                        entry.ColdDataIndex = batch.AddCommandChainColdData(chain);
                        entry.PreparedChainIndex = -1;
                        entry.WorkerIndex = -1;
                    }
                }

                if (secondaryCount == 0)
                    return false;

                int preparedMeshDrawCount = 0;
                using (VulkanCpuStageScope classificationStage =
                       new(_frameTelemetry, EVulkanCpuStage.ScheduledSecondaryClassification))
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.RecordPrimary.ScheduledSecondaryRun.ClassifyAndRefresh"))
                {
                    for (int chainIndex = 0; chainIndex < secondaryCount; chainIndex++)
                    {
                        CommandChain chain = batch.GetCommandChainColdData(entries[chainIndex].ColdDataIndex);

                    ulong currentUniformSlotSignature = ComputeCommandChainUniformSlotSignature(
                        draws,
                        chain.SourceStartIndex - startIndex,
                        chain.SourceCount);
                    bool uniformSlotMappingChanged =
                        chain.RecordedUniformSlotSignature != currentUniformSlotSignature;
                    bool benchmarkForcedRerecord =
                        CommandChainBenchmarkForceRerecord;
                    bool workerOwnedSecondaryRequiresMigration =
                        CommandChainWorkerRecordingQuarantined &&
                        chain.RecordedArtifact.WorkerArenaOwner is not null;
                    bool secondaryNeedsRecording =
                        workerOwnedSecondaryRequiresMigration ||
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
                    if (workerOwnedSecondaryRequiresMigration)
                        chain.DirtyReason |= CommandChainDirtyReason.SecondaryCommandBufferInvalid;
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

                            ref readonly MeshDrawPayload refreshDraw =
                                ref recordingState.Ops.GetMeshDraw(refreshOpIndex);
                            int refreshUniformSlot =
                                draws[refreshOpIndex - startIndex].UniformSlot;
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

                    if (!needsRecording)
                    {
                        VulkanPreparedCommandChainAuthority? authority =
                            chain.PreparedAuthority;
                        if (authority is null)
                        {
                            needsRecording = true;
                        }
                        else
                        {
                            ref readonly VulkanPreparedCommandChainKey chainKey =
                                ref chain.PreparedKeyReference;
                            ref readonly VulkanPreparedCommandChainKey authorityKey =
                                ref authority.PreparedKey;
                            needsRecording =
                                !chainKey.IsComplete ||
                                !chainKey.Matches(in authorityKey);
                        }
                    }

                    if (needsRecording)
                    {
                        if (chain.State is CommandChainState.Reused or
                            CommandChainState.FrameDataRefreshed)
                        {
                            chain.State = CommandChainState.Recorded;
                            chain.DirtyReason |= CommandChainDirtyReason.ResourcePlan;
                        }

                        bool admitted =
                            recordingState
                                .CommandChainRecordingAdmittedByOpIndex[
                                    chain.SourceStartIndex];
                        if (admitted &&
                            recordingState.CanProgressivelyDeferCommandChainPublication &&
                            (recordJobCount >=
                                MaxProgressiveDesktopCommandChainRecordJobs ||
                             (recordJobCount > 0 &&
                              recordOperationCount + chain.SourceCount >
                                MaxProgressiveDesktopCommandChainRecordOperations)))
                        {
                            admitted = false;
                            int chainEndIndex = Math.Min(
                                recordingState.Ops.Length,
                                chain.SourceStartIndex + chain.SourceCount);
                            for (int chainOpIndex = chain.SourceStartIndex;
                                 chainOpIndex < chainEndIndex;
                                 chainOpIndex++)
                            {
                                recordingState.CommandChainRecordingAdmittedByOpIndex[
                                    chainOpIndex] = false;
                            }
                        }
                        entries[chainIndex].NeedsRecording = admitted;
                        if (admitted)
                        {
                            recordJobCount++;
                            recordOperationCount += chain.SourceCount;
                        }
                        else
                            deferredRecordJobCount++;
                    }
                    else
                    {
                        chain.WorkerEligibility =
                            EVulkanCommandChainWorkerEligibility.NotEvaluated;
                        CommandBuffer reusable = chain.SecondaryCommandBuffer;
                        if (reusable.Handle == 0 || !chain.SecondaryCommandBufferExecutable)
                            return false;
                        entries[chainIndex].SecondaryBuffer = reusable;
                    }

                        scheduledOpCount += chain.SourceCount;
                    }
                }

                if (secondaryCount == 0 || scheduledOpCount != runCount)
                    return false;

                batch.EntryCount = secondaryCount;
                batch.DrawCount = runCount;

                if (recordJobCount == 0 && deferredRecordJobCount > 0)
                {
                    recordingState.ProgressiveCommandChainDeferredJobs =
                        Math.Max(
                            recordingState.ProgressiveCommandChainDeferredJobs,
                            deferredRecordJobCount);
                    DeferProgressiveCommandChainPublication(
                        ref recordingState,
                        recordedJobs: 0,
                        recordedOperations: 0,
                        deferredRecordJobCount);
                    return true;
                }

                if (recordJobCount == 0)
                {
                    // Every secondary matched its post-preparation key. The
                    // prepared payload remains owned by this batch until the
                    // merge completes, but no worker encoder is needed.
                    using VulkanCpuStageScope mergeStage =
                        new(_frameTelemetry, EVulkanCpuStage.SecondaryMerge);
                    long mergeStart = Stopwatch.GetTimestamp();
                    for (int i = 0; i < secondaryCount; i++)
                    {
                        CommandChain chain = batch.GetCommandChainColdData(entries[i].ColdDataIndex);
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

                    PopulateScheduledMeshCommandChainExecutionBuffers(entries, executionBuffers, secondaryCount);
                    if (!HaveCurrentSecondaryDescriptorPayloadRequirements(
                            executionBuffers,
                            secondaryCount,
                            out int invalidDescriptorPayloadIndex))
                    {
                        MarkCommandChainSecondaryCommandBufferInvalid(
                            batch.GetCommandChainColdData(
                                entries[invalidDescriptorPayloadIndex].ColdDataIndex));
                        return false;
                    }
                    TransitionSecondaryDescriptorImagesForExecution(
                        recordingState.CommandBuffer,
                        executionBuffers,
                        secondaryCount);

                    BeginRenderPassForTarget(
                        ref recordingState,
                        firstTarget,
                        passIndex,
                        firstContext,
                        secondaryContents: true);
                    if (!ActiveMeshSecondaryInheritanceMatches(
                            ref recordingState,
                            "scheduled-mesh-reuse",
                            firstTarget,
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
                                batch.GetCommandChainColdData(entries[i].ColdDataIndex),
                                EVulkanRecordedCommandArtifactInvalidationReason
                                    .InheritanceMismatch);
                        }

                        return false;
                    }

                    fixed (CommandBuffer* secondaryPtr = executionBuffers)
                        CmdExecuteCommandsTracked(
                            recordingState.CommandBuffer,
                            (uint)secondaryCount,
                            secondaryPtr);
                    for (int i = 0; i < secondaryCount; i++)
                    {
                        if (executionBuffers[i].Handle != 0)
                        {
                            recordingState.ExecutedCommandChainSecondaryHandles
                                .Add(executionBuffers[i].Handle);
                            recordingState.ExecutedCommandChainSecondaryArtifactSequence
                                .Add(batch.GetCommandChainColdData(entries[i].ColdDataIndex));
                        }
                    }

                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanCommandChainMetrics(
                            secondaryCommandBuffers: secondaryCount);
                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanCommandChainWorkerMetrics(
                            reusedChains: secondaryCount,
                            mergeTime: Stopwatch.GetElapsedTime(mergeStart));
                    batch.ExecutionMergeElapsedTicks = Stopwatch.GetTimestamp() - mergeStart;
                    batch.ExecutionMergeBytes = secondaryCount * VulkanCommandChainRecordingEntry.SizeInBytes;
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerLayoutTelemetry(
                        batch.QueueDepth, batch.QueueBytes, batch.QueueHighWaterDepth,
                        batch.QueueHighWaterBytes, batch.LocalMergeBytes,
                        batch.LocalMergeElapsedTicks, batch.ExecutionMergeBytes,
                        batch.ExecutionMergeElapsedTicks);
                    return true;
                }

                // The schedule and the published prepared authority already
                // prove the exact native state of clean executable chains.
                // Construct draw payloads only for chains that will actually be
                // re-recorded; reused ranges remain reserved but unmaterialized.
                using (VulkanCpuStageScope preparedDrawStage =
                       new(_frameTelemetry, EVulkanCpuStage.PreparedDrawConstruction))
                {
                    if (!TryPrepareScheduledMeshCommandChainDraws(
                            ref recordingState,
                            startIndex,
                            passIndex,
                            batch,
                            entries,
                            secondaryCount,
                            draws,
                            inheritedRenderPass,
                            inheritedDynamicRendering,
                            inheritedDynamicRenderingFormats,
                            inheritedDepthStencilReadOnly,
                            out preparedMeshDrawCount))
                    {
                        LogScheduledMeshCommandChainPreparationFallback(
                            _lastReusableFrameDataRefreshFailureReason ??
                            "prepared mesh draw construction failed");
                        return false;
                    }
                }

                for (int entryIndex = 0; entryIndex < secondaryCount; entryIndex++)
                {
                    ref VulkanCommandChainRecordingEntry entry = ref entries[entryIndex];
                    if (!entry.NeedsRecording)
                        continue;
                    CommandChain chain = batch.GetCommandChainColdData(entry.ColdDataIndex);
                    VulkanPreparedCommandChainKey preparedKey =
                        CapturePreparedMeshCommandChainKey(
                            batch.PreparedFrame,
                            chain,
                            startIndex,
                            out string preparedKeyFailureReason);
                    if (!preparedKey.IsComplete)
                    {
                        // Pipeline compilation and descriptor publication may
                        // still be converging during startup. The primary path
                        // can encode that draw once ready; an incomplete native
                        // key must never enter the reusable-secondary authority.
                        LogScheduledMeshCommandChainPreparationFallback(
                            preparedKeyFailureReason);
                        return false;
                    }

                    chain.SetPreparedKey(in preparedKey);
                }

                batch.StartIndex = startIndex;
                batch.JobCount = recordJobCount;
                batch.PublishQueueTelemetry(recordJobCount);
                batch.LocalMergeElapsedTicks = 0;
                batch.LocalMergeBytes = 0;
                batch.ExecutionMergeElapsedTicks = 0;
                batch.ExecutionMergeBytes = 0;
                batch.ActiveWorkerMask = 0;
                batch.Error = null;

                int workerEligibleJobCount = 0;
                int workerEligibleOperationCount = 0;
                for (int entryIndex = 0; entryIndex < secondaryCount; entryIndex++)
                {
                    ref VulkanCommandChainRecordingEntry entry = ref entries[entryIndex];
                    if (!entry.NeedsRecording)
                        continue;
                    CommandChain chain = batch.GetCommandChainColdData(entry.ColdDataIndex);
                    if (EvaluatePreparedCommandChainWorkerEncodability(batch, chain) ==
                        EVulkanCommandChainWorkerEligibility.Eligible)
                    {
                        workerEligibleJobCount++;
                        long operationCount =
                            (long)workerEligibleOperationCount + chain.SourceCount;
                        workerEligibleOperationCount = operationCount >= int.MaxValue
                            ? int.MaxValue
                            : (int)operationCount;
                    }
                }

                EVulkanCommandChainWorkerEligibility workerDomainEligibility =
                    PrepareCommandChainRecordingWorkers(
                    workerEligibleJobCount,
                    workerEligibleOperationCount,
                    true,
                    forceSerial: CommandChainWorkerRecordingQuarantined,
                    recordingState.FrameDataImageIndex,
                    out CommandChainRecordingWorkerState[] workers,
                    out int workerCount,
                    out int workerFrameSlot);
                bool useWorkers =
                    workerDomainEligibility ==
                    EVulkanCommandChainWorkerEligibility.Eligible;
                int schedulingConflictCount = 0;
                for (int entryIndex = 0; entryIndex < secondaryCount; entryIndex++)
                {
                    ref VulkanCommandChainRecordingEntry entry = ref entries[entryIndex];
                    if (!entry.NeedsRecording)
                        continue;
                    CommandChain chain = batch.GetCommandChainColdData(entry.ColdDataIndex);
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
                    entry.WorkerIndex = recordingWorkerIndex;
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

                    entry.SecondaryBuffer = secondary;
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
                for (int chainIndex = 0; chainIndex < secondaryCount; chainIndex++)
                {
                    ref VulkanCommandChainRecordingEntry entry =
                        ref entries[chainIndex];
                    if (!entry.NeedsRecording)
                        continue;

                    CommandChain chain =
                        batch.GetCommandChainColdData(entry.ColdDataIndex);
                    RenderPacket? packet = chain.PacketSnapshot;
                    if (packet is null)
                    {
                        throw new VulkanPlanPreconditionException(
                            "Scheduled mesh command chain has no sealed packet snapshot.");
                    }
                    int preparedDrawStartIndex =
                        chain.SourceStartIndex - startIndex;
                    int packetIndex = batch.PreparedFrame.RetainPacket(packet);
                    VulkanPreparedCommandChainKey chainPreparedKey = chain.PreparedKey;
                    VulkanPreparedCommandChainAuthority authority =
                        chain.PreparedAuthority is { } publishedAuthority &&
                        publishedAuthority.PreparedKey.Matches(in chainPreparedKey)
                            ? publishedAuthority
                            : new VulkanPreparedCommandChainAuthority(chain.PreparedKey);
                    entry.PreparedChainIndex =
                        batch.PreparedFrame.AddCommandChain(
                            new VulkanPreparedCommandChain(
                                chain.Key,
                                chain.SourceStartIndex,
                                chain.SourceCount,
                                preparedDrawStartIndex,
                                packetIndex,
                                batch.PreparedFrame.Generation,
                                authority,
                                preparedInheritance,
                                chain.RecordedArtifact.CreateReference(),
                                chain.WorkerEligibility));
                }

                batch.PreparedFrame.Freeze();
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPreparedMeshDraws(
                    preparedMeshDrawCount);

                // Both parallel jobs and serial conflict fallbacks consume the
                // same frozen recording services. Keep this context alive until
                // every job has finished; resetting it in the parallel wait path
                // made any serial fallback fail deterministically.
                batch.PreparedWorkerContext.Prepare(VulkanFrameCounter);

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

                using (VulkanCpuStageScope cpuStage = new(_frameTelemetry, EVulkanCpuStage.SecondaryRecording))
                {
                    for (int entryIndex = 0; entryIndex < secondaryCount; entryIndex++)
                    {
                        ref VulkanCommandChainRecordingEntry entry = ref entries[entryIndex];
                        if (entry.NeedsRecording && entry.WorkerIndex < 0)
                        {
                            _commandRuntime.RecordPreparedMeshCommandChain(batch, entryIndex);
                            serialRecordedCount++;
                        }
                    }
                }
                batch.PreparedWorkerContext.Reset();

                if (deferredRecordJobCount > 0)
                {
                    DeferProgressiveCommandChainPublication(
                        ref recordingState,
                        recordJobCount,
                        recordOperationCount,
                        deferredRecordJobCount);
                    return true;
                }

                using (VulkanCpuStageScope mergeStage =
                       new(_frameTelemetry, EVulkanCpuStage.SecondaryMerge))
                {
                    long mergeStart = Stopwatch.GetTimestamp();
                    for (int i = 0; i < secondaryCount; i++)
                    {
                        CommandChain chain = batch.GetCommandChainColdData(entries[i].ColdDataIndex);
                        bool inheritanceMatches =
                            CommandChainSecondaryInheritanceMatches(
                                chain,
                                inheritedDynamicRendering,
                                inheritedRenderPass,
                                inheritedFramebuffer,
                                inheritedDynamicRenderingFormats,
                                inheritedDepthStencilReadOnly,
                                inheritedSamples,
                                inheritedLocalReadSignature,
                                inheritedRenderingFlags);
                        if (inheritanceMatches)
                            continue;

                        MarkCommandChainSecondaryCommandBufferInvalid(chain);
                        LogCommandChainSecondaryInheritanceMismatch(
                            "scheduled-mesh",
                            firstTarget,
                            passIndex,
                            $"secondary command buffer 0x{entries[i].SecondaryBuffer.Handle:X} did not publish the resolved inheritance before execution");
                        return false;
                    }

                    PopulateScheduledMeshCommandChainExecutionBuffers(entries, executionBuffers, secondaryCount);
                    if (!HaveCurrentSecondaryDescriptorPayloadRequirements(
                            executionBuffers,
                            secondaryCount,
                            out int invalidDescriptorPayloadIndex))
                    {
                        MarkCommandChainSecondaryCommandBufferInvalid(
                            batch.GetCommandChainColdData(
                                entries[invalidDescriptorPayloadIndex].ColdDataIndex));
                        LogCommandChainSecondaryInheritanceMismatch(
                            "scheduled-mesh",
                            firstTarget,
                            passIndex,
                            $"secondary command buffer 0x{executionBuffers[invalidDescriptorPayloadIndex].Handle:X} did not publish current descriptor-payload image requirements before execution");
                        return false;
                    }
                    batch.ExecutionMergeBytes = secondaryCount * VulkanCommandChainRecordingEntry.SizeInBytes;
                    TransitionSecondaryDescriptorImagesForExecution(
                        recordingState.CommandBuffer,
                        executionBuffers,
                        secondaryCount);

                    BeginRenderPassForTarget(ref recordingState, firstTarget, passIndex, firstContext, secondaryContents: true);
                    if (!ActiveMeshSecondaryInheritanceMatches(ref recordingState,
                        "scheduled-mesh",
                        firstTarget,
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
                                batch.GetCommandChainColdData(entries[i].ColdDataIndex),
                                EVulkanRecordedCommandArtifactInvalidationReason.InheritanceMismatch);

                        return false;
                    }

                    fixed (CommandBuffer* secondaryPtr = executionBuffers)
                        CmdExecuteCommandsTracked(recordingState.CommandBuffer, (uint)secondaryCount, secondaryPtr);
                    for (int i = 0; i < secondaryCount; i++)
                    {
                        if (executionBuffers[i].Handle != 0)
                        {
                            recordingState.ExecutedCommandChainSecondaryHandles.Add(executionBuffers[i].Handle);
                            recordingState.ExecutedCommandChainSecondaryArtifactSequence.Add(batch.GetCommandChainColdData(entries[i].ColdDataIndex));
                        }
                    }

                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(secondaryCommandBuffers: secondaryCount);
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerMetrics(
                        seriallyRecordedChains: serialRecordedCount,
                        reusedChains: secondaryCount - recordJobCount,
                        conflictChains: conflictCount,
                        mergeTime: Stopwatch.GetElapsedTime(mergeStart));
                    batch.ExecutionMergeElapsedTicks = Stopwatch.GetTimestamp() - mergeStart;
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerLayoutTelemetry(
                        batch.QueueDepth, batch.QueueBytes, batch.QueueHighWaterDepth,
                        batch.QueueHighWaterBytes, batch.LocalMergeBytes,
                        batch.LocalMergeElapsedTicks, batch.ExecutionMergeBytes,
                        batch.ExecutionMergeElapsedTicks);
                }
                return true;
            }
            finally
            {
                EndActiveRenderPass(ref recordingState);

                if (!batch.Abandoned)
                    batch.ClearReferences();

                if (meshLabelActive)
                    _deviceContext.CmdEndLabel(recordingState.CommandBuffer);
            }
        }

        private void DeferProgressiveCommandChainPublication(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            int recordedJobs,
            int recordedOperations,
            int deferredJobs)
        {
            recordingState.ProgressiveCommandChainPublicationPending = true;
            recordingState.CommandChainPublicationDeferred = true;
            recordingState.ProgressiveCommandChainAdmittedJobs = recordedJobs;
            recordingState.ProgressiveCommandChainAdmittedOperations =
                recordedOperations;
            recordingState.ProgressiveCommandChainDeferredJobs = deferredJobs;
            recordingState.RecordingDeferredReason =
                $"progressive command-chain publication recorded {recordedJobs} cold artifacts ({recordedOperations} operations) and deferred {deferredJobs}; presenting the last complete scene";
            Debug.VulkanEvery(
                $"Vulkan.CommandChains.ProgressivePublication.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.CommandChains] Progressive desktop publication recorded={0} operations={1} deferred={2}; the current frame will replay the last complete scene.",
                recordedJobs,
                recordedOperations,
                deferredJobs);
        }

        private bool TryCollectScheduledMeshCommandChainUniformSlots(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            int startIndex,
            int runCount,
            int passIndex,
            VulkanCommandChainRecordingDraw[] draws)
        {
            XRFrameBuffer? firstTarget = recordingState.Ops.GetTarget(startIndex);
            for (int relativeIndex = 0; relativeIndex < runCount; relativeIndex++)
            {
                int opIndex = startIndex + relativeIndex;
                if (recordingState.Ops.GetHeader(opIndex).OpCode != EVulkanPrimaryPlanNodeKind.MeshDraw ||
                    recordingState.Ops.GetHeader(opIndex).PassIndex != passIndex ||
                    recordingState.Ops.GetTarget(opIndex) != firstTarget ||
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
                    ref readonly MeshDrawPayload draw = ref recordingState.Ops.GetMeshDraw(opIndex);
                    ref readonly FrameOpContext context = ref recordingState.Ops.GetContext(opIndex);
                    uniformSlot = GetMeshDrawUniformSlot(
                        ref recordingState,
                        opIndex,
                        draw.Draw.Renderer,
                        context,
                        draw.Draw);
                }
                draws[relativeIndex].UniformSlot = uniformSlot;
            }

            return true;
        }

        private static void PopulateScheduledMeshCommandChainExecutionBuffers(
            VulkanCommandChainRecordingEntry[] entries,
            CommandBuffer[] executionBuffers,
            int count)
        {
            for (int index = 0; index < count; index++)
                executionBuffers[index] = entries[index].SecondaryBuffer;
        }

        /// <summary>
        /// Seals the native state that a prepared mesh chain would encode. This
        /// deliberately reads only prepared draw payloads; live renderer state is
        /// not allowed to stand in for the pipelines or descriptor sets selected
        /// by preparation.
        /// </summary>
        private VulkanPreparedCommandChainKey CapturePreparedMeshCommandChainKey(
            VulkanPreparedFrameRecording preparedFrame,
            CommandChain chain,
            int scheduledStartIndex,
            out string failureReason)
        {
            failureReason = "Ready";
            FrameOpSignatureHasher pipelineHash = new();
            Span<VulkanRecordedProgramIdentity> programScratch =
                stackalloc VulkanRecordedProgramIdentity[
                    VulkanRecordedProgramIdentityBuffer.Capacity];
            int programCount = 0;
            bool programOverflow = false;
            VulkanRecordedDescriptorSetIdentityBuffer exactDescriptorSets = default;
            exactDescriptorSets.Initialize(0);
            bool complete = chain.SourceCount > 0;
            int descriptorSetCount = 0;
            for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
            {
                int preparedIndex =
                    chain.SourceStartIndex - scheduledStartIndex + drawIndex;
                if (!preparedFrame.ContainsMeshDrawRangeForOwnerValidation(
                        preparedIndex,
                        1))
                {
                    failureReason = "prepared draw range is unavailable";
                    return VulkanPreparedCommandChainKey.Incomplete;
                }

                ref readonly VkPreparedMeshDraw preparedDraw =
                    ref preparedFrame.GetMeshDrawForOwnerValidation(preparedIndex);
                VulkanPreparedMeshDrawState state = preparedDraw.RecordingState;
                VkRenderProgram program = preparedFrame.GetMeshDrawColdData(state.ColdDataIndex).Program;
                PipelineLayout layout = state.PipelineLayout;
                if (program is null ||
                    program.BindingId == 0u ||
                    program.LinkGeneration == 0UL ||
                    layout.Handle == 0UL)
                {
                    failureReason = "program or pipeline-layout identity is incomplete";
                    return VulkanPreparedCommandChainKey.Incomplete;
                }

                ulong layoutGeneration = GetCurrentVulkanResourceGeneration(
                    ObjectType.PipelineLayout,
                    layout.Handle);
                if (layoutGeneration == 0UL || state.PrimitiveCount <= 0)
                {
                    failureReason = "pipeline-layout generation or primitive range is incomplete";
                    return VulkanPreparedCommandChainKey.Incomplete;
                }

                pipelineHash.Add(program.BindingId);
                pipelineHash.Add(program.LinkGeneration);
                pipelineHash.Add(layout.Handle);
                pipelineHash.Add(layoutGeneration);
                pipelineHash.Add(state.PrimitiveCount);
                for (int primitiveIndex = 0;
                     primitiveIndex < state.PrimitiveCount;
                     primitiveIndex++)
                {
                    Pipeline pipeline = state.GetPrimitive(primitiveIndex).Pipeline;
                    ulong pipelineGeneration = pipeline.Handle == 0UL
                        ? 0UL
                        : GetCurrentVulkanResourceGeneration(
                            ObjectType.Pipeline,
                            pipeline.Handle);
                    if (pipeline.Handle == 0UL || pipelineGeneration == 0UL)
                    {
                        failureReason = "primitive pipeline identity is incomplete";
                        return VulkanPreparedCommandChainKey.Incomplete;
                    }

                    pipelineHash.Add(pipeline.Handle);
                    pipelineHash.Add(pipelineGeneration);
                    AddRecordedProgramIdentity(
                        new VulkanRecordedProgramIdentity(
                            program.BindingId,
                            program.LinkGeneration,
                            layout.Handle,
                            layoutGeneration,
                            pipeline.Handle,
                            pipelineGeneration),
                        programScratch,
                        ref programCount,
                        ref programOverflow);
                }

                if (state.UsesDescriptorHeap)
                {
                    failureReason = state.UsesDescriptorHeap
                        ? "descriptor-heap push data has no stable descriptor-set identity"
                        : "prepared descriptor bindings are incomplete";
                    complete = false;
                    continue;
                }

                VulkanRecordedDescriptorSetIdentityBuffer drawDescriptorSets =
                    CaptureRecordedDescriptorSetIdentities(
                        null,
                        preparedFrame.GetDescriptorBindings(state.DescriptorBindings));
                if (!drawDescriptorSets.IsComplete ||
                    !AppendRecordedDescriptorSetIdentities(ref exactDescriptorSets, drawDescriptorSets))
                {
                    failureReason = "published descriptor-set resource identity is incomplete";
                    complete = false;
                    continue;
                }
                for (int bindingIndex = 0;
                     bindingIndex < state.DescriptorBindings.Count;
                     bindingIndex++)
                {
                    descriptorSetCount++;
                }
            }

            VulkanRecordedProgramIdentityBuffer exactPrograms =
                FinalizeRecordedProgramIdentities(
                    programScratch,
                    programCount,
                    programOverflow);
            if (!exactPrograms.IsComplete)
            {
                failureReason = programOverflow
                    ? "prepared program/pipeline identity capacity was exceeded"
                    : "prepared program/pipeline identity is incomplete";
                complete = false;
            }

            RecordedPacketKey preparedPacketKey =
                chain.DependencySignature.RecordedPacketKey with
                {
                    DescriptorSets = exactDescriptorSets,
                    Programs = exactPrograms,
                };
            if (complete && !preparedPacketKey.IsComplete)
            {
                failureReason = CommandChainTraceEnabled || FrameDataReuseDiagnosticsEnabled
                    ? $"prepared packet identity remains incomplete after descriptor publication: {preparedPacketKey.DescribeFirstIncompleteField()}"
                    : "prepared packet identity remains incomplete after descriptor publication";
            }

            return new VulkanPreparedCommandChainKey(
                pipelineHash.ToHash(),
                ComputeRecordedDescriptorSetIdentityHash(exactDescriptorSets),
                descriptorSetCount,
                preparedPacketKey,
                complete && preparedPacketKey.IsComplete);
        }

        private void LogScheduledMeshCommandChainPreparationFallback(string reason)
        {
            // The broad command-chain trace disables the reusable schedule fast
            // path. Reuse diagnostics must be able to report this production-path
            // fallback without changing the behavior being measured.
            if (!CommandChainTraceEnabled && !FrameDataReuseDiagnosticsEnabled)
                return;

            Debug.VulkanEvery(
                $"Vulkan.CommandChains.PreparedMeshFallback.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.CommandChains] Scheduled mesh secondary preparation fell back to inline encoding: {0}",
                reason);
        }

        private VulkanRecordedDescriptorSetIdentityBuffer CaptureRecordedDescriptorSetIdentities(
            DescriptorSet[]? descriptorSets,
            ReadOnlySpan<VulkanPreparedDescriptorSetBinding> bindings)
        {
            int count = descriptorSets?.Length ?? bindings.Length;
            VulkanRecordedDescriptorSetIdentityBuffer result = default;
            result.Initialize(count);
            if (!result.IsComplete)
                return result;

            for (int i = 0; i < count; i++)
            {
                DescriptorSet set = descriptorSets is null
                    ? bindings[i].DescriptorSet
                    : descriptorSets[i];
                uint setIndex = descriptorSets is null
                    ? bindings[i].SetIndex
                    : unchecked((uint)i);
                VulkanResourceLifetimeTracker tracker =
                    ResourceRuntime.Lifetime.Tracker;
                if (set.Handle == 0 ||
                    !tracker.PublishedDescriptorSets.TryGetValue(
                        set.Handle,
                        out VulkanPublishedDescriptorSetSnapshot? published) ||
                    published.DescriptorSetLifetimeGeneration == 0UL)
                {
                    result.Invalidate();
                    return result;
                }

                VulkanRecordedDescriptorResourceIdentityBuffer resources =
                    published.GetOrCreateRecordedResources(tracker);
                if (!resources.IsComplete)
                {
                    result.Invalidate();
                    return result;
                }

                result.Set(
                    i,
                    new VulkanRecordedDescriptorSetIdentity(
                        setIndex,
                        set.Handle,
                        published.DescriptorSetLifetimeGeneration,
                        published.Generation,
                        published.Generation,
                        resources));
            }
            return result;
        }

        private static bool AppendRecordedDescriptorSetIdentities(ref VulkanRecordedDescriptorSetIdentityBuffer destination, in VulkanRecordedDescriptorSetIdentityBuffer source)
        {
            if (!destination.IsComplete || !source.IsComplete)
            {
                destination.Invalidate();
                return false;
            }

            for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
            {
                VulkanRecordedDescriptorSetIdentity candidate =
                    source.Get(sourceIndex);
                bool duplicate = false;
                for (int destinationIndex = 0;
                     destinationIndex < destination.Count;
                     destinationIndex++)
                {
                    VulkanRecordedDescriptorSetIdentity existing =
                        destination.Get(destinationIndex);
                    if (!existing.Matches(in candidate))
                        continue;

                    duplicate = true;
                    break;
                }

                if (duplicate)
                    continue;

                int nextCount = destination.Count + 1;
                if (nextCount > VulkanRecordedDescriptorSetIdentityBuffer.Capacity)
                {
                    destination.Invalidate();
                    return false;
                }

                destination.Initialize(nextCount);
                destination.Set(nextCount - 1, candidate);
            }

            return destination.IsComplete;
        }

        private static ulong ComputeRecordedDescriptorSetIdentityHash(in VulkanRecordedDescriptorSetIdentityBuffer sets)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(sets.Count); hash.Add(sets.IsComplete);
            for (int i = 0; i < sets.Count; i++) { VulkanRecordedDescriptorSetIdentity set = sets.Get(i); hash.Add(set.SetIndex); hash.Add(set.DescriptorSetHandle); hash.Add(set.DescriptorSetLifetimeGeneration); hash.Add(set.PayloadGeneration); hash.Add(set.PublicationGeneration); }
            return hash.ToHash();
        }

        private bool TryPrepareScheduledMeshCommandChainDraws(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            int startIndex,
            int passIndex,
            VulkanCommandChainRecordingBatch batch,
            VulkanCommandChainRecordingEntry[] entries,
            int secondaryCount,
            VulkanCommandChainRecordingDraw[] draws,
            RenderPass inheritedRenderPass,
            bool inheritedDynamicRendering,
            in DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
            bool inheritedDepthStencilReadOnly,
            out int preparedMeshDrawCount)
        {
            preparedMeshDrawCount = 0;
            int reservedDrawStart =
                batch.PreparedFrame.ReserveMeshDrawSlots(
                    batch.GetCommandChainColdData(entries[secondaryCount - 1].ColdDataIndex).SourceStartIndex +
                    batch.GetCommandChainColdData(entries[secondaryCount - 1].ColdDataIndex).SourceCount -
                    startIndex);
            if (reservedDrawStart != 0)
            {
                throw new InvalidOperationException(
                    "Prepared Vulkan mesh draw storage was not empty at the start of a scheduled batch.");
            }

            for (int chainIndex = 0; chainIndex < secondaryCount; chainIndex++)
            {
                VulkanCommandChainRecordingEntry entry = entries[chainIndex];
                if (!entry.NeedsRecording)
                    continue;
                CommandChain chain = batch.GetCommandChainColdData(entry.ColdDataIndex);
                for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
                {
                    int opIndex = chain.SourceStartIndex + drawIndex;
                    int relativeIndex = opIndex - startIndex;
                    ref readonly MeshDrawPayload draw = ref recordingState.Ops.GetMeshDraw(opIndex);
                    ref readonly FrameOpContext context = ref recordingState.Ops.GetContext(opIndex);
                    using var pipelineScope =
                        RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                            context.PipelineInstance);
                    if (!draw.Draw.Renderer.TryPrepareMeshDrawRecordingState(
                            batch.PreparedFrame,
                            recordingState.CommandBufferImageSlot,
                            draw.Draw,
                            inheritedRenderPass,
                            inheritedDynamicRendering,
                            inheritedDynamicRenderingFormats,
                            passIndex,
                            context.PassMetadata,
                            recordingState.Ops.GetTarget(opIndex),
                            context,
                            inheritedDepthStencilReadOnly,
                            context.PipelineInstance?.DebugName ??
                                "<no pipeline>",
                            draws[relativeIndex].UniformSlot,
                            frameDataAlreadyPrewarmed: true,
                            out VulkanPreparedMeshDrawState preparedState,
                            out string preparedStateReason))
                    {
                        _lastReusableFrameDataRefreshFailureReason =
                            $"prepared mesh draw op={opIndex}/{recordingState.Ops.Length}: {preparedStateReason}";
                        return false;
                    }

                    if (!TryPrepareSecondaryDescriptorImageRequirements(
                            batch.PreparedFrame,
                            batch.PreparedFrame.GetDescriptorBindings(preparedState.DescriptorBindings),
                            recordingState.Ops.GetTarget(opIndex),
                            recordingState.Ops.GetHeader(opIndex).PassIndex,
                            context.PassMetadata,
                            out VulkanPreparedStreamRange descriptorImagePayloads,
                            out VulkanPreparedStreamRange descriptorImageRequirements,
                            out string descriptorImageRequirementReason))
                    {
                        _lastReusableFrameDataRefreshFailureReason =
                            $"prepared mesh descriptor images op={opIndex}/{recordingState.Ops.Length}: {descriptorImageRequirementReason}";
                        return false;
                    }
                    preparedState = preparedState with
                    {
                        DescriptorImagePayloads = descriptorImagePayloads,
                        DescriptorImageRequirements = descriptorImageRequirements,
                    };

                    if (!VkPreparedMeshDraw.TryCreate(batch.PreparedFrame, opIndex, draw.Draw, draws[relativeIndex].UniformSlot, preparedState,
                            out VkPreparedMeshDraw preparedDraw,
                            out string preparedDrawReason))
                    {
                        _lastReusableFrameDataRefreshFailureReason =
                            $"prepared mesh draw op={opIndex}/{recordingState.Ops.Length}: {preparedDrawReason}";
                        return false;
                    }

                    int preparedIndex = batch.PreparedFrame.SetMeshDraw(
                        relativeIndex,
                        preparedDraw);

                    preparedMeshDrawCount++;
                    draw.Draw.Renderer.TryTransitionPreparedDescriptorImagesForSampling(
                        recordingState.CommandBuffer,
                        preparedState,
                        batch.PreparedFrame,
                        recordingState.Ops.GetTarget(opIndex),
                        recordingState.Ops.GetHeader(opIndex).PassIndex,
                        context.PassMetadata);

                    if (preparedIndex != relativeIndex)
                    {
                        throw new InvalidOperationException(
                            "Prepared Vulkan mesh draws lost source ordering.");
                    }
                }
            }

            return true;
        }

        internal unsafe bool TryExecuteMeshCommandChainSecondaryRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, int runCount, int passIndex)
        {
            ref readonly MeshDrawPayload firstDraw =
                ref recordingState.Ops.GetMeshDraw(startIndex);
            XRFrameBuffer? firstTarget = recordingState.Ops.GetTarget(startIndex);
            FrameOpContext firstContext = recordingState.Ops.GetContext(startIndex);
            PendingMeshDraw firstPendingDraw = firstDraw.Draw;
            const int minMeshDrawsPerSecondaryChain = MinMeshDrawsPerRenderPacket;

            if (recordingState.CommandChainSchedule is null ||
                !_enableSecondaryCommandBuffers ||
                runCount < minMeshDrawsPerSecondaryChain ||
                recordingState.ActiveInlineQuery is not null ||
                firstContext.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
            {
                return false;
            }

            EndActiveRenderPass(ref recordingState);

            if (!TryResolveMeshSecondaryInheritance(ref recordingState,
                    firstTarget,
                    passIndex,
                    firstContext,
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
                BuildRenderViewKey(in firstPendingDraw, passIndex, in firstContext, dynamicOverlay: false),
                passIndex,
                ResolveCommandChainTargetIdentity(firstTarget, in firstContext),
                0UL,
                false,
                primaryOwnedChainOrdinal);
            CommandChain chain = GetOrCreateCommandChain(GetCommandChainCache(recordingState.FrameDataImageIndex), chainKey);
            CommandBuffer secondary = chain.SecondaryCommandBuffer;
            bool executedInPrimary = false;
            bool meshLabelActive = false;
            bool secondaryRecordingFinished = false;
            int[] drawUniformSlots = recordingState.RecordingScratch.MeshSecondaryUniformSlots;

            if (_deviceContext.CanRecordCommandBufferDebugLabels)
                meshLabelActive = _deviceContext.CmdBeginLabel(recordingState.CommandBuffer, $"MeshCommandChainSecondary[{runCount}]");

            try
            {
                if (secondary.Handle != 0 && chain.SecondaryCommandPool.Handle == 0)
                {
                    LogCommandChainSecondaryInheritanceMismatch(
                        "mesh",
                        firstTarget,
                        passIndex,
                        $"chain-owned secondary has no owner command pool key={chainKey}");
                    DestroyCommandChainSecondaryCommandBuffer(chain);
                    secondary = default;
                }

                if (!TryEnsureMutableCommandChainSecondaryCommandBuffer(chain, recordingState.FrameDataImageIndex, recordingState.ExecutedCommandChainSecondaryHandles, out secondary))
                    return false;

                recordingState.RecordingScratch.EnsureMeshSecondaryCapacity(runCount);
                drawUniformSlots = recordingState.RecordingScratch.MeshSecondaryUniformSlots;
                for (int i = 0; i < runCount; i++)
                {
                    int opIndex = startIndex + i;
                    ref readonly MeshDrawPayload draw = ref recordingState.Ops.GetMeshDraw(opIndex);
                    ref readonly FrameOpContext context = ref recordingState.Ops.GetContext(opIndex);
                    XRFrameBuffer? target = recordingState.Ops.GetTarget(opIndex);
                    int drawUniformSlot = GetMeshDrawUniformSlot(ref recordingState,
                        opIndex,
                        draw.Draw.Renderer,
                        context,
                        draw.Draw);
                    drawUniformSlots[i] = drawUniformSlot;

                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                        context.PipelineInstance);
                    draw.Draw.Renderer.TryTransitionPreparedDescriptorImagesForSampling(
                        recordingState.CommandBuffer,
                        draw.Draw,
                        drawUniformSlot,
                        recordingState.CommandBufferImageSlot,
                        target,
                        recordingState.Ops.GetHeader(opIndex).PassIndex,
                        context.PassMetadata);

                    draw.Draw.Renderer.EnsureUniformDrawSlotCapacity(drawUniformSlot + 1);
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

                Format[] colorAttachmentFormatsArray = new Format[checked((int)Math.Max(inheritedDynamicRenderingFormats.ColorAttachmentCount, 1u))];
                uint[] colorAttachmentLocationsArray = new uint[checked((int)Math.Max(inheritedDynamicRenderingFormats.ColorAttachmentCount, 1u))];
                uint[] colorInputAttachmentIndicesArray = new uint[checked((int)Math.Max(inheritedDynamicRenderingFormats.ColorAttachmentCount, 1u))];
                uint[] depthInputAttachmentIndexArray = new uint[1];
                uint[] stencilInputAttachmentIndexArray = new uint[1];
                fixed (Format* colorAttachmentFormats = colorAttachmentFormatsArray)
                fixed (uint* colorAttachmentLocations = colorAttachmentLocationsArray)
                fixed (uint* colorInputAttachmentIndices = colorInputAttachmentIndicesArray)
                fixed (uint* depthInputAttachmentIndex = depthInputAttachmentIndexArray)
                fixed (uint* stencilInputAttachmentIndex = stencilInputAttachmentIndexArray)
                {
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

                ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.PreparedCommandChain");
                if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                    throw new Exception("Failed to begin Vulkan mesh command-chain secondary command buffer.");
                }

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
                recordingState.RenderScope.Target = firstTarget;

                try
                {
                    for (int i = startIndex; !meshSecondaryNoOp && i < startIndex + runCount; i++)
                    {
                        ref readonly MeshDrawPayload draw = ref recordingState.Ops.GetMeshDraw(i);
                        ref readonly FrameOpContext context = ref recordingState.Ops.GetContext(i);
                        using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(context.PipelineInstance);
                        RecordMeshDrawPayloadIntoCommandBuffer(ref recordingState, secondary, in draw, recordingState.Ops.GetTarget(i), context, passIndex, drawUniformSlots[i - startIndex]);
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
                BeginRenderPassForTarget(ref recordingState, firstTarget, passIndex, firstContext, secondaryContents: true);
                if (!ActiveMeshSecondaryInheritanceMatches(ref recordingState,
                        "mesh",
                        firstTarget,
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

                Array.Clear(drawUniformSlots, 0, Math.Min(runCount, drawUniformSlots.Length));

                if (meshLabelActive)
                    _deviceContext.CmdEndLabel(recordingState.CommandBuffer);
            }
        }
    }
}
