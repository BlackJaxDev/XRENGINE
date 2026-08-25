using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Command-runtime encoding of the frozen mesh-chain worker stream.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    internal unsafe void RecordPreparedMeshCommandChain(
        VulkanCommandChainRecordingBatch batch,
        int chainIndex)
    {
        ref VulkanCommandChainRecordingEntry entry = ref batch.Entries[chainIndex];
        CommandChain chain = batch.GetCommandChainColdData(entry.ColdDataIndex);
        ref readonly VulkanPreparedCommandChain preparedChain =
            ref batch.PreparedFrame.GetCommandChain(entry.PreparedChainIndex);
        RenderPacket packet = batch.PreparedFrame.GetPacketForEncoding(preparedChain);
        if (!preparedChain.Matches(chain, packet))
        {
            throw new InvalidOperationException(
                $"Prepared Vulkan command-chain input became stale before encoding. key={preparedChain.Key} " +
                $"source={preparedChain.SourceStartIndex}+{preparedChain.SourceCount} artifactGeneration=" +
                $"{preparedChain.WritableArtifact.ArtifactGeneration}.");
        }

        if (packet.DrawCount != preparedChain.SourceCount || preparedChain.SourceCount <= 0)
            throw new InvalidOperationException("Prepared mesh command-chain packet draw range does not match the scheduled source range.");

        VulkanTrackedCommandEncoder encoder = new(this);
        VulkanRecordedCommandInheritance inheritance = preparedChain.Inheritance;
        using VulkanWorkerSecondaryCommandArena.RecordingLease arenaLease =
            VulkanWorkerSecondaryCommandArena.EnterRecording(chain.RecordedArtifact.WorkerArenaOwner);
        CommandBuffer secondary = entry.SecondaryBuffer;
        chain.RecordedArtifact.Invalidate(EVulkanRecordedCommandArtifactInvalidationReason.RecordingStarted);
        Result resetResult = encoder.Reset(secondary);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanWorkerSecondaryCommandBufferReset();
        if (resetResult != Result.Success)
            throw new InvalidOperationException($"Failed to reset Vulkan worker mesh command-chain secondary command buffer: {resetResult}.");

        CommandBufferInheritanceInfo inheritanceInfo = new()
        {
            SType = StructureType.CommandBufferInheritanceInfo,
            RenderPass = inheritance.DynamicRendering ? default : inheritance.RenderPass,
            Subpass = 0,
            Framebuffer = inheritance.DynamicRendering ? default : inheritance.Framebuffer,
            OcclusionQueryEnable = Vk.False,
            QueryFlags = QueryControlFlags.None,
            PipelineStatistics = QueryPipelineStatisticFlags.None,
        };

        uint colorAttachmentCount = inheritance.DynamicRenderingFormats.ColorAttachmentCount;
        int attachmentScratchCount = checked((int)Math.Max(colorAttachmentCount, 1u));
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
        bool recordingStarted = false;
        fixed (Format* colorAttachmentFormats = colorAttachmentFormatSpan)
        fixed (uint* colorAttachmentLocations = colorAttachmentLocationSpan)
        fixed (uint* colorInputAttachmentIndices = colorInputAttachmentIndexSpan)
        {
        uint* depthInputAttachmentIndex = stackalloc uint[1];
        uint* stencilInputAttachmentIndex = stackalloc uint[1];
        CommandBufferInheritanceRenderingInfo renderingInheritanceInfo = default;
        if (inheritance.DynamicRendering)
        {
            inheritance.DynamicRenderingFormats.CopyColorAttachmentFormats(colorAttachmentFormats, colorAttachmentCount);
            renderingInheritanceInfo = new CommandBufferInheritanceRenderingInfo
            {
                SType = StructureType.CommandBufferInheritanceRenderingInfo,
                Flags = inheritance.RenderingFlags,
                ViewMask = inheritance.DynamicRenderingFormats.ViewMask,
                ColorAttachmentCount = colorAttachmentCount,
                PColorAttachmentFormats = colorAttachmentCount > 0 ? colorAttachmentFormats : null,
                DepthAttachmentFormat = inheritance.DynamicRenderingFormats.DepthAttachmentFormat,
                StencilAttachmentFormat = inheritance.DynamicRenderingFormats.StencilAttachmentFormat,
                RasterizationSamples = inheritance.Samples,
            };

            RenderingAttachmentLocationInfo localReadAttachmentLocations = default;
            RenderingInputAttachmentIndexInfo localReadInputIndices = default;
            void* localReadPNext = renderingInheritanceInfo.PNext;
            DynamicRenderingLocalReadSignature localReadSignature = inheritance.LocalReadSignature;
            encoder.TryAppendDynamicRenderingLocalReadInheritance(
                in localReadSignature,
                colorAttachmentCount,
                ref localReadPNext,
                &localReadAttachmentLocations,
                &localReadInputIndices,
                colorAttachmentLocations,
                colorInputAttachmentIndices,
                depthInputAttachmentIndex,
                stencilInputAttachmentIndex);
            renderingInheritanceInfo.PNext = localReadPNext;

            inheritanceInfo.PNext = &renderingInheritanceInfo;
        }

        CommandBufferInheritanceDescriptorHeapInfoEXTNative descriptorHeapInheritanceInfo = default;
        BindHeapInfoEXTNative inheritedSamplerHeapInfo = default;
        BindHeapInfoEXTNative inheritedResourceHeapInfo = default;
        encoder.TryAppendDescriptorHeapInheritance(
            ref inheritanceInfo,
            &descriptorHeapInheritanceInfo,
            &inheritedSamplerHeapInfo,
            &inheritedResourceHeapInfo);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.SimultaneousUseBit,
            PInheritanceInfo = &inheritanceInfo,
        };

        if (!DeviceContext.StateMachine.IsOperational)
            throw new InvalidOperationException("Vulkan device is not operational for prepared worker recording.");
        if (BeginTrackedCommandBuffer(
                secondary,
                ref beginInfo,
                "PreparedWorkerRecording") != Result.Success)
            throw new InvalidOperationException("Failed to begin Vulkan worker mesh command-chain secondary command buffer.");
        recordingStarted = true;
        }

        try
        {
            chain.RecordedArtifact.BeginRecording(CommandBuffers.ResolveRecordingGeneration(secondary));
            for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
            {
                ref readonly VkPreparedMeshDraw preparedDraw = ref GetPreparedCommandChainDraw(batch, entry.PreparedChainIndex, drawIndex);
                Viewport viewport = preparedDraw.Viewport;
                Rect2D scissor = preparedDraw.Scissor;
                uint viewportScissorCount = preparedDraw.ViewportScissorCount;
                if (viewportScissorCount > 1 &&
                    preparedDraw.IndexedViewports.Count >= (int)viewportScissorCount &&
                    preparedDraw.IndexedScissors.Count >= (int)viewportScissorCount)
                {
                    encoder.SetViewportScissor(secondary,
                        batch.PreparedFrame.GetViewports(preparedDraw.IndexedViewports),
                        batch.PreparedFrame.GetScissors(preparedDraw.IndexedScissors), viewportScissorCount);
                }
                else
                {
                    encoder.SetViewportScissor(secondary, viewport, scissor);
                }

                if (!VkMeshRenderer.RecordPreparedMeshDrawState(secondary, preparedDraw.RecordingState, batch.PreparedFrame, encoder))
                {
                    chain.State = CommandChainState.NotReady;
                    chain.DirtyReason |= CommandChainDirtyReason.PipelineGeneration;
                    throw new InvalidOperationException(
                        $"A prewarmed Vulkan command-chain draw became unavailable during secondary recording. " +
                        $"sourceIndex={preparedDraw.SourceOpIndex} mesh='{batch.PreparedFrame.GetMeshDrawColdData(preparedDraw.RecordingState.ColdDataIndex).DiagnosticMeshName}' " +
                        $"uniformSlot={preparedDraw.UniformSlot} preparedStateGeneration={batch.PreparedFrame.GetMeshDrawColdData(preparedDraw.RecordingState.ColdDataIndex).FrameDataGeneration}.");
                }
            }

            // Descriptor image contracts are part of this recording generation.
            // Freeze them while the current tracking batch is still active so an
            // older completed generation can never satisfy publication checks.
            for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
            {
                ref readonly VkPreparedMeshDraw preparedDraw = ref GetPreparedCommandChainDraw(
                    batch,
                    entry.PreparedChainIndex,
                    drawIndex);
                VulkanPreparedMeshDrawState recordingState = preparedDraw.RecordingState;
                if (recordingState.UsesDescriptorHeap || recordingState.DescriptorBindings.IsEmpty)
                    continue;
                if (!CaptureSecondaryDescriptorSetImageRequirements(
                        secondary,
                        batch.PreparedFrame,
                        recordingState.DescriptorImagePayloads,
                        recordingState.DescriptorImageRequirements,
                        out string descriptorRequirementFailure))
                {
                    throw new VulkanPlanPreconditionException(
                        $"Prepared mesh secondary 0x{secondary.Handle:X} could not publish prepared descriptor image requirements: {descriptorRequirementFailure}.");
                }
            }

            if (encoder.End(secondary) != Result.Success)
                throw new InvalidOperationException("Failed to end Vulkan worker mesh command-chain secondary command buffer.");
            recordingStarted = false;
        }
        catch
        {
            if (recordingStarted)
                encoder.Abandon(secondary);
            FailCommandChainSecondaryArtifactPublication(chain);
            throw;
        }

        chain.RecordedUniformSlotSignature = ComputeUniformSlotSignature(
            batch.Draws, chain.SourceStartIndex - batch.StartIndex, chain.SourceCount);
        chain.State = CommandChainState.Recorded;
        chain.FrameDataRefreshTouchedDescriptors = false;
        chain.RecordedArtifact.StoreInheritance(inheritance);
        // Publish the prepared native key and the dependency signature as one
        // operation before the artifact snapshots that signature below. The
        // former worker-only helper updated PreparedAuthority but left the old
        // descriptor key in DependencySignature, permanently making the newly
        // recorded secondary disagree with its own current chain state.
        PublishPreparedCommandChainAuthority(chain, preparedChain.Authority);
        if (System.Threading.Volatile.Read(ref batch.CancelRequested) != 0)
        {
            FailCommandChainSecondaryArtifactPublication(chain);
            return;
        }

        _ = TryPublishCommandChainSecondaryArtifact(chain, ResourceRuntime);
    }

    private static ref readonly VkPreparedMeshDraw GetPreparedCommandChainDraw(
        VulkanCommandChainRecordingBatch batch,
        int preparedChainIndex,
        int drawIndex)
    {
        ref readonly VulkanPreparedCommandChain preparedChain = ref batch.PreparedFrame.GetCommandChain(preparedChainIndex);
        if ((uint)drawIndex >= (uint)preparedChain.SourceCount)
            throw new ArgumentOutOfRangeException(nameof(drawIndex));

        return ref batch.PreparedFrame.GetMeshDraw(preparedChain.PreparedDrawStartIndex + drawIndex);
    }

    private static ulong ComputeUniformSlotSignature(
        VulkanCommandChainRecordingDraw[] draws,
        int startIndex,
        int count)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(count);
        for (int index = 0; index < count; index++)
            hash.Add(draws[startIndex + index].UniformSlot);
        return hash.ToHash();
    }

}
