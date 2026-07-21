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
    public void PrimaryReuseCapability_IsEnabledByDependencyValidation()
        => VulkanRenderer.VulkanPrimaryCommandBufferReuseSafe.ShouldBeTrue();

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
