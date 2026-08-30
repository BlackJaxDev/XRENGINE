using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class BackendReadyFramePackageTests
{
    [Test]
    public void PrepareThenSwap_PublishesSortedImmutablePackage()
    {
        const int opaquePass = (int)EDefaultRenderPass.OpaqueDeferred;
        const int transparentPass = (int)EDefaultRenderPass.TransparentForward;
        RenderCommandCollection commands = CreateCollection(transparentPass, opaquePass);
        RenderCommandMesh3D opaque = new(opaquePass);
        RenderCommandMesh3D transparent = new(transparentPass);
        BackendReadyFramePackageIdentity identity = CreateIdentity(collectGeneration: 7);

        commands.AddCPU(transparent);
        commands.AddCPU(opaque);
        commands.PrepareBackendReadyFramePackage(identity);
        commands.SwapBuffers();

        BackendReadyFramePackage package = commands.RenderingBackendReadyPackage;
        package.State.ShouldBe(EBackendReadyFramePackageState.Published);
        package.Identity.ShouldBe(identity);
        package.CommandCount.ShouldBe(2);
        package.MeshCommandCount.ShouldBe(2);
        package.Passes.Length.ShouldBe(2);
        package.Passes[0].PassIndex.ShouldBe(opaquePass);
        package.Passes[1].PassIndex.ShouldBe(transparentPass);
        package.CanonicalSubmissionCount.ShouldBe(0);
        package.TryGetPass(opaquePass, out BackendReadyRenderPass opaquePackage).ShouldBeTrue();
        opaquePackage.Commands.Single().ShouldBeSameAs(opaque);
    }

    [Test]
    public void MutationAfterPrepare_IsIncludedByLatePublicationRefresh()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueDeferred;
        RenderCommandCollection commands = CreateCollection(pass);
        RenderCommandMesh3D first = new(pass);
        RenderCommandMesh3D second = new(pass);

        commands.AddCPU(first);
        commands.PrepareBackendReadyFramePackage(CreateIdentity(collectGeneration: 1));
        commands.AddCPU(second);
        commands.SwapBuffers();

        BackendReadyFramePackage package = commands.RenderingBackendReadyPackage;
        package.CommandCount.ShouldBe(2);
        package.TryGetPass(pass, out BackendReadyRenderPass preparedPass).ShouldBeTrue();
        preparedPass.Commands.ShouldContain(first);
        preparedPass.Commands.ShouldContain(second);
    }

    [Test]
    public void PreparingNextFrame_DoesNotMutatePublishedPackage()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueForward;
        RenderCommandCollection commands = CreateCollection(pass);
        RenderCommandMesh3D first = new(pass);
        RenderCommandMesh3D second = new(pass);

        commands.AddCPU(first);
        commands.PrepareBackendReadyFramePackage(CreateIdentity(collectGeneration: 3));
        commands.SwapBuffers();
        BackendReadyFramePackage published = commands.RenderingBackendReadyPackage;
        long firstPackageGeneration = published.PackageGeneration;

        commands.AddCPU(second);
        commands.PrepareBackendReadyFramePackage(CreateIdentity(collectGeneration: 4));

        published.State.ShouldBe(EBackendReadyFramePackageState.Published);
        published.PackageGeneration.ShouldBe(firstPackageGeneration);
        published.CommandCount.ShouldBe(1);
        published.TryGetPass(pass, out BackendReadyRenderPass firstPass).ShouldBeTrue();
        firstPass.Commands.Single().ShouldBeSameAs(first);
    }

    [Test]
    public void VulkanResourcePlanner_ConsumesPublishedPackageMetadata()
    {
        string source = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.ResourcePlannerContext.cs");

        source.ShouldContain(
            "pipeline.ActiveMeshRenderCommands.RenderingBackendReadyPackage.PassMetadata");
    }

    [Test]
    public void WarmProducerAndPublicationPath_AllocatesNoManagedMemory()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueForward;
        RenderCommandCollection commands = CreateCollection(pass);
        RenderCommandMesh3D command = new(pass);
        BackendReadyFramePackageIdentity identity = CreateIdentity(collectGeneration: 1);

        for (int i = 0; i < 32; i++)
        {
            commands.AddCPU(command);
            commands.PrepareBackendReadyFramePackage(
                identity with { CollectGeneration = i + 1L });
            commands.SwapBuffers();
        }

        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
        {
            commands.AddCPU(command);
            commands.PrepareBackendReadyFramePackage(
                identity with { CollectGeneration = i + 100L });
            commands.SwapBuffers();
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        allocatedBytes.ShouldBe(0L);
    }

    [TestCase(
        8L,
        20UL,
        30,
        40,
        50,
        1920,
        EBackendReadyFramePackageValidationFailure.CollectGenerationMismatch)]
    [TestCase(
        7L,
        21UL,
        30,
        40,
        50,
        1920,
        EBackendReadyFramePackageValidationFailure.CommandGenerationMismatch)]
    [TestCase(
        7L,
        20UL,
        31,
        40,
        50,
        1920,
        EBackendReadyFramePackageValidationFailure.ResourceGenerationMismatch)]
    [TestCase(
        7L,
        20UL,
        30,
        41,
        50,
        1920,
        EBackendReadyFramePackageValidationFailure.DescriptorGenerationMismatch)]
    [TestCase(
        7L,
        20UL,
        30,
        40,
        51,
        1920,
        EBackendReadyFramePackageValidationFailure.RenderGraphGenerationMismatch)]
    [TestCase(
        7L,
        20UL,
        30,
        40,
        50,
        1280,
        EBackendReadyFramePackageValidationFailure.ViewportMismatch)]
    public void ValidatorRejectsStaleMutationAndResizeInputs(
        long consumedGeneration,
        ulong commandGeneration,
        int resourceGeneration,
        int descriptorGeneration,
        int renderGraphGeneration,
        int viewportWidth,
        EBackendReadyFramePackageValidationFailure expectedFailure)
    {
        const int pass = (int)EDefaultRenderPass.OpaqueForward;
        RenderCommandCollection commands = CreateCollection(pass);
        commands.AddCPU(new RenderCommandMesh3D(pass));
        commands.PrepareBackendReadyFramePackage(CreateIdentity(collectGeneration: 7L));
        commands.SwapBuffers();
        BackendReadyFramePackageValidationContext context = new(
            consumedGeneration,
            commandGeneration,
            resourceGeneration,
            descriptorGeneration,
            renderGraphGeneration,
            viewportWidth,
            1080,
            1600,
            900);

        BackendReadyFramePackageValidationResult result =
            BackendReadyFramePackageValidator.Validate(
                commands.RenderingBackendReadyPackage,
                in context);

        result.Accepted.ShouldBeFalse();
        result.Failure.ShouldBe(expectedFailure);
    }

    [Test]
    public void CancelTransitionsBothPackageBuffersOutOfConsumerOwnership()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueForward;
        RenderCommandCollection commands = CreateCollection(pass);
        commands.AddCPU(new RenderCommandMesh3D(pass));
        commands.PrepareBackendReadyFramePackage(CreateIdentity(collectGeneration: 2L));
        commands.SwapBuffers();

        commands.CancelBackendReadyFramePackages();

        commands.RenderingBackendReadyPackage.State.ShouldBe(
            EBackendReadyFramePackageState.Cancelled);
    }

    private static RenderCommandCollection CreateCollection(params int[] passes)
    {
        Dictionary<int, IComparer<RenderCommand>?> sorters = new(passes.Length);
        for (int i = 0; i < passes.Length; i++)
            sorters.Add(passes[i], new NearToFarRenderCommandSorter());
        return new RenderCommandCollection(sorters);
    }

    private static BackendReadyFramePackageIdentity CreateIdentity(long collectGeneration)
        => new(
            FrameId: 10UL,
            CollectGeneration: collectGeneration,
            CommandGeneration: 20UL,
            ResourceGeneration: 30,
            DescriptorGeneration: 40,
            RenderGraphGeneration: 50,
            ViewportWidth: 1920,
            ViewportHeight: 1080,
            InternalWidth: 1600,
            InternalHeight: 900);

}
