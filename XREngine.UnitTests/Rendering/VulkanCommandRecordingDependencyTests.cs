using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCommandRecordingDependencyTests
{
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
    public void DataPublicationMismatch_PreservesCompatibleRecording()
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
        mismatch.InvalidationClass.ShouldBe(CommandRecordingInvalidationClass.DataOnly);
        mismatch.RequiresRecording.ShouldBeFalse();
    }

    [Test]
    public void EveryDataOnlyGenerationPreservesCompatibleStaticRecording()
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

        for (int i = 0; i < updates.Length; i++)
        {
            CommandRecordingDependencyMismatch mismatch = recorded.Compare(updates[i]);
            mismatch.Field.ShouldBe(expectedFields[i]);
            mismatch.InvalidationClass.ShouldBe(CommandRecordingInvalidationClass.DataOnly);
            mismatch.RequiresRecording.ShouldBeFalse();
        }
    }

    [Test]
    public void ProductionPrimaryReuseDefaultsOnAndDiagnosticOverrideIsOptional()
    {
        VulkanRenderer.VulkanPrimaryCommandBufferReuseSafe.ShouldBeTrue();
        RuntimeRenderingHostServiceDefaults.EnableVulkanPrimaryCommandBufferReuse.ShouldBeTrue();
        new XREngine.VulkanCommandRecordingSettings().PrimaryCommandBufferReuseEnabled.ShouldBeTrue();

        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        source.ShouldContain("VulkanPrimaryCommandBufferReuseOverride ??");
        source.ShouldContain("RuntimeRenderingHostServices.Current.EnableVulkanPrimaryCommandBufferReuse");
        source.ShouldNotContain("VulkanPrimaryCommandBufferReuseSafe = false");
    }

    [Test]
    public void ReusableChainRefreshAdvancesDataOnlyDependencyBaseline()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");

        source.ShouldContain("chain.DependencySignature = BuildCommandChainDependencySignature(packet, chain.Key)");
    }

    [Test]
    public void DescriptorPublication_DoesNotMasqueradeAsSamplerAllocation()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string primaryDependency = Slice(
            recording,
            "private static CommandRecordingDependencySignature CaptureCommandRecordingDependencySignature(",
            "private static void CapturePreparedBindingIdentities(");
        primaryDependency.ShouldContain("SamplerAllocationGeneration: descriptorBindingIdentity");
        primaryDependency.ShouldNotContain("SamplerAllocationGeneration: generations.Descriptor");

        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
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
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        string frameOps = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string manifest = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineVariantManifest.cs");

        recording.ShouldContain("programHash.Add(draw.PreparedProgram?.BindingId ?? 0u);");
        lowering.ShouldContain("hash.Add(draw.Draw.PreparedProgram?.BindingId ?? 0u);");
        lowering.ShouldContain("MixSignature(descriptorSetSignature, preparedProgram.BindingId)");
        lowering.ShouldContain("? unchecked((int)preparedProgram.BindingId)");
        frameOps.ShouldContain("hash.Add(meshDraw.Draw.PreparedProgram?.BindingId ?? 0u);");
        manifest.ShouldContain("hash.Add(draw.PreparedProgram?.BindingId ?? 0u);");
    }

    [Test]
    public void PrimaryReuseCapability_IsEnabledByDependencyValidation()
        => VulkanRenderer.VulkanPrimaryCommandBufferReuseSafe.ShouldBeTrue();

    [Test]
    public void InlinePrimaryReuse_ReRecordsOnlyForOutputViewportCameraChanges()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string fastReuse = Slice(
            recording,
            "private bool TryReuseCleanCommandChainPrimaryVariant(",
            "private bool TryRefreshReusableCommandBufferFrameData(");
        string cameraPoseDirtyCheck = Slice(
            recording,
            "// An inline desktop primary owns the swapchain writer and must be re-recorded",
            "if (!dirty && IsCommandBufferVariantImageLayoutStateDirty");

        fastReuse.ShouldNotContain("variant.RecordedGenerations.CameraPose != currentGenerations.CameraPose");
        cameraPoseDirtyCheck.ShouldContain("variant.RecordedGenerations.CameraPose != currentGenerations.CameraPose");
        cameraPoseDirtyCheck.ShouldContain("!usingCommandChains");
    }

    [Test]
    public void CameraPoseReuseKey_IsIndependentOfVisibilityDrawOrdering()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
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
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
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
        string chains = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanCommandChains.cs");
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");

        chains.ShouldContain("CommandRecordingDependencySignature DependencySignature");
        lowering.ShouldContain("chain.DependencySignature = BuildCommandChainDependencySignature(packet, key)");
        lowering.ShouldContain("schedule.PublishDependencySignature(scheduleDependencySignature)");
        lowering.ShouldContain("affected-family");
        lowering.ShouldContain("affected-range");
    }

    [Test]
    public void TransientPrimary_PreservesCurrentSubmitMetadataUntilSubmissionCompletes()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
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
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs");
        string drain = Slice(
            allocation,
            "private void DrainInvalidatedCommandBufferRecordings(",
            "private void AllocateCommandBufferDirtyFlags(");

        drain.ShouldContain("if (!CanResetVulkanCommandBuffer(commandBuffer, out _))");
        drain.ShouldNotContain("lifetime.QueuedSubmissionCount != 0");
        drain.ShouldNotContain("UpdateVulkanResourceCompletionState_NoLock(commandResource)");
    }

    [TestCase(0UL, 1UL, 256UL)]
    [TestCase(256UL, 128UL, 256UL)]
    [TestCase(256UL, 257UL, 512UL)]
    [TestCase(512UL, 1025UL, 2048UL)]
    public void ResizableBufferCapacity_GrowsOnlyOnOverflow(
        ulong currentCapacity,
        ulong requiredBytes,
        ulong expectedCapacity)
        => VulkanRenderer.VkDataBuffer.ResolveResizableBufferCapacity(currentCapacity, requiredBytes)
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
    {
        string path = Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{relativePath}'.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

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
