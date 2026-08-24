using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCommandRecordingDependencyTests
{
    [Test]
    public void SharedIdentityComponents_KeepPrimaryAndSecondaryDependenciesSeparate()
    {
        CommandRecordingDependencySignature baseline = CreateSignature();
        VulkanCommandIdentityComponents baselineComponents =
            baseline.CaptureIdentityComponents();

        VulkanCommandIdentityComponents primaryChange =
            (baseline with { RenderArea = baseline.RenderArea + 1 })
            .CaptureIdentityComponents();
        primaryChange.PrimaryOnly.ShouldNotBe(
            baselineComponents.PrimaryOnly);
        primaryChange.SecondaryOnly.ShouldBe(
            baselineComponents.SecondaryOnly);

        VulkanCommandIdentityComponents secondaryChange =
            (baseline with
            {
                MeshBindingIdentity =
                    baseline.MeshBindingIdentity + 1,
            }).CaptureIdentityComponents();
        secondaryChange.PrimaryOnly.ShouldBe(
            baselineComponents.PrimaryOnly);
        secondaryChange.SecondaryOnly.ShouldNotBe(
            baselineComponents.SecondaryOnly);

        VulkanCommandIdentityComponents queueChange =
            (baseline with { QueueFamily = baseline.QueueFamily + 1 })
            .CaptureIdentityComponents();
        baselineComponents.Compare(queueChange).Component.ShouldBe(
            EVulkanCommandIdentityComponent.QueueAssumptions);
    }

    [Test]
    public void StructuralMismatch_RejectsReuseAndReportsFirstField()
    {
        CommandRecordingDependencySignature recorded = CreateSignature();
        CommandRecordingDependencySignature current = recorded with { ViewMask = 0x3u };

        CommandRecordingDependencyMismatch mismatch = recorded.Compare(current);

        mismatch.Field.ShouldBe(CommandRecordingDependencyField.ViewMask);
        mismatch.InvalidationClass.ShouldBe(CommandRecordingInvalidationClass.Structural);
        mismatch.RequiresRecording.ShouldBeTrue();
    }

    [Test]
    public void BindingIdentityMismatch_RejectsOnlyAffectedRecording()
    {
        CommandRecordingDependencySignature recorded = CreateSignature();
        CommandRecordingDependencySignature current = recorded with { DescriptorSetGeneration = 99UL };

        CommandRecordingDependencyMismatch mismatch = recorded.Compare(current);

        mismatch.Field.ShouldBe(CommandRecordingDependencyField.DescriptorSetGeneration);
        mismatch.InvalidationClass.ShouldBe(CommandRecordingInvalidationClass.BindingIdentity);
        mismatch.RequiresRecording.ShouldBeTrue();
    }

    [Test]
    public void ExplicitOwnedGenerationFields_ClassifyEveryRecordingDependency()
    {
        CommandRecordingDependencySignature recorded = CreateSignature();
        (CommandRecordingDependencySignature Current, CommandRecordingDependencyField Field)[] changes =
        [
            (recorded with { OutputPassAttachment = recorded.OutputPassAttachment + 1 }, CommandRecordingDependencyField.OutputPassAttachment),
            (recorded with { RenderArea = recorded.RenderArea + 1 }, CommandRecordingDependencyField.RenderArea),
            (recorded with { ViewMask = recorded.ViewMask + 1 }, CommandRecordingDependencyField.ViewMask),
            (recorded with { QueueFamily = recorded.QueueFamily + 1 }, CommandRecordingDependencyField.QueueFamily),
            (recorded with { DynamicRenderingInheritance = recorded.DynamicRenderingInheritance + 1 }, CommandRecordingDependencyField.DynamicRenderingInheritance),
            (recorded with { PipelineGeneration = recorded.PipelineGeneration + 1 }, CommandRecordingDependencyField.PipelineGeneration),
            (recorded with { PipelineLayoutGeneration = recorded.PipelineLayoutGeneration + 1 }, CommandRecordingDependencyField.PipelineLayoutGeneration),
            (recorded with { MeshBindingIdentity = recorded.MeshBindingIdentity + 1 }, CommandRecordingDependencyField.MeshBindingIdentity),
            (recorded with { IndexBufferBindingIdentity = recorded.IndexBufferBindingIdentity + 1 }, CommandRecordingDependencyField.IndexBufferBindingIdentity),
            (recorded with { VertexBufferBindingIdentity = recorded.VertexBufferBindingIdentity + 1 }, CommandRecordingDependencyField.VertexBufferBindingIdentity),
            (recorded with { BufferAllocationGeneration = recorded.BufferAllocationGeneration + 1 }, CommandRecordingDependencyField.BufferAllocationGeneration),
            (recorded with { ImageAllocationGeneration = recorded.ImageAllocationGeneration + 1 }, CommandRecordingDependencyField.ImageAllocationGeneration),
            (recorded with { ImageViewGeneration = recorded.ImageViewGeneration + 1 }, CommandRecordingDependencyField.ImageViewGeneration),
            (recorded with { SamplerAllocationGeneration = recorded.SamplerAllocationGeneration + 1 }, CommandRecordingDependencyField.SamplerAllocationGeneration),
            (recorded with { DescriptorLayoutGeneration = recorded.DescriptorLayoutGeneration + 1 }, CommandRecordingDependencyField.DescriptorLayoutGeneration),
            (recorded with { DescriptorSetGeneration = recorded.DescriptorSetGeneration + 1 }, CommandRecordingDependencyField.DescriptorSetGeneration),
            (recorded with { ResourcePlanGeneration = recorded.ResourcePlanGeneration + 1 }, CommandRecordingDependencyField.ResourcePlanGeneration),
            (recorded with { ExternalTargetVariant = recorded.ExternalTargetVariant + 1 }, CommandRecordingDependencyField.ExternalTargetVariant),
            (recorded with { FrameSlotVariant = recorded.FrameSlotVariant + 1 }, CommandRecordingDependencyField.FrameSlotVariant),
            (recorded with { DescriptorPublicationGeneration = recorded.DescriptorPublicationGeneration + 1 }, CommandRecordingDependencyField.DescriptorPublicationGeneration),
            (recorded with { DataPublicationGeneration = recorded.DataPublicationGeneration + 1 }, CommandRecordingDependencyField.DataPublicationGeneration),
            (recorded with { VolatileSuffixGeneration = recorded.VolatileSuffixGeneration + 1 }, CommandRecordingDependencyField.VolatileSuffixGeneration),
        ];

        for (int index = 0; index < changes.Length; index++)
            recorded.Compare(changes[index].Current).Field.ShouldBe(changes[index].Field);
    }

    [Test]
    public void CommandChainPrimaryDependency_IgnoresBindingsOwnedBySecondaryBuffers()
    {
        CommandRecordingDependencySignature recorded = CreateSignature();
        CommandRecordingDependencySignature changedSecondaryBindings = recorded with
        {
            PipelineLayoutGeneration = recorded.PipelineLayoutGeneration + 1UL,
            MeshBindingIdentity = recorded.MeshBindingIdentity + 1UL,
            IndexBufferBindingIdentity = recorded.IndexBufferBindingIdentity + 1UL,
            VertexBufferBindingIdentity = recorded.VertexBufferBindingIdentity + 1UL,
            DescriptorPublicationGeneration = recorded.DescriptorPublicationGeneration + 1UL,
        };

        recorded.Compare(changedSecondaryBindings).RequiresRecording.ShouldBeTrue();
        recorded.CompareCommandChainPrimary(changedSecondaryBindings)
            .ShouldBe(CommandRecordingDependencyMismatch.None);

        recorded.CompareCommandChainPrimary(changedSecondaryBindings)
            .ShouldBe(CommandRecordingDependencyMismatch.None);

        CommandRecordingDependencySignature changedDescriptorPublication =
            recorded with
            {
                DescriptorPublicationGeneration = recorded.DescriptorPublicationGeneration + 1UL,
            };
        recorded.CompareCommandChainPrimary(changedDescriptorPublication)
            .ShouldBe(CommandRecordingDependencyMismatch.None);

        recorded.CompareCommandChainPrimary(recorded with
            {
                OutputPassAttachment = recorded.OutputPassAttachment + 1UL,
            }).ShouldBe(CommandRecordingDependencyMismatch.None);

        CommandRecordingDependencySignature changedRenderArea =
            recorded with { RenderArea = recorded.RenderArea + 1UL };
        recorded.Compare(changedRenderArea).Field.ShouldBe(CommandRecordingDependencyField.RenderArea);
        recorded.CompareCommandChainPrimary(changedRenderArea)
            .ShouldBe(CommandRecordingDependencyMismatch.None);

        CommandRecordingDependencySignature changedViewMask =
            recorded with { ViewMask = recorded.ViewMask + 1u };
        recorded.Compare(changedViewMask).Field.ShouldBe(CommandRecordingDependencyField.ViewMask);
        recorded.CompareCommandChainPrimary(changedViewMask)
            .ShouldBe(CommandRecordingDependencyMismatch.None);

        CommandRecordingDependencySignature changedInheritance =
            recorded with { DynamicRenderingInheritance = recorded.DynamicRenderingInheritance + 1UL };
        recorded.Compare(changedInheritance).Field.ShouldBe(CommandRecordingDependencyField.DynamicRenderingInheritance);
        recorded.CompareCommandChainPrimary(changedInheritance)
            .ShouldBe(CommandRecordingDependencyMismatch.None);

        CommandRecordingDependencySignature changedPipeline =
            recorded with { PipelineGeneration = recorded.PipelineGeneration + 1UL };
        recorded.Compare(changedPipeline).Field.ShouldBe(CommandRecordingDependencyField.PipelineGeneration);
        recorded.CompareCommandChainPrimary(changedPipeline)
            .ShouldBe(CommandRecordingDependencyMismatch.None);

        CommandRecordingDependencySignature changedFallbackContextResources = recorded with
        {
            ImageAllocationGeneration = recorded.ImageAllocationGeneration + 1UL,
            ImageViewGeneration = recorded.ImageViewGeneration + 1UL,
            SamplerAllocationGeneration = recorded.SamplerAllocationGeneration + 1UL,
            DescriptorLayoutGeneration = recorded.DescriptorLayoutGeneration + 1UL,
            DescriptorSetGeneration = recorded.DescriptorSetGeneration + 1UL,
        };
        recorded.Compare(changedFallbackContextResources)
            .Field.ShouldBe(CommandRecordingDependencyField.ImageAllocationGeneration);
        recorded.CompareCommandChainPrimary(changedFallbackContextResources)
            .ShouldBe(CommandRecordingDependencyMismatch.None);

        CommandRecordingDependencySignature changedResourcePlan = recorded with
        {
            ResourcePlanGeneration = recorded.ResourcePlanGeneration + 1UL,
        };
        recorded.Compare(changedResourcePlan)
            .Field.ShouldBe(CommandRecordingDependencyField.ResourcePlanGeneration);
        recorded.CompareCommandChainPrimary(changedResourcePlan)
            .ShouldBe(CommandRecordingDependencyMismatch.None);
    }

    [Test]
    public void DescriptorPublicationMismatch_RequiresRecording()
    {
        CommandRecordingDependencySignature recorded = CreateSignature();
        CommandRecordingDependencySignature current = recorded with
        {
            DescriptorPublicationGeneration = 100UL,
            DataPublicationGeneration = 101UL,
            VolatileSuffixGeneration = 102UL,
        };

        CommandRecordingDependencyMismatch mismatch = recorded.Compare(current);

        mismatch.Field.ShouldBe(CommandRecordingDependencyField.DescriptorPublicationGeneration);
        mismatch.InvalidationClass.ShouldBe(CommandRecordingInvalidationClass.BindingIdentity);
        mismatch.RequiresRecording.ShouldBeTrue();
    }

    [Test]
    public void PublicationGenerations_UseSpecRequiredInvalidationClasses()
    {
        CommandRecordingDependencySignature recorded = CreateSignature();
        CommandRecordingDependencySignature[] updates =
        [
            recorded with { DescriptorPublicationGeneration = recorded.DescriptorPublicationGeneration + 1UL },
            recorded with { DataPublicationGeneration = recorded.DataPublicationGeneration + 1UL },
            recorded with { VolatileSuffixGeneration = recorded.VolatileSuffixGeneration + 1UL },
        ];
        CommandRecordingDependencyField[] expectedFields =
        [
            CommandRecordingDependencyField.DescriptorPublicationGeneration,
            CommandRecordingDependencyField.DataPublicationGeneration,
            CommandRecordingDependencyField.VolatileSuffixGeneration,
        ];
        CommandRecordingInvalidationClass[] expectedClasses =
        [
            CommandRecordingInvalidationClass.BindingIdentity,
            CommandRecordingInvalidationClass.DataOnly,
            CommandRecordingInvalidationClass.DataOnly,
        ];

        for (int i = 0; i < updates.Length; i++)
        {
            CommandRecordingDependencyMismatch mismatch = recorded.Compare(updates[i]);
            mismatch.Field.ShouldBe(expectedFields[i]);
            mismatch.InvalidationClass.ShouldBe(expectedClasses[i]);
            mismatch.RequiresRecording.ShouldBe(i == 0);
        }
    }

    [Test]
    public void ProductionPrimaryReuseDefaultsOnAndDiagnosticOverrideIsOptional()
    {
        VulkanCommandRuntime.VulkanPrimaryCommandBufferReuseSafe.ShouldBeTrue();
        RuntimeRenderingHostServiceDefaults.EnableVulkanPrimaryCommandBufferReuse.ShouldBeTrue();
        new XREngine.VulkanCommandRecordingSettings().PrimaryCommandBufferReuseEnabled.ShouldBeTrue();

        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        source.ShouldContain("VulkanPrimaryCommandBufferReuseOverride ??");
        source.ShouldContain("RuntimeRenderingHostServices.Settings.EnableVulkanPrimaryCommandBufferReuse");
        source.ShouldNotContain("VulkanPrimaryCommandBufferReuseSafe = false");
    }

    [Test]
    public void HybridCommandRecording_DefaultsToAutoAndEnvironmentOverrideWins()
    {
        RuntimeRenderingHostServiceDefaults.VulkanCommandRecordingMode
            .ShouldBe(EVulkanCommandRecordingMode.Auto);
        new XREngine.VulkanCommandRecordingSettings().Mode
            .ShouldBe(EVulkanCommandRecordingMode.Auto);

        VulkanCommandRuntime.ResolveCommandChainsRequested(EVulkanCommandRecordingMode.Auto, null)
            .ShouldBeTrue();
        VulkanCommandRuntime.ResolveCommandChainsRequested(EVulkanCommandRecordingMode.Hybrid, null)
            .ShouldBeTrue();
        VulkanCommandRuntime.ResolveCommandChainsRequested(EVulkanCommandRecordingMode.Inline, null)
            .ShouldBeFalse();
        VulkanCommandRuntime.ResolveCommandChainsRequested((EVulkanCommandRecordingMode)int.MaxValue, null)
            .ShouldBeFalse();
        VulkanCommandRuntime.ResolveCommandChainsRequested(EVulkanCommandRecordingMode.Auto, false)
            .ShouldBeFalse();
        VulkanCommandRuntime.ResolveCommandChainsRequested(EVulkanCommandRecordingMode.Inline, true)
            .ShouldBeTrue();
    }

    [Test]
    public void HybridCommandRecording_AutoDoesNotPromoteExternalSwapchainTargets()
    {
        string lowering = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "bool commandChainsEnabledForTarget = allowExternalSwapchainTarget");
        string targetPolicy = Slice(
            lowering,
            "bool commandChainsEnabledForTarget = allowExternalSwapchainTarget",
            "if (!commandChainsEnabledForTarget)");

        targetPolicy.ShouldContain("? CommandChainsExplicitlyRequested");
        targetPolicy.ShouldContain(": CommandChainsEnabledForCurrentRecording");
    }

    [Test]
    public void CommandChainPipelineGeneration_TracksSuccessfulProgramRelinks()
    {
        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string lowering = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "pipelineGenerationHash.Add(ResolvePipelineGeneration(drawOp))",
            "hash.Add(program?.LinkGeneration ?? 0UL)");

        program.ShouldContain("internal ulong LinkGeneration");
        program.ShouldContain("Interlocked.Increment(ref _linkGeneration)");
        lowering.ShouldContain("pipelineGenerationHash.Add(ResolvePipelineGeneration(drawOp))");
        lowering.ShouldContain("hash.Add(program?.LinkGeneration ?? 0UL)");
    }

    [Test]
    public void CleanPrimaryReuse_UsesTheSameDeferredOverlayScheduleKeyAsLowering()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs")
            .Replace("\r\n", "\n");
        string fastReuse = Slice(
            recording,
            "private bool TryReuseCleanCommandChainPrimaryVariant(",
            "private bool TryRefreshReusableCommandBufferFrameData(");

        fastReuse.ShouldContain(
            "FrameOp[] scheduledDynamicUiBatchTextOps = preserveSwapchainForOverlay\n" +
            "                ? Array.Empty<FrameOp>()\n" +
            "                : dynamicUiBatchTextOps;");
        fastReuse.ShouldContain(
            "imageIndex,\n" +
            "                    ops,\n" +
            "                    scheduledDynamicUiBatchTextOps,\n" +
            "                    plannerRevision);");
    }

    [Test]
    public void CommandChainPrimaryReuse_TracksRenderTargetAndTopologyInsteadOfVisibleDrawSignature()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string primaryDependency = Slice(
            recording,
            "private static CommandRecordingDependencySignature CaptureCommandRecordingDependencySignature(",
            "private static void CapturePreparedBindingIdentities(");
        string fastReuse = Slice(
            recording,
            "private bool TryReuseCleanCommandChainPrimaryVariant(",
            "private bool TryRefreshReusableCommandBufferFrameData(");

        primaryDependency.ShouldContain("OutputPassAttachment: outputPassAttachmentHash.ToHash()");
        primaryDependency.ShouldContain("outputPassAttachmentHash.Add(context.OutputFrameBufferIdentity)");
        primaryDependency.ShouldContain("outputPassAttachmentHash.Add(context.OutputTargetIdentity)");
        primaryDependency.ShouldNotContain("staticStructuralSignature");
        fastReuse.ShouldNotContain(
            "variant.CommandChainScheduleSignature != cachedSchedule.StructuralSignature");
        fastReuse.ShouldContain(
            "variant.CommandChainPrimaryGroupSignature != currentPrimaryGroupSignature");
        fastReuse.ShouldContain(
            "variant.CommandChainPrimaryGroupSignature = currentPrimaryGroupSignature");
        fastReuse.ShouldContain(
            "CompareCommandChainPrimary(\n" +
            "                        currentDependencySignature,\n" +
            "                        allCommandChainGroupsUseSecondaryBuffers)");
        fastReuse.ShouldContain(
            "descriptorResourcesCapturedByFrameSignature:\n" +
            "                                allCommandChainGroupsUseSecondaryBuffers");
        fastReuse.ShouldContain(
            "variant.CommandChainPrimarySkeletonSignature != currentPrimarySkeletonSignature");
        fastReuse.ShouldNotContain("variant.PlannerRevision != plannerRevision");
    }

    [Test]
    public void RecordedPrimary_PublishesPostRecordingSecondaryArtifactIdentity()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        int recordingEnd = recording.IndexOf(
            "_commandRecorder.ExitRecordingScope();",
            StringComparison.Ordinal);
        int recompute = recording.IndexOf(
            "commandChainPrimaryIdentityComponents =",
            recordingEnd,
            StringComparison.Ordinal);
        int publish = recording.IndexOf(
            "variant.CommandChainPrimaryGroupSignature =",
            recompute,
            StringComparison.Ordinal);

        recordingEnd.ShouldBeGreaterThanOrEqualTo(0);
        recompute.ShouldBeGreaterThan(recordingEnd);
        publish.ShouldBeGreaterThan(recompute);
    }

    [Test]
    public void PrimaryVariantCache_PreservesCleanRotatingOutputAttachments()
    {
        CommandRecordingDependencyMismatch attachmentMismatch = new(
            CommandRecordingDependencyField.OutputPassAttachment,
            CommandRecordingInvalidationClass.Structural);
        CommandRecordingDependencyMismatch pipelineMismatch = new(
            CommandRecordingDependencyField.PipelineGeneration,
            CommandRecordingInvalidationClass.Structural);

        attachmentMismatch.Field.ShouldBe(
            CommandRecordingDependencyField.OutputPassAttachment);
        pipelineMismatch.Field.ShouldBe(
            CommandRecordingDependencyField.PipelineGeneration);
        CreateSignature().CompareCommandChainPrimary(
            CreateSignature() with
            {
                OutputPassAttachment = 99,
                PipelineGeneration = 99,
            }).ShouldBe(CommandRecordingDependencyMismatch.None);
    }

    [Test]
    public void CommandChainUniformSlotSignature_TracksOrderedBakedOffsets()
    {
        int[] baseline = [4, 8, 12, 16];
        int[] same = [4, 8, 12, 16];
        int[] reordered = [8, 4, 12, 16];

        VulkanCommandRuntime.ComputeCommandChainUniformSlotSignature(baseline, 0, baseline.Length)
            .ShouldBe(VulkanCommandRuntime.ComputeCommandChainUniformSlotSignature(same, 0, same.Length));
        VulkanCommandRuntime.ComputeCommandChainUniformSlotSignature(baseline, 0, baseline.Length)
            .ShouldNotBe(VulkanCommandRuntime.ComputeCommandChainUniformSlotSignature(reordered, 0, reordered.Length));
    }

    [Test]
    public void ReusableChainRefreshAdvancesDataOnlyDependencyBaseline()
    {
        string source = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "chain.DependencySignature = BuildCommandChainDependencySignature(");

        source.ShouldContain("chain.DependencySignature = BuildCommandChainDependencySignature(");
        source.ShouldContain("chain.Key,");
    }

    [Test]
    public void DescriptorPublication_DoesNotMasqueradeAsSamplerAllocation()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string primaryDependency = Slice(
            recording,
            "private static CommandRecordingDependencySignature CaptureCommandRecordingDependencySignature(",
            "private static void CapturePreparedBindingIdentities(");
        primaryDependency.ShouldContain("SamplerAllocationGeneration: descriptorBindingIdentity");
        primaryDependency.ShouldNotContain("SamplerAllocationGeneration: generations.Descriptor");

        string lowering = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "internal static CommandRecordingDependencySignature BuildCommandChainDependencySignature(");
        string chainDependency = Slice(
            lowering,
            "internal static CommandRecordingDependencySignature BuildCommandChainDependencySignature(",
            "internal static void ValidateReusableCommandChainReferences(");
        chainDependency.ShouldContain("SamplerAllocationGeneration: packet.DescriptorSnapshot.DescriptorSetSignature");
        chainDependency.ShouldNotContain("SamplerAllocationGeneration: packet.DescriptorSnapshot.DescriptorGeneration");
        chainDependency.ShouldContain("DescriptorPublicationGeneration: packet.DescriptorSnapshot.DescriptorGeneration");
    }

    [Test]
    public void ProgramBindingIdentity_ScopesPrimaryAndCommandChainReuse()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string lowering = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "hash.Add(draw.Draw.PreparedProgram?.BindingId ?? 0u)",
            "MixSignature(descriptorSetSignature, preparedProgram.BindingId)");
        string manifest = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineVariantManifest.cs");

        recording.ShouldContain("programHash.Add(draw.PreparedProgram?.BindingId ?? 0u);");
        lowering.ShouldContain("hash.Add(draw.Draw.PreparedProgram?.BindingId ?? 0u);");
        lowering.ShouldContain("MixSignature(descriptorSetSignature, preparedProgram.BindingId)");
        lowering.ShouldContain("? unchecked((int)preparedProgram.BindingId)");
        manifest.ShouldContain("hash.Add(draw.PreparedProgram?.BindingId ?? 0u);");
    }

    [Test]
    public void PrimaryReuseCapability_IsEnabledByDependencyValidation()
        => VulkanCommandRuntime.VulkanPrimaryCommandBufferReuseSafe.ShouldBeTrue();

    [Test]
    public void InlinePrimaryReuse_ReRecordsOnlyForOutputViewportCameraChanges()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string fastReuse = Slice(
            recording,
            "private bool TryReuseCleanCommandChainPrimaryVariant(",
            "private bool TryRefreshReusableCommandBufferFrameData(");
        string cameraPoseDirtyCheck = Slice(
            recording,
            "// An inline desktop primary owns the swapchain writer and must be re-recorded",
            "IsCommandBufferVariantImageLayoutStateDirty(");

        fastReuse.ShouldNotContain("variant.RecordedGenerations.CameraPose != currentGenerations.CameraPose");
        cameraPoseDirtyCheck.ShouldContain("_commandScheduler.HasCameraGenerationChanged(");
        string scheduler = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanCommandScheduler.cs");
        scheduler.ShouldContain("=> !usesCommandChains && recordedGeneration != currentGeneration;");
    }

    [Test]
    public void CameraPoseReuseKey_IsIndependentOfVisibilityDrawOrdering()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string cameraGeneration = Slice(
            recording,
            "private static ulong ComputeCameraPoseGeneration(",
            "private static ulong ComputeFrameDataGeneration(");

        cameraGeneration.ShouldContain("Span<ulong> uniqueCameraPoseSignatures = stackalloc ulong[128]");
        cameraGeneration.ShouldContain("TryGetPrimaryViewportCameraPoseDraw");
        cameraGeneration.ShouldContain("IsCameraAttachedToOutputViewport");
        cameraGeneration.ShouldContain("case IndirectDrawOp indirectDraw");
        cameraGeneration.ShouldContain("SortCameraPoseSignatures(uniqueCameraPoseSignatures, uniqueCameraPoseCount)");
        cameraGeneration.ShouldContain("ComputeCameraPoseGenerationConservatively");
    }

    [Test]
    public void DescriptorWriteBreadcrumbs_DoNotSplitFrameOpRecordingContexts()
    {
        string plannerState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string descriptorGeneration = Slice(
            plannerState,
            "private ulong ResolveFrameOpContextDescriptorGeneration(",
            "internal static ulong ComputeFrameOpContextRecordingFingerprint(");

        descriptorGeneration.ShouldContain("ComputeResourceRegistrySignature(registry)");
        descriptorGeneration.ShouldContain("return unchecked((ulong)(uint)ComputeResourceRegistrySignature(registry));");
    }

    [Test]
    public void CommandChainsAndSchedules_StoreTheSharedDependencySignature()
    {
        string chains = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "CommandRecordingDependencySignature DependencySignature");
        string lowering = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "chain.DependencySignature = BuildCommandChainDependencySignature(",
            "schedule.PublishDependencySignature(scheduleDependencySignature)",
            "affected-family");

        chains.ShouldContain("CommandRecordingDependencySignature DependencySignature");
        lowering.ShouldContain("chain.DependencySignature = BuildCommandChainDependencySignature(");
        lowering.ShouldContain("schedule.PublishDependencySignature(scheduleDependencySignature)");
        lowering.ShouldContain("affected-family");
        lowering.ShouldContain("affected-range");
    }

    [Test]
    public void TransientPrimary_PreservesCurrentSubmitMetadataUntilSubmissionCompletes()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string transientMarker = Slice(
            recording,
            "private static void MarkCommandBufferVariantTransient(",
            "private static void MarkCommandBufferVariantDirtyAfterConcurrentInvalidation(");

        transientMarker.ShouldContain("variant.Dirty = true");
        transientMarker.ShouldContain("variant.DirtyReason = reason");
        transientMarker.ShouldNotContain("RecordedFrameOpContextFingerprint =");
        transientMarker.ShouldNotContain("RecordedDependencySignature =");
        transientMarker.ShouldNotContain("RecordedGenerations =");
    }

    [Test]
    public void InvalidatedCommandBufferDrain_UsesCanonicalResetPredicate()
    {
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs");
        string drain = Slice(
            allocation,
            "private void DrainInvalidatedCommandBufferRecordings(",
            "private void AllocateCommandBufferDirtyFlags(");

        drain.ShouldContain("if (!CanResetVulkanCommandBuffer(commandBuffer, out _))");
        drain.ShouldNotContain("lifetime.QueuedSubmissionCount != 0");
        drain.ShouldNotContain("UpdateVulkanResourceCompletionState_NoLock(commandResource)");
    }

    [Test]
    public void OrdinaryDescriptorWrites_InvalidateRecordedDependentsBeforeReuse()
    {
        string descriptorSets = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSets.cs");
        string templates = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorUpdateTemplates.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs");

        descriptorSets.ShouldContain("TryCaptureDescriptorUpdateInvalidations_NoLock(");
        descriptorSets.ShouldContain("PublishDescriptorSetContentUpdate(");
        descriptorSets.ShouldContain("setState.UsesUpdateAfterBind");
        descriptorSets.ShouldContain("CanUseUpdateAfterBind(write.DescriptorType)");
        descriptorSets.ShouldContain("InvalidateCachedCommandBuffersByHandle(");
        templates.ShouldContain("ValidateAndRecordVulkanDescriptorWrites(");
        templates.ShouldContain("TryCaptureDescriptorUpdateInvalidations_NoLock(");
        recording.ShouldContain("SnapshotDescriptorSetContentUpdateGeneration()");
        recording.ShouldContain("HaveDescriptorSetContentsUpdatedSince(descriptorSetContentUpdateGeneration)");
        recording.ShouldContain("descriptor contents changed without UPDATE_AFTER_BIND");
        allocation.ShouldContain("ContainsCommandBufferHandle(");
        allocation.ShouldNotContain("HashSet<ulong> dependentHandles = new");
    }

    [Test]
    public void CapturedDrawBindings_SelectImmutableDescriptorAllocationVariants()
    {
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string drawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

		descriptors.ShouldContain("ComputeDispatchSnapshot? bindingSnapshot = null");
		descriptors.ShouldContain("if (refreshFrameIndex is { } validatedFrameIndex &&");
		descriptors.ShouldContain("TryActivatePublishedDescriptorOwnerGeneration(");
		descriptors.ShouldContain("if (resourcesCapturedByFrameSignature || refreshFrameIndex.HasValue)");
        descriptors.ShouldContain("ulong resourceFingerprint,");
        descriptors.ShouldContain("ComputeDispatchSnapshot? bindingSnapshot,");
        descriptors.ShouldContain("out int imageStart,");
        descriptors.ShouldContain("ComputeDispatchSnapshot? bindingSnapshot = null)");
        descriptors.ShouldContain("out DescriptorImageInfo info,");
        descriptors.ShouldContain("bindingSnapshot))");
        drawing.ShouldContain("EnsureDescriptorSets(material, drawUniformSlot, imageIndex, draw.ProgramBindingSnapshot)");
        drawing.ShouldContain("EnsureDescriptorSets(material, drawUniformSlot, frameIndex, draw.ProgramBindingSnapshot)");
        drawing.ShouldContain("capturedDescriptorResources,");
        drawing.ShouldContain("draw.ProgramBindingSnapshot,");
        drawing.ShouldContain("out string descriptorReason);");
    }

    [TestCase(0UL, 1UL, 256UL)]
    [TestCase(256UL, 128UL, 256UL)]
    [TestCase(256UL, 257UL, 512UL)]
    [TestCase(512UL, 1025UL, 2048UL)]
    public void ResizableBufferCapacity_GrowsOnlyOnOverflow(
        ulong currentCapacity,
        ulong requiredBytes,
        ulong expectedCapacity)
        => VkDataBuffer.ResolveResizableBufferCapacity(currentCapacity, requiredBytes)
            .ShouldBe(expectedCapacity);

    private static CommandRecordingDependencySignature CreateSignature()
        => new(
            OutputPassAttachment: 1UL,
            RenderArea: 2UL,
            ViewMask: 1u,
            QueueFamily: 0u,
            DynamicRenderingInheritance: 3UL,
            PipelineGeneration: 4UL,
            PipelineLayoutGeneration: 5UL,
            MeshBindingIdentity: 6UL,
            IndexBufferBindingIdentity: 7UL,
            VertexBufferBindingIdentity: 8UL,
            BufferAllocationGeneration: 9UL,
            ImageAllocationGeneration: 10UL,
            ImageViewGeneration: 11UL,
            SamplerAllocationGeneration: 12UL,
            DescriptorLayoutGeneration: 13UL,
            DescriptorSetGeneration: 14UL,
            ResourcePlanGeneration: 15UL,
            ExternalTargetVariant: 0u,
            FrameSlotVariant: 0,
            DescriptorPublicationGeneration: 16UL,
            DataPublicationGeneration: 17UL,
            VolatileSuffixGeneration: 18UL);

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadPartialType(relativePath);

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected start marker '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Expected end marker '{endMarker}'.");
        return source[start..end];
    }

    private static string ResolveRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate XRENGINE repository root.");
    }
}
