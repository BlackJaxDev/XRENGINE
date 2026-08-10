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
        CommandChain chain = batch.Chains[chainIndex];
        ref readonly VulkanPreparedCommandChain preparedChain =
            ref batch.PreparedFrame.GetCommandChain(chainIndex);
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

        VulkanTrackedCommandEncoder encoder = batch.PreparedWorkerContext.Encoder
            ?? throw new InvalidOperationException("Prepared mesh command-chain worker inputs were not frozen.");
        VulkanRecordedCommandInheritance inheritance = preparedChain.Inheritance;
        using VulkanWorkerSecondaryCommandArena.RecordingLease arenaLease =
            VulkanWorkerSecondaryCommandArena.EnterRecording(chain.RecordedArtifact.WorkerArenaOwner);
        CommandBuffer secondary = batch.SecondaryBuffers[chainIndex];
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
        Format* colorAttachmentFormats = stackalloc Format[(int)Math.Max(colorAttachmentCount, 1u)];
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
            uint* colorAttachmentLocations = stackalloc uint[(int)Math.Max(colorAttachmentCount, 1u)];
            uint* colorInputAttachmentIndices = stackalloc uint[(int)Math.Max(colorAttachmentCount, 1u)];
            uint* depthInputAttachmentIndex = stackalloc uint[1];
            uint* stencilInputAttachmentIndex = stackalloc uint[1];
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

        if (!encoder.DeviceContext.StateMachine.IsOperational)
            throw new InvalidOperationException("Vulkan device is not operational for prepared worker recording.");
        if (encoder.Api.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
            throw new InvalidOperationException("Failed to begin Vulkan worker mesh command-chain secondary command buffer.");

        encoder.CommandRuntime.ResetBindState(encoder, secondary);
        chain.RecordedArtifact.BeginRecording(encoder.CommandRuntime.CommandBuffers.ResolveRecordingGeneration(secondary));
        for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
        {
            ref readonly VkPreparedMeshDraw preparedDraw = ref GetPreparedCommandChainDraw(batch, chainIndex, drawIndex);
            Viewport viewport = preparedDraw.Viewport;
            Rect2D scissor = preparedDraw.Scissor;
            uint viewportScissorCount = preparedDraw.ViewportScissorCount;
            if (viewportScissorCount > 1 &&
                preparedDraw.IndexedViewports is { } indexedViewports &&
                preparedDraw.IndexedScissors is { } indexedScissors &&
                indexedViewports.Length >= (int)viewportScissorCount &&
                indexedScissors.Length >= (int)viewportScissorCount)
            {
                encoder.SetViewportScissor(secondary, indexedViewports, indexedScissors, viewportScissorCount);
            }
            else
            {
                encoder.SetViewportScissor(secondary, viewport, scissor);
            }

            if (!VkMeshRenderer.RecordPreparedMeshDrawState(secondary, preparedDraw.RecordingState, encoder))
            {
                chain.State = CommandChainState.NotReady;
                chain.DirtyReason |= CommandChainDirtyReason.PipelineGeneration;
                throw new InvalidOperationException(
                    $"A prewarmed Vulkan command-chain draw became unavailable during secondary recording. " +
                    $"sourceIndex={preparedDraw.SourceOpIndex} mesh='{preparedDraw.DiagnosticMeshName}' " +
                    $"uniformSlot={preparedDraw.UniformSlot} preparedStateGeneration={preparedDraw.RecordingState.FrameDataGeneration}.");
            }
        }

        if (encoder.End(secondary) != Result.Success)
            throw new InvalidOperationException("Failed to end Vulkan worker mesh command-chain secondary command buffer.");

        for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
        {
            ref readonly VkPreparedMeshDraw preparedDraw = ref GetPreparedCommandChainDraw(
                batch,
                chainIndex,
                drawIndex);
            VulkanPreparedMeshDrawState recordingState = preparedDraw.RecordingState;
            if (recordingState.UsesDescriptorHeap ||
                recordingState.DescriptorBindingCount == 0)
            {
                continue;
            }
            if (recordingState.DescriptorBindings is not { } descriptorBindings ||
                descriptorBindings.Length < recordingState.DescriptorBindingCount)
            {
                throw new VulkanPlanPreconditionException(
                    "A prepared mesh secondary has an incomplete descriptor-binding snapshot.");
            }

            for (int bindingIndex = 0;
                 bindingIndex < recordingState.DescriptorBindingCount;
                 bindingIndex++)
            {
                DescriptorSet descriptorSet =
                    descriptorBindings[bindingIndex].DescriptorSet;
                if (!CaptureSecondaryDescriptorSetImageRequirements(
                        secondary,
                        descriptorSet,
                        preparedDraw.Target,
                        out string descriptorRequirementFailure))
                {
                    throw new VulkanPlanPreconditionException(
                        $"Prepared mesh secondary 0x{secondary.Handle:X} could not capture descriptor set 0x{descriptorSet.Handle:X} publication requirements: {descriptorRequirementFailure}.");
                }
            }
        }

        chain.RecordedUniformSlotSignature = ComputeUniformSlotSignature(
            batch.UniformSlots, chain.SourceStartIndex - batch.StartIndex, chain.SourceCount);
        chain.State = CommandChainState.Recorded;
        chain.FrameDataRefreshTouchedDescriptors = false;
        chain.RecordedArtifact.StoreInheritance(inheritance);
        // Publish the prepared native key and the dependency signature as one
        // operation before the artifact snapshots that signature below. The
        // former worker-only helper updated PreparedAuthority but left the old
        // descriptor key in DependencySignature, permanently making the newly
        // recorded secondary disagree with its own current chain state.
        PublishPreparedCommandChainAuthority(chain, preparedChain.Authority);
        PublishRecordedSecondary(chain, encoder.ResourceRuntime);
    }

    private static ref readonly VkPreparedMeshDraw GetPreparedCommandChainDraw(
        VulkanCommandChainRecordingBatch batch,
        int chainIndex,
        int drawIndex)
    {
        ref readonly VulkanPreparedCommandChain preparedChain = ref batch.PreparedFrame.GetCommandChain(chainIndex);
        if ((uint)drawIndex >= (uint)preparedChain.SourceCount)
            throw new ArgumentOutOfRangeException(nameof(drawIndex));

        return ref batch.PreparedFrame.GetMeshDraw(preparedChain.PreparedDrawStartIndex + drawIndex);
    }

    private static ulong ComputeUniformSlotSignature(int[] uniformSlots, int startIndex, int count)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(count);
        for (int index = 0; index < count; index++)
            hash.Add(uniformSlots[startIndex + index]);
        return hash.ToHash();
    }

    private static void PublishRecordedSecondary(CommandChain chain, VulkanResourceRuntime resources)
    {
        VulkanRecordedCommandArtifact artifact = chain.RecordedArtifact;
        ulong handle = unchecked((ulong)artifact.NativeBuffer.Handle);
        if (handle == 0)
        {
            artifact.MarkFailed();
            return;
        }

        lock (resources.Lifetime.Tracker.SyncRoot)
        {
            IReadOnlyList<KeyValuePair<VulkanResourceLifetimeKey, ulong>> dependencies = Array.Empty<KeyValuePair<VulkanResourceLifetimeKey, ulong>>();
            ulong generation = artifact.RecordingGeneration;
            int queuedSubmissionCount = 0;
            if (resources.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                dependencies = lifetime.TouchedDependencies;
                generation = lifetime.RecordingGeneration;
                queuedSubmissionCount = lifetime.QueuedSubmissionCount;
            }

            int recordedReferences = 0;
            if (resources.Lifetime.Tracker.ResourceLifetimes.TryGetValue(new VulkanResourceLifetimeKey(ObjectType.CommandBuffer, handle), out VulkanResourceLifetimeRecord? resource))
                recordedReferences = resource.Pins.RecordedReferenceCount;
            ref readonly CommandRecordingDependencySignature dependencySignature =
                ref chain.DependencySignatureReference;
            artifact.PublishExecutable(
                in dependencySignature,
                dependencies,
                generation,
                queuedSubmissionCount,
                recordedReferences);
        }
    }

}
