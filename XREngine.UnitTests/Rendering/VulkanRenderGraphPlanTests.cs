using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanRenderGraphPlanTests
{
    [Test]
    public void TopologicalSort_RejectsMissingExplicitDependency()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "Consumer").DependsOn(99);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => RenderGraphSynchronizationPlanner.TopologicallySort(metadata.Build()));

        exception.Message.ShouldContain("missing pass 99");
    }

    [Test]
    public void TopologicalSort_RejectsCycleWithDependencyChain()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "First").DependsOn(2);
        metadata.ForPass(2, "Second").DependsOn(1);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => RenderGraphSynchronizationPlanner.TopologicallySort(metadata.Build()));

        exception.Message.ShouldContain("dependency cycle");
        exception.Message.ShouldContain("First");
        exception.Message.ShouldContain("Second");
    }

    [Test]
    public void VersionedResource_RejectsUninitializedRead()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "Consumer")
            .UseVersionedResource("Lighting", ERenderPassResourceType.SampledTexture, ERenderGraphAccess.Read, 2);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => RenderGraphSynchronizationPlanner.TopologicallySort(metadata.Build()));

        exception.Message.ShouldContain("read before it is produced or imported");
    }

    [Test]
    public void VersionedResource_RejectsOverflowingSubresourceRange()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "InvalidRange")
            .UseVersionedResource(
                "Lighting",
                ERenderPassResourceType.StorageTexture,
                ERenderGraphAccess.Write,
                0,
                new RenderGraphSubresourceRange(uint.MaxValue, 2u, 0u, 1u));

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => RenderGraphSynchronizationPlanner.TopologicallySort(metadata.Build()));

        exception.Message.ShouldContain("invalid subresource range");
    }

    [Test]
    public void VersionedImportedResource_AcceptsValidInitialState()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "Consumer")
            .UseVersionedResource(
                "SwapchainColor",
                ERenderPassResourceType.SampledTexture,
                ERenderGraphAccess.Read,
                0,
                imported: true,
                importedInitialState: new RenderGraphSyncState(
                    RenderGraphStageMask.ColorAttachmentOutput,
                    RenderGraphAccessMask.MemoryRead,
                    RenderGraphImageLayout.Present));

        RenderGraphSynchronizationPlanner.TopologicallySort(metadata.Build()).Single().PassIndex.ShouldBe(1);
    }

    [Test]
    public void VersionedResource_DerivesProducerBeforeEarlierDeclaredConsumer()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(20, "Consumer")
            .UseVersionedResource("Lighting", ERenderPassResourceType.SampledTexture, ERenderGraphAccess.Read, 1);
        metadata.ForPass(10, "Producer")
            .UseVersionedResource("Lighting", ERenderPassResourceType.StorageTexture, ERenderGraphAccess.Write, 1);

        int[] order = RenderGraphSynchronizationPlanner.TopologicallySort(metadata.Build())
            .Select(static pass => pass.PassIndex)
            .ToArray();

        order.ShouldBe([10, 20]);
        RenderGraphSynchronizationInfo synchronization = RenderGraphSynchronizationPlanner.Build(metadata.Build());
        synchronization.Edges.ShouldContain(edge =>
            edge.ProducerPassIndex == 10 &&
            edge.ConsumerPassIndex == 20 &&
            edge.ResourceVersion == 1);
    }

    [Test]
    public void VersionedResource_AllowsDisjointProducersAndLinksOverlappingConsumer()
    {
        RenderGraphSubresourceRange mip0 = new(0u, 1u, 0u, 1u);
        RenderGraphSubresourceRange mip1 = new(1u, 1u, 0u, 1u);
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "Mip0Producer").UseVersionedResource(
            "Pyramid", ERenderPassResourceType.StorageTexture, ERenderGraphAccess.Write, 0, mip0);
        metadata.ForPass(2, "Mip1Producer").UseVersionedResource(
            "Pyramid", ERenderPassResourceType.StorageTexture, ERenderGraphAccess.Write, 0, mip1);
        metadata.ForPass(3, "Mip1Consumer").UseVersionedResource(
            "Pyramid", ERenderPassResourceType.SampledTexture, ERenderGraphAccess.Read, 0, mip1);

        RenderGraphSynchronizationInfo synchronization = RenderGraphSynchronizationPlanner.Build(metadata.Build());

        synchronization.Edges.ShouldContain(edge => edge.ProducerPassIndex == 2 && edge.ConsumerPassIndex == 3);
        synchronization.Edges.ShouldNotContain(edge => edge.ProducerPassIndex == 1 && edge.ConsumerPassIndex == 3);
    }

    [Test]
    public void VersionedResource_PartiallyOverlappingRangesProduceHazardEdge()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "Writer").UseVersionedResource(
            "Atlas", ERenderPassResourceType.StorageTexture, ERenderGraphAccess.Write, 0,
            new RenderGraphSubresourceRange(0u, 3u, 0u, 1u));
        metadata.ForPass(2, "Reader").UseVersionedResource(
            "Atlas", ERenderPassResourceType.SampledTexture, ERenderGraphAccess.Read, 0,
            new RenderGraphSubresourceRange(2u, 2u, 0u, 1u));

        RenderGraphSynchronizationEdge edge = RenderGraphSynchronizationPlanner.Build(metadata.Build()).Edges
            .Single(edge => edge.ProducerPassIndex == 1 && edge.ConsumerPassIndex == 2);
        edge.SubresourceRange.ShouldBe(new RenderGraphSubresourceRange(2u, 1u, 0u, 1u));
    }

    [Test]
    public void VersionedResource_RejectsOverlappingProducersForOneVersion()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "FirstWriter").UseVersionedResource(
            "Atlas", ERenderPassResourceType.StorageTexture, ERenderGraphAccess.Write, 3,
            new RenderGraphSubresourceRange(0u, 3u, 0u, 1u));
        metadata.ForPass(2, "OverlappingWriter").UseVersionedResource(
            "Atlas", ERenderPassResourceType.StorageTexture, ERenderGraphAccess.Write, 3,
            new RenderGraphSubresourceRange(2u, 2u, 0u, 1u));

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => RenderGraphSynchronizationPlanner.TopologicallySort(metadata.Build()));

        exception.Message.ShouldContain("overlapping producers");
        exception.Message.ShouldContain("passes 1 and 2");
    }

    [Test]
    public void CompiledPlan_HasStableStructuralIdentityAndDiagnosticDump()
    {
        VulkanRenderer.VulkanRenderGraphCompiler compiler = new();
        VulkanRenderer.VulkanCompiledRenderGraph first = compiler.Compile(CreateSimpleGraph());
        VulkanRenderer.VulkanCompiledRenderGraph second = compiler.Compile(CreateSimpleGraph());

        first.Plan.CompatibilityIdentity.ShouldBe(second.Plan.CompatibilityIdentity);
        first.Plan.Generation.ShouldNotBe(second.Plan.Generation);
        first.Plan.Dump().ShouldContain("pass order=0 id=1");
        first.Plan.Dump().ShouldContain("edge 1 -> 2 resource=SceneColor v0");
        first.Plan.Passes[0].Resources[0].StageMask.ShouldBe(RenderGraphStageMask.ColorAttachmentOutput);
        first.Plan.Passes[0].Resources[0].AccessMask.ShouldBe(RenderGraphAccessMask.ColorAttachmentWrite);
        first.Plan.Passes[0].Resources[0].Layout.ShouldBe(RenderGraphImageLayout.ColorAttachment);
        first.Plan.Submissions.Count.ShouldBeGreaterThan(0);
        first.Plan.Outputs.Single().ResourceName.ShouldBe("SceneColor");
    }

    [Test]
    public void CompiledPlanIdentity_IncludesImportedInitialState()
    {
        static IReadOnlyCollection<RenderPassMetadata> Create(RenderGraphImageLayout layout)
        {
            RenderPassMetadataCollection metadata = new();
            metadata.ForPass(1, "ImportedRead").UseVersionedResource(
                "External", ERenderPassResourceType.SampledTexture, ERenderGraphAccess.Read, 0,
                imported: true,
                importedInitialState: new RenderGraphSyncState(
                    RenderGraphStageMask.AllCommands,
                    RenderGraphAccessMask.MemoryRead,
                    layout));
            return metadata.Build();
        }

        VulkanRenderer.VulkanRenderGraphCompiler compiler = new();
        ulong present = compiler.Compile(Create(RenderGraphImageLayout.Present)).Plan.CompatibilityIdentity;
        ulong general = compiler.Compile(Create(RenderGraphImageLayout.General)).Plan.CompatibilityIdentity;

        present.ShouldNotBe(general);
    }

    [Test]
    public void QueueOwnershipPlan_RejectsIgnoredOwnerFamily()
    {
        VulkanBarrierPlanner.QueueOwnershipConfig config = new(Vk.QueueFamilyIgnored);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(config.Validate);

        exception.Message.ShouldContain("valid graphics queue family");
    }

    [TestCase(true, false, "compute owner")]
    [TestCase(false, true, "transfer owner")]
    public void QueueOwnershipPlan_RejectsIgnoredOptionalOwnerFamilies(
        bool ignoredCompute,
        bool ignoredTransfer,
        string expectedMessage)
    {
        VulkanBarrierPlanner.QueueOwnershipConfig config = new(
            GraphicsQueueFamilyIndex: 0u,
            ComputeQueueFamilyIndex: ignoredCompute ? Vk.QueueFamilyIgnored : 1u,
            TransferQueueFamilyIndex: ignoredTransfer ? Vk.QueueFamilyIgnored : 2u);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(config.Validate);

        exception.Message.ShouldContain(expectedMessage);
    }

    [Test]
    public void CompiledPlan_AutoVersionsLegacyWrites()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "InitialWrite").UseColorAttachment("History", ERenderGraphAccess.Write);
        metadata.ForPass(2, "Rewrite").UseColorAttachment("History", ERenderGraphAccess.Write);
        metadata.ForPass(3, "Read").SampleTexture("History");

        VulkanRenderer.VulkanCompiledRenderGraph graph = new VulkanRenderer.VulkanRenderGraphCompiler().Compile(metadata.Build());

        graph.Plan.Passes[0].Resources.Single().LogicalVersion.ShouldBe(0);
        graph.Plan.Passes[1].Resources.Single().LogicalVersion.ShouldBe(1);
        graph.Plan.Passes[2].Resources.Single().LogicalVersion.ShouldBe(1);
    }

    private static IReadOnlyCollection<RenderPassMetadata> CreateSimpleGraph()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "Opaque")
            .UseVersionedResource("SceneColor", ERenderPassResourceType.ColorAttachment, ERenderGraphAccess.Write, 0);
        metadata.ForPass(2, "PostProcess")
            .UseVersionedResource("SceneColor", ERenderPassResourceType.SampledTexture, ERenderGraphAccess.Read, 0);
        return metadata.Build();
    }
}
