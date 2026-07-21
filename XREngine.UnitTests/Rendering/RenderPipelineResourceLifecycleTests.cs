using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderPipelineResourceLifecycleTests
{
    [Test]
    public void LayoutBuilder_OrdersDependenciesBeforeFrameBuffers()
    {
        RenderPipelineResourceLayoutBuilder builder = new();

        builder.FrameBuffer("ColorFBO")
            .Color(0, "Color")
            .Factory(() => throw new NotSupportedException())
            .Add();

        builder.Texture("Color")
            .Size(RenderResourceSizePolicy.Internal())
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
            .Format(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
            .SizedFormat(ESizedInternalFormat.Rgba8)
            .Factory(() => throw new NotSupportedException())
            .Add();

        RenderPipelineResourceLayout layout = builder.Build(RenderPipelineResourceProfile.Empty);

        layout.OrderedSpecs.Select(x => x.Name).ToArray().ShouldBe(["Color", "ColorFBO"]);
    }

    [Test]
    public void LayoutBuilder_AllowsAttachmentlessQuadMaterialHelpers()
    {
        RenderPipelineResourceLayoutBuilder builder = new();

        builder.QuadMaterial("PostProcessQuad")
            .Factory(() => throw new NotSupportedException())
            .Add();

        RenderPipelineResourceLayout layout = builder.Build(RenderPipelineResourceProfile.Empty);

        QuadMaterialSpec spec = layout.OrderedSpecs.ShouldHaveSingleItem().ShouldBeOfType<QuadMaterialSpec>();
        spec.Name.ShouldBe("PostProcessQuad");
        spec.SizePolicy.ShouldBe(RenderResourceSizePolicy.Absolute(0u, 0u));
    }

    [Test]
    public void FrameBufferRegistry_DistinguishesAttachmentlessHelpersFromPhysicalTargets()
    {
        RenderResourceRegistry registry = new();
        RenderFrameBufferResource helper = registry.RegisterFrameBufferDescriptor(new FrameBufferResourceDescriptor(
            "PostProcessQuad",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(0u, 0u),
            Array.Empty<FrameBufferAttachmentDescriptor>()));
        RenderFrameBufferResource target = registry.RegisterFrameBufferDescriptor(new FrameBufferResourceDescriptor(
            "ColorFBO",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(64u, 32u),
            [new FrameBufferAttachmentDescriptor("Color", EFrameBufferAttachment.ColorAttachment0, 0, -1)]));

        helper.HasAttachments.ShouldBeFalse();
        target.HasAttachments.ShouldBeTrue();
    }

    [Test]
    public void LayoutBuilder_RecordsExternalOwnershipWithoutMaterializingIt()
    {
        RenderPipelineResourceLayoutBuilder builder = new();

        builder.External("$ExternalOutput")
            .Contract(
                ExternalRenderResourceKind.FrameBuffer,
                ExternalRenderResourceOwnership.XrRuntime,
                ExternalRenderResourceSynchronization.AcquireRelease)
            .Add();

        RenderPipelineResourceLayout layout = builder.Build(RenderPipelineResourceProfile.Empty);

        ExternalResourceSpec spec = layout.OrderedSpecs.ShouldHaveSingleItem().ShouldBeOfType<ExternalResourceSpec>();
        spec.Lifetime.ShouldBe(RenderResourceLifetime.External);
        spec.Ownership.ShouldBe(ExternalRenderResourceOwnership.XrRuntime);
        spec.Synchronization.ShouldBe(ExternalRenderResourceSynchronization.AcquireRelease);
        layout.LowerTextureDescriptors().ShouldBeEmpty();
        layout.LowerFrameBufferDescriptors().ShouldBeEmpty();
    }

    [Test]
    public void LayoutBuilder_LowersTextureAndViewDescriptorsWithoutLosingVulkanMetadata()
    {
        RenderPipelineResourceLayoutBuilder builder = new();

        builder.Texture("Color")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.TransferDestination)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Samples(4u)
            .Layers(2u)
            .Mips(new RenderResourceMipPolicy(0u, 3u, AutoGenerateMipmaps: true, RequireImmutableStorage: true))
            .Add();

        builder.TextureView("ColorView", "Color")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .MipRange(1u, 2u)
            .LayerRange(1u, 1u)
            .Target(array: true, multisample: true)
            .Add();

        Dictionary<string, TextureResourceDescriptor> descriptors = builder
            .Build(RenderPipelineResourceProfile.Empty)
            .LowerTextureDescriptors()
            .ToDictionary(static d => d.Name, StringComparer.OrdinalIgnoreCase);

        TextureResourceDescriptor color = descriptors["Color"];
        color.Kind.ShouldBe(RenderPipelineResourceKind.Texture);
        color.Usage.ShouldBe(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.TransferDestination);
        color.InternalFormat.ShouldBe(EPixelInternalFormat.Rgba16f);
        color.PixelFormat.ShouldBe(EPixelFormat.Rgba);
        color.PixelType.ShouldBe(EPixelType.HalfFloat);
        color.SizedInternalFormat.ShouldBe(ESizedInternalFormat.Rgba16f);
        color.Samples.ShouldBe(4u);
        color.ArrayLayers.ShouldBe(2u);
        color.MipPolicy.MipLevelCount.ShouldBe(3u);
        color.MipPolicy.AutoGenerateMipmaps.ShouldBeTrue();
        color.MipPolicy.RequireImmutableStorage.ShouldBeTrue();

        TextureResourceDescriptor view = descriptors["ColorView"];
        view.Kind.ShouldBe(RenderPipelineResourceKind.TextureView);
        view.SourceTextureName.ShouldBe("Color");
        view.BaseMipLevel.ShouldBe(1u);
        view.MipLevelCount.ShouldBe(2u);
        view.BaseLayer.ShouldBe(1u);
        view.LayerCount.ShouldBe(1u);
        view.ArrayTarget.ShouldBeTrue();
        view.Multisample.ShouldBeTrue();
    }

    [Test]
    public void Registry_BindTexturePreservesPredeclaredDescriptor()
    {
        RenderResourceRegistry registry = new();
        TextureResourceDescriptor plannedDescriptor = new(
            "Color",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(64u, 32u),
            FormatLabel: ESizedInternalFormat.Rgba16f.ToString(),
            SupportsAliasing: false,
            RequiresStorageUsage: true,
            Usage: RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.StorageImage,
            SizedInternalFormat: ESizedInternalFormat.Rgba16f,
            Samples: 4u,
            MipPolicy: new RenderResourceMipPolicy(0u, 3u));

        registry.RegisterTextureDescriptor(plannedDescriptor);

        XRTexture2D runtimeTexture = XRTexture2D.CreateFrameBufferTexture(
            64u,
            32u,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        runtimeTexture.Name = "Color";

        registry.BindTexture(runtimeTexture);

        TextureResourceDescriptor actual = registry.TextureRecords["Color"].Descriptor;
        actual.SizedInternalFormat.ShouldBe(ESizedInternalFormat.Rgba16f);
        actual.Usage.ShouldBe(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.StorageImage);
        actual.RequiresStorageUsage.ShouldBeTrue();
        actual.Samples.ShouldBe(4u);
        actual.MipPolicy.MipLevelCount.ShouldBe(3u);
    }

    [Test]
    public void Registry_BorrowedTextureIsDetachedWithoutBeingDestroyed()
    {
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        RenderResourceRegistry registry = new();
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            16u,
            16u,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Name = "ImportedTexture";

        try
        {
            registry.BindTexture(texture, ownsInstance: false);
            RenderTextureResource record = registry.TextureRecords[texture.Name];
            record.OwnsInstance.ShouldBeFalse();

            registry.DestroyAllPhysicalResources();

            texture.IsDestroyed.ShouldBeFalse();
            record.Instance.ShouldBeNull();
            registry.TextureRecords.ShouldNotContainKey(texture.Name);
        }
        finally
        {
            texture.Destroy(true);
        }
    }

    [Test]
    public void Registry_BorrowedBufferIsDetachedWithoutBeingDestroyed()
    {
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        RenderResourceRegistry registry = new();
        XRDataBuffer buffer = new(
            "ImportedBuffer",
            EBufferTarget.ShaderStorageBuffer,
            4u,
            EComponentType.Float,
            1,
            false,
            false);

        try
        {
            registry.BindBuffer(buffer, ownsInstance: false);
            RenderBufferResource record = registry.BufferRecords[buffer.AttributeName];
            record.OwnsInstance.ShouldBeFalse();

            registry.DestroyAllPhysicalResources();

            buffer.IsDestroyed.ShouldBeFalse();
            record.Instance.ShouldBeNull();
            registry.BufferRecords.ShouldNotContainKey(buffer.AttributeName);
        }
        finally
        {
            buffer.Destroy(true);
        }
    }

    [Test]
    public void Registry_DescriptorSignatureCachesUntilDescriptorsChange()
    {
        RenderResourceRegistry registry = new();
        TextureResourceDescriptor color = new(
            "Color",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(64u, 32u),
            FormatLabel: ESizedInternalFormat.Rgba8.ToString(),
            Usage: RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment,
            SizedInternalFormat: ESizedInternalFormat.Rgba8);

        registry.RegisterTextureDescriptor(color);
        int firstRevision = registry.DescriptorRevision;
        int firstSignature = registry.DescriptorSignature;

        registry.RegisterTextureDescriptor(color);
        registry.DescriptorRevision.ShouldBe(firstRevision);
        registry.DescriptorSignature.ShouldBe(firstSignature);

        FrameBufferResourceDescriptor fbo = new(
            "ColorFBO",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(64u, 32u),
            [new FrameBufferAttachmentDescriptor("Color", EFrameBufferAttachment.ColorAttachment0, 0, -1)]);
        registry.RegisterFrameBufferDescriptor(fbo);
        int fboRevision = registry.DescriptorRevision;
        int fboSignature = registry.DescriptorSignature;
        fboRevision.ShouldBeGreaterThan(firstRevision);

        FrameBufferResourceDescriptor equivalentFbo = new(
            "ColorFBO",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(64u, 32u),
            [new FrameBufferAttachmentDescriptor("Color", EFrameBufferAttachment.ColorAttachment0, 0, -1)]);
        registry.RegisterFrameBufferDescriptor(equivalentFbo);
        registry.DescriptorRevision.ShouldBe(fboRevision);
        registry.DescriptorSignature.ShouldBe(fboSignature);

        registry.RegisterTextureDescriptor(color with { Usage = color.Usage | RenderPipelineResourceUsage.StorageImage });
        registry.DescriptorRevision.ShouldBeGreaterThan(fboRevision);
        registry.DescriptorSignature.ShouldNotBe(fboSignature);
    }

    [Test]
    public void Registry_ExplicitTextureRebindUpdatesDescriptorSizeAndSignature()
    {
        RenderResourceRegistry registry = new();
        TextureResourceDescriptor halfResolutionGtao = new(
            "GTAORawTexture",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(1280u, 684u),
            FormatLabel: ESizedInternalFormat.R16f.ToString(),
            Usage: RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment,
            SizedInternalFormat: ESizedInternalFormat.R16f);

        registry.RegisterTextureDescriptor(halfResolutionGtao);
        int initialRevision = registry.DescriptorRevision;
        int initialSignature = registry.DescriptorSignature;

        XRTexture2D fullResolutionTexture = XRTexture2D.CreateFrameBufferTexture(
            2560u,
            1369u,
            EPixelInternalFormat.R16f,
            EPixelFormat.Red,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        fullResolutionTexture.Name = "GTAORawTexture";

        TextureResourceDescriptor fullResolutionGtao = halfResolutionGtao with
        {
            SizePolicy = RenderResourceSizePolicy.Absolute(2560u, 1369u)
        };
        registry.BindTexture(fullResolutionTexture, fullResolutionGtao);

        TextureResourceDescriptor actual = registry.TextureRecords["GTAORawTexture"].Descriptor;
        actual.SizePolicy.Width.ShouldBe(2560u);
        actual.SizePolicy.Height.ShouldBe(1369u);
        registry.DescriptorRevision.ShouldBeGreaterThan(initialRevision);
        registry.DescriptorSignature.ShouldNotBe(initialSignature);
    }

    [Test]
    public void Registry_ImplicitTextureRebindPreservesDeclaredScaledSizePolicy()
    {
        RenderResourceRegistry registry = new();
        TextureResourceDescriptor declared = new(
            "GTAORawTexture",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Internal(0.5f),
            FormatLabel: ESizedInternalFormat.R16f.ToString(),
            Usage: RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment,
            SizedInternalFormat: ESizedInternalFormat.R16f);
        registry.RegisterTextureDescriptor(declared);

        XRTexture2D concrete = XRTexture2D.CreateFrameBufferTexture(
            448u,
            504u,
            EPixelInternalFormat.R16f,
            EPixelFormat.Red,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        concrete.Name = declared.Name;

        registry.BindTexture(concrete);

        TextureResourceDescriptor actual = registry.TextureRecords[declared.Name].Descriptor;
        actual.ShouldBe(declared);
    }

    [Test]
    public void RenderPassMetadata_RevisionsAndCachedViewsChangeOnlyOnMutation()
    {
        RenderPassMetadataCollection collection = new();
        RenderPassBuilder passBuilder = collection.ForPass(3, "Main", ERenderGraphPassStage.Graphics);
        RenderPassMetadata pass = collection.Build().Single();

        int initialRevision = pass.Revision;
        var initialDependencies = pass.ExplicitDependencies;
        var initialSchemas = pass.DescriptorSchemas;
        var resourceUsages = pass.ResourceUsages;

        pass.ExplicitDependencies.ShouldBeSameAs(initialDependencies);
        pass.DescriptorSchemas.ShouldBeSameAs(initialSchemas);
        pass.ResourceUsages.ShouldBeSameAs(resourceUsages);

        passBuilder.UseEngineDescriptors();
        pass.Revision.ShouldBe(initialRevision);
        pass.DescriptorSchemas.ShouldBeSameAs(initialSchemas);

        passBuilder.DependsOn(1);
        pass.Revision.ShouldBeGreaterThan(initialRevision);
        pass.ExplicitDependencies.ShouldNotBeSameAs(initialDependencies);
        pass.ExplicitDependencies.ShouldBe([1]);

        int dependencyRevision = pass.Revision;
        passBuilder.SampleTexture("Color");
        pass.Revision.ShouldBeGreaterThan(dependencyRevision);
        pass.ResourceUsages.ShouldBeSameAs(resourceUsages);
        pass.ResourceUsages.Count.ShouldBe(1);
    }

    [Test]
    public void RenderGraphTopologicalSort_UsesDeclarationOrderForReadyPassTieBreaks()
    {
        RenderPassMetadataCollection metadata = new();
        int background = (int)EDefaultRenderPass.Background;
        int onTop = (int)EDefaultRenderPass.OnTopForward;
        const int temporalResolve = 100000;

        metadata.ForPass(background, EDefaultRenderPass.Background.ToString(), ERenderGraphPassStage.Graphics);
        metadata.ForPass(temporalResolve, "Temporal_AccumulationResolve", ERenderGraphPassStage.Graphics);
        metadata.ForPass(onTop, EDefaultRenderPass.OnTopForward.ToString(), ERenderGraphPassStage.Graphics)
            .DependsOn(background);

        int[] orderedPasses = RenderGraphSynchronizationPlanner
            .TopologicallySort(metadata.Build())
            .Select(static pass => pass.PassIndex)
            .ToArray();

        orderedPasses.ShouldBe([background, temporalResolve, onTop]);
    }

    [Test]
    public void FullOverdrawMetadata_UsesSyntheticPassesWithoutPollutingSourcePasses()
    {
        const string forwardFbo = "ForwardPassFBO";
        const string fullOverdrawFbo = "FullOverdrawCountFBO";
        int onTopPassIndex = (int)EDefaultRenderPass.OnTopForward;

        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(onTopPassIndex, EDefaultRenderPass.OnTopForward.ToString(), ERenderGraphPassStage.Graphics)
            .UseColorAttachment(RenderGraphResourceNames.MakeFboColor(forwardFbo))
            .UseDepthAttachment(RenderGraphResourceNames.MakeFboDepth(forwardFbo));

        ViewportRenderCommandContainer container = new();
        using (container.AddUsing<VPRC_BindFBOByName>(x =>
            x.SetOptions(fullOverdrawFbo, write: true, clearColor: true, clearDepth: false, clearStencil: false)))
        {
            container.Add<VPRC_RenderFullOverdrawPass>().RenderPasses = [onTopPassIndex];
        }

        container.BuildRenderPassMetadata(metadata);

        RenderPassMetadata[] built = metadata.Build().ToArray();
        RenderPassMetadata onTop = built.Single(pass => pass.PassIndex == onTopPassIndex);
        onTop.Name.ShouldBe(EDefaultRenderPass.OnTopForward.ToString());
        onTop.ResourceUsages.ShouldNotContain(usage =>
            usage.ResourceName.Contains(fullOverdrawFbo, StringComparison.OrdinalIgnoreCase));

        RenderPassMetadata fullOverdraw = built.Single(pass => pass.Name == "FullOverdraw_OnTopForward");
        fullOverdraw.PassIndex.ShouldNotBe(onTopPassIndex);
        fullOverdraw.ResourceUsages.ShouldContain(usage =>
            usage.ResourceType == ERenderPassResourceType.ColorAttachment &&
            usage.ResourceName == RenderGraphResourceNames.MakeFboColor(fullOverdrawFbo));
    }

    [Test]
    public void VulkanPlanner_AllocatesSourceTexturesAndResolvesViewsToPhysicalGroups()
    {
        RenderResourceRegistry registry = new();
        registry.RegisterTextureDescriptor(new TextureResourceDescriptor(
            "DepthStencil",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            FormatLabel: ESizedInternalFormat.Depth24Stencil8.ToString(),
            Usage: RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.DepthStencilAttachment | RenderPipelineResourceUsage.TransferDestination,
            SizedInternalFormat: ESizedInternalFormat.Depth24Stencil8,
            Samples: 4u,
            MipPolicy: new RenderResourceMipPolicy(0u, 3u)));

        registry.RegisterTextureDescriptor(new TextureResourceDescriptor(
            "DepthView",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            FormatLabel: ESizedInternalFormat.Depth24Stencil8.ToString(),
            ArrayLayers: 1u,
            Kind: RenderPipelineResourceKind.TextureView,
            Usage: RenderPipelineResourceUsage.SampledTexture,
            SizedInternalFormat: ESizedInternalFormat.Depth24Stencil8,
            SourceTextureName: "DepthStencil",
            MipLevelCount: 1u,
            LayerCount: 1u,
            DepthStencilAspect: EDepthStencilFmt.Depth,
            Multisample: true));

        registry.RegisterFrameBufferDescriptor(new FrameBufferResourceDescriptor(
            "DepthFBO",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            [new FrameBufferAttachmentDescriptor("DepthView", EFrameBufferAttachment.DepthAttachment, 0, -1)]));

        VulkanResourcePlanner planner = new();
        planner.Sync(registry);

        planner.TryGetTextureDescriptor("DepthView", out _).ShouldBeTrue();
        planner.TryGetPhysicalTextureDescriptor("DepthView", out _).ShouldBeFalse();
        planner.CurrentPlan.AllTextures().Select(static x => x.Name).ShouldBe(["DepthStencil"]);
        planner.ResolveImageResourceName("DepthView").ShouldBe("DepthStencil");

        VulkanResourceAllocator allocator = new();
        allocator.UpdatePlan(planner.CurrentPlan);
        allocator.RebuildPhysicalPlan(null!, null, planner);

        VulkanPhysicalImageGroup group = allocator.EnumeratePhysicalGroups().Single();
        group.Format.ShouldBe(Format.D24UnormS8Uint);
        group.Samples.ShouldBe(SampleCountFlags.Count4Bit);
        group.MipLevels.ShouldBe(1u);
        group.Usage.HasFlag(ImageUsageFlags.DepthStencilAttachmentBit).ShouldBeTrue();
        group.Usage.HasFlag(ImageUsageFlags.SampledBit).ShouldBeTrue();

        allocator.TryGetPhysicalGroupForResource("DepthView", out VulkanPhysicalImageGroup? viewGroup).ShouldBeTrue();
        viewGroup.ShouldBeSameAs(group);
    }

    [Test]
    public void VulkanPlanner_PersistentColorTargetsKeepStablePhysicalUsageAcrossMetadataChanges()
    {
        RenderResourceRegistry registry = new();
        registry.RegisterTextureDescriptor(new TextureResourceDescriptor(
            "FinalColor",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            FormatLabel: ESizedInternalFormat.Rgba16f.ToString(),
            Usage: RenderPipelineResourceUsage.ColorAttachment,
            SizedInternalFormat: ESizedInternalFormat.Rgba16f));

        registry.RegisterFrameBufferDescriptor(new FrameBufferResourceDescriptor(
            "FinalColorFBO",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            [new FrameBufferAttachmentDescriptor("FinalColor", EFrameBufferAttachment.ColorAttachment0, 0, -1)]));

        VulkanResourcePlanner planner = new();
        planner.Sync(registry);

        RenderPassMetadataCollection attachmentOnlyMetadata = new();
        attachmentOnlyMetadata.ForPass(1, "FinalWrite", ERenderGraphPassStage.Graphics)
            .UseColorAttachment("fbo::FinalColorFBO::color");

        RenderPassMetadataCollection sampledMetadata = new();
        sampledMetadata.ForPass(1, "FinalWrite", ERenderGraphPassStage.Graphics)
            .UseColorAttachment("fbo::FinalColorFBO::color");
        sampledMetadata.ForPass(2, "Present", ERenderGraphPassStage.Graphics)
            .SampleTexture("tex::FinalColor");

        int attachmentOnlySignature = VulkanResourceAllocator.ComputePhysicalPlanUsageSignature(planner, attachmentOnlyMetadata.Build());
        int sampledSignature = VulkanResourceAllocator.ComputePhysicalPlanUsageSignature(planner, sampledMetadata.Build());
        sampledSignature.ShouldBe(attachmentOnlySignature);

        VulkanResourceAllocator allocator = new();
        allocator.UpdatePlan(planner.CurrentPlan);
        allocator.RebuildPhysicalPlan(null!, attachmentOnlyMetadata.Build(), planner);

        VulkanPhysicalImageGroup group = allocator.EnumeratePhysicalGroups().Single();
        group.Usage.HasFlag(ImageUsageFlags.ColorAttachmentBit).ShouldBeTrue();
        group.Usage.HasFlag(ImageUsageFlags.SampledBit).ShouldBeTrue();
    }

    [Test]
    public void VulkanAllocator_ReusedImageMetadataChangesOnlyAtCommit()
    {
        VulkanResourcePlanner activePlanner = CreateSingleTexturePlanner("SharedColor", EPixelFormat.Rgba);
        VulkanResourceAllocator activeAllocator = new();
        activeAllocator.UpdatePlan(activePlanner.CurrentPlan);
        activeAllocator.RebuildPhysicalPlan(null!, null, activePlanner);
        VulkanPhysicalImageGroup activeGroup = activeAllocator.EnumeratePhysicalGroups().Single();
        typeof(VulkanPhysicalImageGroup).GetField("_allocated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(activeGroup, true);

        VulkanResourcePlanner pendingPlanner = CreateSingleTexturePlanner("SharedColor", EPixelFormat.Bgra);
        VulkanResourceAllocator pendingAllocator = new();
        pendingAllocator.UpdatePlan(pendingPlanner.CurrentPlan);
        pendingAllocator.RebuildPhysicalPlan(null!, null, pendingPlanner);

        pendingAllocator.ReuseCompatiblePhysicalImagesFrom(activeAllocator, out _).ShouldBe(1);
        activeGroup.LogicalResources.Single().Descriptor.PixelFormat.ShouldBe(EPixelFormat.Rgba);
        pendingAllocator.TryGetPhysicalGroupForResource("SharedColor", out VulkanPhysicalImageGroup? reusedGroup)
            .ShouldBeTrue();
        reusedGroup.ShouldBeSameAs(activeGroup);

        pendingAllocator.CommitReusedPhysicalImageMetadata();

        activeGroup.LogicalResources.Single().Descriptor.PixelFormat.ShouldBe(EPixelFormat.Bgra);
    }

    [Test]
    public void VulkanBarrierPlanner_CoalescesSampledReadOnlyDepthByPhysicalSubresource()
    {
        RenderResourceRegistry registry = new();
        registry.RegisterTextureDescriptor(new TextureResourceDescriptor(
            "DepthStencil",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            FormatLabel: ESizedInternalFormat.Depth24Stencil8.ToString(),
            Usage: RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.DepthStencilAttachment,
            SizedInternalFormat: ESizedInternalFormat.Depth24Stencil8));

        registry.RegisterTextureDescriptor(new TextureResourceDescriptor(
            "DepthView",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            FormatLabel: ESizedInternalFormat.Depth24Stencil8.ToString(),
            Kind: RenderPipelineResourceKind.TextureView,
            Usage: RenderPipelineResourceUsage.SampledTexture,
            SizedInternalFormat: ESizedInternalFormat.Depth24Stencil8,
            SourceTextureName: "DepthStencil",
            DepthStencilAspect: EDepthStencilFmt.Depth));

        registry.RegisterFrameBufferDescriptor(new FrameBufferResourceDescriptor(
            "PostProcessOutputFBO",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            [new FrameBufferAttachmentDescriptor("DepthStencil", EFrameBufferAttachment.DepthStencilAttachment, 0, -1)]));

        VulkanResourcePlanner resourcePlanner = new();
        resourcePlanner.Sync(registry);

        VulkanResourceAllocator allocator = new();
        allocator.UpdatePlan(resourcePlanner.CurrentPlan);
        allocator.RebuildPhysicalPlan(null!, null, resourcePlanner);

        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(100, "PostProcess", ERenderGraphPassStage.Graphics)
            .SampleTexture("tex::DepthView")
            .UseDepthAttachment("fbo::PostProcessOutputFBO::depth", ERenderGraphAccess.Read);
        metadata.ForPass(101, "Tsr", ERenderGraphPassStage.Graphics)
            .SampleTexture("tex::DepthView");

        VulkanBarrierPlanner barrierPlanner = new();
        barrierPlanner.Rebuild(metadata.Build(), resourcePlanner, allocator);

        barrierPlanner.ImageBarriers.ShouldContain(static barrier =>
            barrier.PassIndex == 100 &&
            barrier.ResourceName.Equals("DepthStencil", StringComparison.OrdinalIgnoreCase) &&
            barrier.Next.Layout == ImageLayout.DepthStencilReadOnlyOptimal &&
            (barrier.Next.AccessMask & AccessFlags.ShaderReadBit) != 0 &&
            (barrier.Next.AccessMask & AccessFlags.DepthStencilAttachmentReadBit) != 0);
        barrierPlanner.ImageBarriers.ShouldNotContain(static barrier =>
            barrier.PassIndex == 101 &&
            barrier.Previous.Layout == ImageLayout.DepthStencilAttachmentOptimal);
    }

    [Test]
    public void VulkanBarrierPlanner_RejectsSampledWritableDepthInOnePass()
    {
        RenderResourceRegistry registry = new();
        registry.RegisterTextureDescriptor(new TextureResourceDescriptor(
            "DepthStencil",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            FormatLabel: ESizedInternalFormat.Depth24Stencil8.ToString(),
            Usage: RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.DepthStencilAttachment,
            SizedInternalFormat: ESizedInternalFormat.Depth24Stencil8));
        registry.RegisterFrameBufferDescriptor(new FrameBufferResourceDescriptor(
            "DepthFBO",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            [new FrameBufferAttachmentDescriptor("DepthStencil", EFrameBufferAttachment.DepthStencilAttachment, 0, -1)]));

        VulkanResourcePlanner resourcePlanner = new();
        resourcePlanner.Sync(registry);
        VulkanResourceAllocator allocator = new();
        allocator.UpdatePlan(resourcePlanner.CurrentPlan);
        allocator.RebuildPhysicalPlan(null!, null, resourcePlanner);

        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(100, "InvalidDepthFeedback", ERenderGraphPassStage.Graphics)
            .SampleTexture("tex::DepthStencil")
            .UseDepthAttachment("fbo::DepthFBO::depth", ERenderGraphAccess.Write);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new VulkanBarrierPlanner().Rebuild(metadata.Build(), resourcePlanner, allocator));
        exception.Message.ShouldContain("samples and writes depth image");
    }

    [Test]
    public void QuadBlit_PostProcessOutputMetadata_SamplesDepthStencilWithoutAttachingDestinationDepth()
    {
        RenderPassMetadataCollection metadata = new();
        RenderGraphDescribeContext context = new(metadata);

        new VPRC_RenderQuadToFBO()
            .SetTargets(DefaultRenderPipeline.PostProcessFBOName, DefaultRenderPipeline.PostProcessOutputFBOName)
            .ConfigureRenderGraphResources(static resources => resources
                .SampleTexture(DefaultRenderPipeline.DepthViewTextureName)
                .SampleTexture(DefaultRenderPipeline.StencilViewTextureName))
            .DescribeRenderPass(context);

        RenderPassMetadata pass = metadata.Build().Single(static x =>
            string.Equals(x.Name, "QuadBlit_PostProcessFBO_to_PostProcessOutputFBO", StringComparison.Ordinal));

        pass.ResourceUsages.ShouldContain(static x =>
            x.ResourceType == ERenderPassResourceType.SampledTexture &&
            x.ResourceName == "tex::DepthView");
        pass.ResourceUsages.ShouldContain(static x =>
            x.ResourceType == ERenderPassResourceType.SampledTexture &&
            x.ResourceName == "tex::StencilView");
        pass.ResourceUsages.ShouldNotContain(static x =>
            x.ResourceType == ERenderPassResourceType.DepthAttachment ||
            x.ResourceType == ERenderPassResourceType.StencilAttachment);
    }

    [Test]
    public void LateDebugOverlay_PostProcessOutputMetadata_IsColorOnly()
    {
        RenderPassMetadataCollection metadata = new();
        RenderGraphDescribeContext context = new(metadata);

        context.PushRenderTarget(DefaultRenderPipeline.PostProcessOutputFBOName, writes: true, clearColor: false, clearDepth: false, clearStencil: false);
        new VPRC_RenderDebugShapes { RenderGraphPassName = "LateDebugOverlay" }
            .DescribeRenderPass(context);
        context.PopRenderTarget();

        RenderPassMetadata pass = metadata.Build().Single(static x =>
            string.Equals(x.Name, "LateDebugOverlay", StringComparison.Ordinal));

        pass.ResourceUsages.ShouldContain(static x =>
            x.ResourceType == ERenderPassResourceType.ColorAttachment &&
            x.ResourceName == "fbo::PostProcessOutputFBO::color");
        pass.ResourceUsages.ShouldNotContain(static x =>
            x.ResourceType == ERenderPassResourceType.DepthAttachment ||
            x.ResourceType == ERenderPassResourceType.StencilAttachment);
    }

    [Test]
    public void LayoutBuilder_RejectsMissingFrameBufferAttachment()
    {
        RenderPipelineResourceLayoutBuilder builder = new();
        builder.FrameBuffer("BrokenFBO")
            .Color(0, "MissingColor")
            .Factory(() => throw new NotSupportedException())
            .Add();

        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => builder.Build(RenderPipelineResourceProfile.Empty));
        ex.Message.ShouldContain("BrokenFBO");
        ex.Message.ShouldContain("MissingColor");
    }

    [Test]
    public void DefaultRenderPipeline_DefaultMonoLayout_DeclaresCoreGraphResources()
    {
        DefaultRenderPipeline pipeline = new();
        RenderPipelineResourceLayout layout = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, msaaSamples: 1u));

        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.DepthStencilTextureName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.DepthViewTextureName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.StencilViewTextureName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.DeferredGBufferFBOName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.ForwardPassFBOName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.LightCombineFBOName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.PostProcessOutputFBOName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.FinalPostProcessOutputFBOName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.FxaaFBOName);
        layout.ResourcesByName.Keys.ShouldNotContain(DefaultRenderPipeline.TsrUpscaleFBOName);

        FrameBufferSpec postProcessOutput = layout.ResourcesByName[DefaultRenderPipeline.PostProcessOutputFBOName].ShouldBeOfType<FrameBufferSpec>();
        postProcessOutput.Attachments.Select(static x => x.ResourceName).ToArray().ShouldBe([
            DefaultRenderPipeline.PostProcessOutputTextureName
        ]);
        postProcessOutput.Attachments.ShouldNotContain(static x =>
            x.Attachment == EFrameBufferAttachment.DepthAttachment ||
            x.Attachment == EFrameBufferAttachment.DepthStencilAttachment ||
            x.Attachment == EFrameBufferAttachment.StencilAttachment);

        FrameBufferSpec gBuffer = layout.ResourcesByName[DefaultRenderPipeline.DeferredGBufferFBOName].ShouldBeOfType<FrameBufferSpec>();
        gBuffer.Attachments.Select(x => x.ResourceName).ToArray().ShouldBe([
            DefaultRenderPipeline.AlbedoOpacityTextureName,
            DefaultRenderPipeline.NormalTextureName,
            DefaultRenderPipeline.RMSETextureName,
            DefaultRenderPipeline.TransformIdTextureName,
            DefaultRenderPipeline.DepthStencilTextureName
        ]);
    }

    [Test]
    public void DefaultRenderPipeline_StereoTsrLayout_DescriptorFactoriesAndFbosUseTwoLayerMultiviewShapes()
    {
        const ulong motionBlur = 1UL << 10;
        const ulong depthOfField = 1UL << 11;
        const ulong bloom = 1UL << 14;
        const ulong temporal = 1UL << 15;
        DefaultRenderPipeline pipeline = new(stereo: true);
        RenderPipelineResourceLayout layout = pipeline.BuildResourceLayout(CreateProfile(
            EAntiAliasingMode.Tsr,
            msaaSamples: 1u,
            featureMask: motionBlur | depthOfField | bloom | temporal,
            stereo: true));

        string[] stereoPostProcessTextures =
        [
            DefaultRenderPipeline.PostProcessOutputTextureName,
            DefaultRenderPipeline.FinalPostProcessOutputTextureName,
            DefaultRenderPipeline.BloomBlurTextureName,
            DefaultRenderPipeline.VelocityTextureName,
            DefaultRenderPipeline.HistoryColorTextureName,
            DefaultRenderPipeline.HistoryDepthStencilTextureName,
            DefaultRenderPipeline.TemporalColorInputTextureName,
            DefaultRenderPipeline.TemporalExposureVarianceTextureName,
            DefaultRenderPipeline.HistoryExposureVarianceTextureName,
            DefaultRenderPipeline.MotionBlurTextureName,
            DefaultRenderPipeline.DepthOfFieldTextureName,
            DefaultRenderPipeline.TsrOutputTextureName,
            DefaultRenderPipeline.TsrHistoryColorTextureName,
        ];

        foreach (string textureName in stereoPostProcessTextures)
        {
            TextureSpec spec = layout.ResourcesByName[textureName].ShouldBeOfType<TextureSpec>();
            spec.Layers.ShouldBe(2u, textureName);
            spec.StereoCompatible.ShouldBeTrue(textureName);
            spec.Factory.ShouldNotBeNull(textureName);

            XRTexture2DArray texture = spec.Factory!().ShouldBeOfType<XRTexture2DArray>(textureName);
            texture.Depth.ShouldBe(2u, textureName);
            texture.OVRMultiViewParameters.ShouldNotBeNull(textureName);
            texture.OVRMultiViewParameters!.Offset.ShouldBe(0, textureName);
            texture.OVRMultiViewParameters.NumViews.ShouldBe(2u, textureName);
        }

        TextureSpec bloomSpec = layout.ResourcesByName[DefaultRenderPipeline.BloomBlurTextureName]
            .ShouldBeOfType<TextureSpec>();
        bloomSpec.MipPolicy.MipLevelCount.ShouldBe(5u);

        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.PostProcessOutputFBOName, DefaultRenderPipeline.PostProcessOutputTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.FinalPostProcessOutputFBOName, DefaultRenderPipeline.FinalPostProcessOutputTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.HistoryCaptureFBOName, DefaultRenderPipeline.HistoryColorTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.TemporalInputFBOName, DefaultRenderPipeline.TemporalColorInputTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.TemporalAccumulationFBOName, DefaultRenderPipeline.TemporalExposureVarianceTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.HistoryExposureFBOName, DefaultRenderPipeline.HistoryExposureVarianceTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.TsrUpscaleFBOName, DefaultRenderPipeline.TsrOutputTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.TsrHistoryColorFBOName, DefaultRenderPipeline.TsrHistoryColorTextureName);
    }

    [Test]
    public void DefaultRenderPipeline_StereoFxaaLayout_DescriptorFactoryAndFboUseTwoLayerMultiviewShape()
    {
        DefaultRenderPipeline pipeline = new(stereo: true);
        RenderPipelineResourceLayout layout = pipeline.BuildResourceLayout(CreateProfile(
            EAntiAliasingMode.Fxaa,
            msaaSamples: 1u,
            stereo: true));

        AssertStereoTextureFactory(layout, DefaultRenderPipeline.FxaaOutputTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.FxaaFBOName, DefaultRenderPipeline.FxaaOutputTextureName);
    }

    [Test]
    public void DefaultRenderPipeline_StereoSmaaLayout_AllDescriptorsFactoriesAndFbosUseTwoLayerMultiviewShapes()
    {
        DefaultRenderPipeline pipeline = new(stereo: true);
        RenderPipelineResourceLayout layout = pipeline.BuildResourceLayout(CreateProfile(
            EAntiAliasingMode.Smaa,
            msaaSamples: 1u,
            stereo: true));

        AssertStereoTextureFactory(layout, DefaultRenderPipeline.SmaaEdgeTextureName);
        AssertStereoTextureFactory(layout, DefaultRenderPipeline.SmaaBlendTextureName);
        AssertStereoTextureFactory(layout, DefaultRenderPipeline.SmaaOutputTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.SmaaEdgeFBOName, DefaultRenderPipeline.SmaaEdgeTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.SmaaBlendFBOName, DefaultRenderPipeline.SmaaBlendTextureName);
        AssertStereoFramebufferAttachment(layout, DefaultRenderPipeline.SmaaFBOName, DefaultRenderPipeline.SmaaOutputTextureName);
    }

    [Test]
    public void DefaultRenderPipeline_StereoPostProcessFallbacksRemainArraySamplers()
    {
        const ulong openXrVulkanSafePath = 1UL << 12;
        DefaultRenderPipeline pipeline = new(stereo: true);
        RenderPipelineResourceLayout layout = pipeline.BuildResourceLayout(CreateProfile(
            EAntiAliasingMode.Fxaa,
            msaaSamples: 1u,
            featureMask: openXrVulkanSafePath,
            stereo: true));

        string[] fallbackNames =
        [
            DefaultRenderPipeline.BloomBlurTextureName,
            DefaultRenderPipeline.AtmosphereColorTextureName,
            DefaultRenderPipeline.VolumetricFogColorTextureName,
        ];
        foreach (string name in fallbackNames)
        {
            TextureSpec spec = layout.ResourcesByName[name].ShouldBeOfType<TextureSpec>();
            spec.Layers.ShouldBe(2u, name);
            spec.StereoCompatible.ShouldBeTrue(name);
            XRTexture2DArray texture = spec.Factory!().ShouldBeOfType<XRTexture2DArray>(name);
            texture.Depth.ShouldBe(2u, name);
            texture.OVRMultiViewParameters.ShouldNotBeNull(name);
            texture.OVRMultiViewParameters!.NumViews.ShouldBe(2u, name);
        }
    }

    [TestCase(typeof(DefaultRenderPipeline))]
    [TestCase(typeof(DefaultRenderPipeline2))]
    public void DefaultPipelines_BloomLayoutDeclaresEveryMipFrameBuffer(Type pipelineType)
    {
        const ulong bloomResources = 1UL << 14;
        RenderPipeline pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;
        RenderPipelineResourceLayout layout = pipeline.BuildResourceLayout(CreateProfile(
            EAntiAliasingMode.Fxaa,
            1u,
            featureMask: bloomResources));

        (string Name, int MipLevel)[] expected =
        [
            (VPRC_BloomPass.BloomMip0FBOName, 0),
            (VPRC_BloomPass.BloomDS1FBOName, 1),
            (VPRC_BloomPass.BloomDS2FBOName, 2),
            (VPRC_BloomPass.BloomDS3FBOName, 3),
            (VPRC_BloomPass.BloomDS4FBOName, 4),
            (VPRC_BloomPass.BloomUS3FBOName, 3),
            (VPRC_BloomPass.BloomUS2FBOName, 2),
            (VPRC_BloomPass.BloomUS1FBOName, 1),
        ];

        foreach ((string name, int mipLevel) in expected)
        {
            FrameBufferSpec frameBuffer = layout.ResourcesByName[name].ShouldBeOfType<FrameBufferSpec>();
            FrameBufferAttachmentDescriptor attachment = frameBuffer.Attachments.ShouldHaveSingleItem();
            attachment.ResourceName.ShouldBe("BloomBlurTexture");
            attachment.MipLevel.ShouldBe(mipLevel, name);
            attachment.LayerIndex.ShouldBe(-1, name);
            frameBuffer.Factory.ShouldNotBeNull(name);
        }
    }

    [TestCase(typeof(DefaultRenderPipeline))]
    [TestCase(typeof(DefaultRenderPipeline2))]
    public void DefaultPipelines_ForwardPlusBuffersMatchProfileDimensions(Type pipelineType)
    {
        RenderPipeline pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;
        RenderPipelineResourceLayout mono = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, 1u));
        RenderPipelineResourceLayout stereo = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, 1u, stereo: true));

        BufferSpec localLights = mono.ResourcesByName[VPRC_ForwardPlusLightCullingPass.LocalLightsBufferName].ShouldBeOfType<BufferSpec>();
        localLights.ElementCount.ShouldBe(VPRC_ForwardPlusLightCullingPass.MaxLocalLights);
        localLights.ElementStride.ShouldBe(VPRC_ForwardPlusLightCullingPass.LocalLightStride);

        uint monoTiles = 80u * 45u;
        uint stereoTiles = monoTiles * 2u;
        mono.ResourcesByName[VPRC_ForwardPlusLightCullingPass.TileLightCountsBufferName]
            .ShouldBeOfType<BufferSpec>().ElementCount.ShouldBe(monoTiles);
        stereo.ResourcesByName[VPRC_ForwardPlusLightCullingPass.TileLightCountsBufferName]
            .ShouldBeOfType<BufferSpec>().ElementCount.ShouldBe(stereoTiles);
        stereo.ResourcesByName[VPRC_ForwardPlusLightCullingPass.VisibleIndicesBufferName]
            .ShouldBeOfType<BufferSpec>().ElementCount.ShouldBe(stereoTiles * VPRC_ForwardPlusLightCullingPass.MaxLightsPerTile);
    }

    [TestCase(typeof(DefaultRenderPipeline))]
    [TestCase(typeof(DefaultRenderPipeline2))]
    public void DefaultPipelines_GiProfilesDeclareOnlySelectedWorkingResources(Type pipelineType)
    {
        const ulong restir = 1UL << 20;
        const ulong radianceCascades = 1UL << 22;
        const ulong surfelGi = 1UL << 23;
        RenderPipeline pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;
        RenderPipelineResourceLayout baseline = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, 1u));
        RenderPipelineResourceLayout restirLayout = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, 1u, restir));
        RenderPipelineResourceLayout radianceLayout = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, 1u, radianceCascades, stereo: true));
        RenderPipelineResourceLayout surfelLayout = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, 1u, surfelGi));

        baseline.ResourcesByName.Keys.ShouldNotContain(VPRC_ReSTIRPass.InitialReservoirBufferName);
        baseline.ResourcesByName.Keys.ShouldNotContain(VPRC_RadianceCascadesPass.HistoryTextureAName);
        baseline.ResourcesByName.Keys.ShouldNotContain(VPRC_SurfelGIPass.SurfelBufferName);

        foreach (string name in new[]
        {
            VPRC_ReSTIRPass.InitialReservoirBufferName,
            VPRC_ReSTIRPass.TemporalReservoirBufferName,
            VPRC_ReSTIRPass.SpatialReservoirBufferName,
        })
        {
            BufferSpec buffer = restirLayout.ResourcesByName[name].ShouldBeOfType<BufferSpec>();
            buffer.ElementCount.ShouldBe(1280u * 720u, name);
            buffer.ElementStride.ShouldBe(VPRC_ReSTIRPass.ReservoirStride, name);
        }

        foreach (string name in new[]
        {
            VPRC_RadianceCascadesPass.HistoryTextureAName,
            VPRC_RadianceCascadesPass.HistoryTextureBName,
        })
        {
            TextureSpec history = radianceLayout.ResourcesByName[name].ShouldBeOfType<TextureSpec>();
            history.Layers.ShouldBe(2u, name);
            history.HistoryPolicy.ShouldBe(RenderResourceHistoryPolicy.ClearOnCommit, name);
        }

        (string Name, uint Count)[] surfelBuffers =
        [
            (VPRC_SurfelGIPass.SurfelBufferName, VPRC_SurfelGIPass.MaxSurfelsConst),
            (VPRC_SurfelGIPass.CounterBufferName, VPRC_SurfelGIPass.CounterCount),
            (VPRC_SurfelGIPass.FreeStackBufferName, VPRC_SurfelGIPass.MaxSurfelsConst),
            (VPRC_SurfelGIPass.GridCountsBufferName, VPRC_SurfelGIPass.GridCellCount),
            (VPRC_SurfelGIPass.GridIndicesBufferName, VPRC_SurfelGIPass.GridIndexCount),
        ];
        foreach ((string name, uint count) in surfelBuffers)
            surfelLayout.ResourcesByName[name].ShouldBeOfType<BufferSpec>().ElementCount.ShouldBe(count, name);
    }

    [Test]
    public void DefaultRenderPipeline_LightCombineFbo_IsGenerationOwnedAfterAoMigration()
    {
        DefaultRenderPipeline pipeline = new();
        RenderPipelineResourceLayout layout = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, msaaSamples: 1u));

        FrameBufferSpec lightCombine = layout.ResourcesByName[DefaultRenderPipeline.LightCombineFBOName]
            .ShouldBeOfType<FrameBufferSpec>();

        lightCombine.Lifetime.ShouldBe(RenderResourceLifetime.Transient);
        lightCombine.Attachments.Select(x => x.ResourceName).ToArray().ShouldBe([DefaultRenderPipeline.DiffuseTextureName]);
        lightCombine.Dependencies.ShouldContain(DefaultRenderPipeline.AlbedoOpacityTextureName);
        lightCombine.Dependencies.ShouldContain(DefaultRenderPipeline.NormalTextureName);
        lightCombine.Dependencies.ShouldContain(DefaultRenderPipeline.RMSETextureName);
        lightCombine.Dependencies.ShouldContain(DefaultRenderPipeline.AmbientOcclusionIntensityTextureName);
        lightCombine.Dependencies.ShouldContain(DefaultRenderPipeline.DepthViewTextureName);
        lightCombine.Dependencies.ShouldContain(DefaultRenderPipeline.LightingAccumTextureName);
        lightCombine.Dependencies.ShouldContain(DefaultRenderPipeline.BRDFTextureName);

        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");

        source.ShouldNotContain("LightCombineFBOName,\n            CreateLightCombineFBO,\n            GetDesiredFBOSizeInternal,\n            NeedsRecreateLightCombineFbo)");
        source.ShouldNotContain("DependentFboNames = new[] { LightCombineFBOName }");
    }

    [Test]
    public void DefaultRenderPipeline_MsaaProfile_DeclaresDeferredMsaaResources()
    {
        DefaultRenderPipeline pipeline = new();

        RenderPipelineResourceLayout nonMsaa = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, msaaSamples: 1u));
        RenderPipelineResourceLayout msaa = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Msaa, msaaSamples: 4u));

        nonMsaa.ResourcesByName.Keys.ShouldNotContain(DefaultRenderPipeline.MsaaGBufferFBOName);
        nonMsaa.ResourcesByName.Keys.ShouldNotContain(DefaultRenderPipeline.MsaaDepthViewTextureName);

        msaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.MsaaGBufferFBOName);
        msaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.MsaaLightingFBOName);
        msaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.MsaaDepthViewTextureName);

        TextureSpec depth = msaa.ResourcesByName[DefaultRenderPipeline.MsaaDepthStencilTextureName].ShouldBeOfType<TextureSpec>();
        depth.Samples.ShouldBe(4u);
    }

    [Test]
    public void DefaultRenderPipeline_OpenXrVulkanSafeMsaaProfile_DoesNotDeclareDeferredMsaaResources()
    {
        DefaultRenderPipeline pipeline = new();
        const ulong openXrVulkanSafePathFeature = 1UL << 12;

        RenderPipelineResourceLayout msaa = pipeline.BuildResourceLayout(CreateProfile(
            EAntiAliasingMode.Msaa,
            msaaSamples: 4u,
            featureMask: openXrVulkanSafePathFeature));

        msaa.ResourcesByName.Keys.ShouldNotContain(DefaultRenderPipeline.MsaaGBufferFBOName);
        msaa.ResourcesByName.Keys.ShouldNotContain(DefaultRenderPipeline.MsaaLightingFBOName);
        msaa.ResourcesByName.Keys.ShouldNotContain(DefaultRenderPipeline.MsaaDepthViewTextureName);
    }

    [Test]
    public void DefaultRenderPipeline_SmaaProfile_DeclaresSmaaResources()
    {
        DefaultRenderPipeline pipeline = new();

        RenderPipelineResourceLayout smaa = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Smaa, msaaSamples: 1u));

        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.SmaaEdgeTextureName);
        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.SmaaBlendTextureName);
        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.SmaaOutputTextureName);
        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.SmaaEdgeFBOName);
        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.SmaaBlendFBOName);
        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.SmaaFBOName);

        FrameBufferSpec output = smaa.ResourcesByName[DefaultRenderPipeline.SmaaFBOName].ShouldBeOfType<FrameBufferSpec>();
        output.Attachments.Select(x => x.ResourceName).ShouldContain(DefaultRenderPipeline.SmaaOutputTextureName);
    }

    [Test]
    public void DefaultRenderPipeline_MsaaLightCombineFactory_UsesDeclaredSampleTextures()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs");

        source.ShouldNotContain("EnsurePipelineTexture(MsaaDepthStencilTextureName");
        source.ShouldNotContain("EnsurePipelineTexture(MsaaLightingTextureName");
        source.ShouldContain("GetTexture<XRTexture>(MsaaLightingTextureName)!");
    }

    [Test]
    public void DefaultRenderPipeline_StereoPostProcessSettingsUseEffectiveCamera()
    {
        string pipelineSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        string postProcessSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.PostProcessing.cs");
        string bloomSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_BloomPass.cs");

        pipelineSource.ShouldContain("private static XRCamera? ResolveCurrentSettingsCamera");
        pipelineSource.ShouldContain("?? RenderingCamera");
        pipelineSource.ShouldContain("var camera = ResolveCurrentSettingsCamera(currentPipeline);");

        postProcessSource.ShouldContain("ResolveCurrentSettingsCamera()?.GetPostProcessStageState<BloomSettings>()");
        postProcessSource.ShouldContain("ResolveCurrentSettingsCamera()?.GetActivePostProcessState()");
        postProcessSource.ShouldNotContain("RenderingPipelineState?.SceneCamera?.GetActivePostProcessState()");

        bloomSource.ShouldContain("private static BloomSettings? ResolveBloomSettings(XRRenderPipelineInstance instance)");
        bloomSource.ShouldContain("?? instance.LastRenderingCamera");
        bloomSource.ShouldNotContain("var camera = instance.RenderState.SceneCamera;\n            var bloomStage = camera?.GetPostProcessStageState<BloomSettings>();");
    }

    [Test]
    public void DefaultRenderPipeline_GtaoScratchResources_FollowResolutionFeatureMask()
    {
        DefaultRenderPipeline pipeline = new();

        AssertGtaoScratchScale(
            pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, msaaSamples: 1u, featureMask: 1UL << 13)),
            0.5f);
        AssertGtaoScratchScale(
            pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, msaaSamples: 1u, featureMask: (1UL << 13) | (1UL << 6))),
            1.0f);
        AssertGtaoScratchScale(
            pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, msaaSamples: 1u, featureMask: (1UL << 13) | (1UL << 7))),
            0.25f);
    }

    [Test]
    public void DefaultRenderPipeline_GtaoFrameBuffers_DependOnSampledDepthAndNormalViews()
    {
        const ulong ambientOcclusionResourcesEnabled = 1UL << 13;
        const ulong gtaoMode = 8UL << 26;
        DefaultRenderPipeline pipeline = new();
        RenderPipelineResourceLayout layout = pipeline.BuildResourceLayout(CreateProfile(
            EAntiAliasingMode.Fxaa,
            msaaSamples: 1u,
            featureMask: ambientOcclusionResourcesEnabled | gtaoMode));

        string[] frameBufferNames =
        [
            DefaultRenderPipeline.AmbientOcclusionFBOName,
            DefaultRenderPipeline.AmbientOcclusionBlurFBOName,
            DefaultRenderPipeline.GTAOBlurIntermediateFBOName,
        ];

        int depthViewIndex = layout.OrderedSpecs
            .Select((spec, index) => (spec, index))
            .Single(entry => entry.spec.Name == DefaultRenderPipeline.DepthViewTextureName)
            .index;
        int normalIndex = layout.OrderedSpecs
            .Select((spec, index) => (spec, index))
            .Single(entry => entry.spec.Name == DefaultRenderPipeline.NormalTextureName)
            .index;

        foreach (string frameBufferName in frameBufferNames)
        {
            FrameBufferSpec frameBuffer = layout.ResourcesByName[frameBufferName].ShouldBeOfType<FrameBufferSpec>();
            frameBuffer.Dependencies.ShouldContain(DefaultRenderPipeline.DepthViewTextureName, frameBufferName);
            frameBuffer.Dependencies.ShouldContain(DefaultRenderPipeline.NormalTextureName, frameBufferName);

            int frameBufferIndex = layout.OrderedSpecs
                .Select((spec, index) => (spec, index))
                .Single(entry => ReferenceEquals(entry.spec, frameBuffer))
                .index;
            frameBufferIndex.ShouldBeGreaterThan(depthViewIndex, frameBufferName);
            frameBufferIndex.ShouldBeGreaterThan(normalIndex, frameBufferName);
        }
    }

    [Test]
    public void DefaultRenderPipeline_GtaoScratchFactory_MatchesDescriptorRoundingForOddEyeExtent()
    {
        DefaultRenderPipeline.ScaleGtaoScratchExtent(896u, divisor: 2).ShouldBe(448u);
        DefaultRenderPipeline.ScaleGtaoScratchExtent(1007u, divisor: 2).ShouldBe(504u);
        DefaultRenderPipeline.ScaleGtaoScratchExtent(1007u, divisor: 4).ShouldBe(252u);
    }

    [Test]
    public void ResourceManager_MaterializesDeclaredTextureAndFrameBufferIntoPendingRegistry()
    {
        XRRenderPipelineInstance instance = new();
        RenderPipelineResourceLayoutBuilder builder = new();

        builder.Texture("Color")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
            .Format(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
            .SizedFormat(ESizedInternalFormat.Rgba8)
            .Factory(() => XRTexture2D.CreateFrameBufferTexture(
                64u,
                32u,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte,
                EFrameBufferAttachment.ColorAttachment0))
            .Add();

        builder.FrameBuffer("ColorFBO")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, "Color")
            .Factory(() =>
            {
                XRTexture color = instance.GetTexture<XRTexture>("Color")
                    ?? throw new InvalidOperationException("Color texture was not available in build context.");
                return new XRFrameBuffer(((IFrameBufferAttachement)color, EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = "ColorFBO"
                };
            })
            .Add();

        RenderPipelineResourceLayout layout = builder.Build(RenderPipelineResourceProfile.Empty);
        RenderResourceGeneration generation = new(CreateKey(), layout);

        try
        {
            RenderPipelineResourceManager manager = new();
            manager.Materialize(instance, generation).ShouldBeTrue();

            generation.Status.ShouldBe(RenderResourceGenerationStatus.Ready);
            generation.Registry.TryGetTexture("Color", out XRTexture? texture).ShouldBeTrue();
            generation.Registry.TryGetFrameBuffer("ColorFBO", out XRFrameBuffer? frameBuffer).ShouldBeTrue();
            frameBuffer!.Targets![0].Target.ShouldBeSameAs(texture);
        }
        finally
        {
            generation.Dispose();
        }
    }

    [Test]
    public void ResourceGeneration_StateTransitions_RecordLifecycle()
    {
        RenderResourceGeneration generation = new(CreateKey(), RenderPipelineResourceLayout.Empty);
        generation.Status.ShouldBe(RenderResourceGenerationStatus.Created);

        generation.BeginBuild();
        generation.Status.ShouldBe(RenderResourceGenerationStatus.Building);

        generation.MarkReady();
        generation.Status.ShouldBe(RenderResourceGenerationStatus.Ready);

        generation.MarkActive("Commit");
        generation.Status.ShouldBe(RenderResourceGenerationStatus.Active);
        generation.CommitReason.ShouldBe("Commit");

        generation.MarkRetired("Replacement");
        generation.Status.ShouldBe(RenderResourceGenerationStatus.Retired);
        generation.RetirementReason.ShouldBe("Replacement");

        generation.Dispose();
        generation.Status.ShouldBe(RenderResourceGenerationStatus.Disposed);

        RenderResourceGeneration failed = new(CreateKey(), RenderPipelineResourceLayout.Empty);
        failed.BeginBuild();
        failed.MarkFailed("Factory failed");
        failed.Status.ShouldBe(RenderResourceGenerationStatus.Failed);
        failed.Diagnostics.ShouldContain("Factory failed");

        RenderResourceGeneration superseded = new(CreateKey(), RenderPipelineResourceLayout.Empty);
        superseded.MarkSuperseded("Newer request");
        superseded.Status.ShouldBe(RenderResourceGenerationStatus.Superseded);
        superseded.RetirementReason.ShouldBe("Newer request");
    }

    [Test]
    public void ResourceGenerationKey_ContainsOnlyStructuralRenderProfileInputs()
    {
        string[] propertyNames = typeof(ResourceGenerationKey)
            .GetProperties()
            .Select(static p => p.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        string[] expectedProperties =
        [
            nameof(ResourceGenerationKey.PipelineName),
            nameof(ResourceGenerationKey.DisplayWidth),
            nameof(ResourceGenerationKey.DisplayHeight),
            nameof(ResourceGenerationKey.ExternalTargetKind),
            nameof(ResourceGenerationKey.InternalWidth),
            nameof(ResourceGenerationKey.InternalHeight),
            nameof(ResourceGenerationKey.OutputHDR),
            nameof(ResourceGenerationKey.AntiAliasingMode),
            nameof(ResourceGenerationKey.MsaaSampleCount),
            nameof(ResourceGenerationKey.Stereo),
            nameof(ResourceGenerationKey.FeatureMask),
            nameof(ResourceGenerationKey.ReservedViewCount),
            nameof(ResourceGenerationKey.ReservedEyeIndex),
            nameof(ResourceGenerationKey.SettingsRevision),
        ];

        propertyNames.ShouldBe(expectedProperties.OrderBy(static name => name, StringComparer.Ordinal).ToArray());

        propertyNames.ShouldNotContain("Camera");
        propertyNames.ShouldNotContain("CameraTransform");
        propertyNames.ShouldNotContain("ViewMatrix");
        propertyNames.ShouldNotContain("ProjectionMatrix");
    }

    [Test]
    public void EffectiveGenerationKey_ChangesForEachStructuralProfileInput()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        SetEffectiveFrameProfile(instance, outputHdr: false, EAntiAliasingMode.None, msaaSamples: 1u);
        ResourceGenerationKey baseline = BuildGenerationKey(instance, 640, 360, 320, 180);

        AssertOnlyKeyFieldsChanged(baseline, BuildGenerationKey(instance, 800, 450, 320, 180),
            nameof(ResourceGenerationKey.DisplayWidth), nameof(ResourceGenerationKey.DisplayHeight));
        AssertOnlyKeyFieldsChanged(baseline, BuildGenerationKey(instance, 640, 360, 400, 225),
            nameof(ResourceGenerationKey.InternalWidth), nameof(ResourceGenerationKey.InternalHeight));

        SetEffectiveFrameProfile(instance, outputHdr: true, EAntiAliasingMode.None, msaaSamples: 1u);
        AssertOnlyKeyFieldsChanged(baseline, BuildGenerationKey(instance, 640, 360, 320, 180),
            nameof(ResourceGenerationKey.OutputHDR), nameof(ResourceGenerationKey.SettingsRevision));

        SetEffectiveFrameProfile(instance, outputHdr: false, EAntiAliasingMode.Taa, msaaSamples: 1u);
        AssertOnlyKeyFieldsChanged(baseline, BuildGenerationKey(instance, 640, 360, 320, 180),
            nameof(ResourceGenerationKey.AntiAliasingMode), nameof(ResourceGenerationKey.SettingsRevision));

        SetEffectiveFrameProfile(instance, outputHdr: false, EAntiAliasingMode.None, msaaSamples: 4u);
        AssertOnlyKeyFieldsChanged(baseline, BuildGenerationKey(instance, 640, 360, 320, 180),
            nameof(ResourceGenerationKey.MsaaSampleCount), nameof(ResourceGenerationKey.SettingsRevision));

        SetEffectiveFrameProfile(instance, outputHdr: false, EAntiAliasingMode.None, msaaSamples: 1u);
        pipeline.StereoResources = true;
        AssertOnlyKeyFieldsChanged(baseline, BuildGenerationKey(instance, 640, 360, 320, 180),
            nameof(ResourceGenerationKey.Stereo), nameof(ResourceGenerationKey.ReservedViewCount), nameof(ResourceGenerationKey.SettingsRevision));
        BuildGenerationKey(instance, 640, 360, 320, 180).ReservedEyeIndex.ShouldBe(0u);
        pipeline.StereoResources = false;

        pipeline.FeatureMask = 0x20UL;
        AssertOnlyKeyFieldsChanged(baseline, BuildGenerationKey(instance, 640, 360, 320, 180),
            nameof(ResourceGenerationKey.FeatureMask), nameof(ResourceGenerationKey.SettingsRevision));

        pipeline.FeatureMask = 0UL;
        XRViewport externalViewport = new(null) { RendersToExternalSwapchainTarget = true };
        AssertOnlyKeyFieldsChanged(baseline, BuildGenerationKey(instance, 640, 360, 320, 180, externalViewport),
            nameof(ResourceGenerationKey.ExternalTargetKind), nameof(ResourceGenerationKey.SettingsRevision));
    }

    [Test]
    public void EffectiveGenerationKey_ReusesImmutableSettingsRevisionAcrossExtentOnlyRequests()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        SetEffectiveFrameProfile(instance, outputHdr: false, EAntiAliasingMode.Taa, msaaSamples: 1u);

        ResourceGenerationKey first = BuildGenerationKey(instance, 640, 360, 320, 180);
        ResourceGenerationKey resized = BuildGenerationKey(instance, 1280, 720, 640, 360);
        ResourceGenerationKey repeated = BuildGenerationKey(instance, 1280, 720, 640, 360);

        first.SettingsRevision.ShouldBeGreaterThan(0UL);
        resized.SettingsRevision.ShouldBe(first.SettingsRevision);
        repeated.ShouldBe(resized);
    }

    [Test]
    public void ResourceGeneration_DetectsFeatureMaskDivergenceForSameSettingsRevision()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        ResourceGenerationKey baseline = CreateKey() with { SettingsRevision = 7UL };
        ResourceGenerationKey divergent = baseline with { FeatureMask = 0x20UL };

        RequestGenerationKey(instance, baseline, "Baseline").ShouldBeTrue();
        RequestGenerationKey(instance, divergent, "Divergent").ShouldBeTrue();

        instance.ResourceGenerationDivergenceCount.ShouldBeGreaterThan(0L);
    }

    [Test]
    public void ResourceGeneration_DetectsLayoutDivergenceBehindMatchingPendingKey()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        ResourceGenerationKey key = CreateKey() with { SettingsRevision = 11UL };

        RequestGenerationKey(instance, key, "Baseline", force: true).ShouldBeTrue();
        pipeline.IncludeExternalTexture = true;
        RequestGenerationKey(instance, key, "SameKeyDifferentLayout", force: true).ShouldBeTrue();

        instance.ResourceGenerationDivergenceCount.ShouldBeGreaterThan(0L);
    }

    [Test]
    public void ResourceGenerationSettings_CameraNullPathDoesNotReadAmbientOrLastCamera()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        string capture = Regex.Match(
            source,
            @"private ResourceGenerationSettingsSnapshot CaptureResourceGenerationSettingsSnapshot[\s\S]*?private bool TryPreparePendingGeneration").Value;

        capture.ShouldNotContain("RenderState.SceneCamera");
        capture.ShouldNotContain("RenderState.RenderingCamera");
        capture.ShouldNotContain("LastSceneCamera");
        capture.ShouldNotContain("LastRenderingCamera");
        capture.ShouldContain("viewport?.ActiveCamera");
    }

    [TestCase(typeof(DefaultRenderPipeline))]
    [TestCase(typeof(DefaultRenderPipeline2))]
    [NonParallelizable]
    public void DefaultPipeline_CameraUnavailableResizeThenFramePrepareKeepsOneFeatureSnapshot(Type pipelineType)
    {
        IRuntimeShaderServices? previousShaderServices = RuntimeShaderServices.Current;
        XRRenderPipelineInstance? instance = null;
        try
        {
            RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
            RenderPipeline pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;
            instance = new XRRenderPipelineInstance(pipeline);
            XRCamera gtaoCamera = new();
            _ = gtaoCamera.PostProcessStates.GetOrCreateState(pipeline);
            XRViewport viewport = new(null) { SetRenderPipelineFromCamera = false };
            viewport.Camera = gtaoCamera;

            ResourceGenerationKey cameraAvailable = BuildGenerationKey(
                instance,
                1920,
                1080,
                1920,
                1080,
                viewport);
            cameraAvailable.FeatureMask.ShouldNotBe(0UL);

            // Model the window callback that temporarily cannot resolve its camera. The last
            // immutable settings snapshot must carry GTAO mode/resolution through the callback.
            viewport.Camera = null;
            instance.ViewportResized(2560, 1369, viewport);
            RenderResourceGeneration? resizePending = instance.PendingGeneration;
            if (pipeline is DefaultRenderPipeline)
            {
                resizePending.ShouldNotBeNull();
                resizePending.Key.FeatureMask.ShouldBe(cameraAvailable.FeatureMask);
                resizePending.Key.SettingsRevision.ShouldBe(cameraAvailable.SettingsRevision);
            }
            else
            {
                // V2 invalidates its resize-sensitive resources here and deliberately waits
                // for frame preparation to request the next managed generation.
                resizePending.ShouldBeNull();
            }

            // The next frame resolves the camera again. It must recognize the resize request as
            // the same generation instead of superseding it as a FrameProfileChanged request.
            viewport.Camera = gtaoCamera;
            instance.RequestResourceGeneration(
                2560,
                1369,
                2560,
                1369,
                "FrameProfileChanged",
                viewport: viewport).ShouldBeTrue();

            RenderResourceGeneration framePending = instance.PendingGeneration.ShouldNotBeNull();
            if (resizePending is not null)
                framePending.ShouldBeSameAs(resizePending);
            framePending.Key.FeatureMask.ShouldBe(cameraAvailable.FeatureMask);
            framePending.Key.SettingsRevision.ShouldBe(cameraAvailable.SettingsRevision);
            instance.ResourceGenerationDivergenceCount.ShouldBe(0L);
        }
        finally
        {
            instance?.DestroyCache();
            RuntimeShaderServices.Current = previousShaderServices;
        }
    }

    [Test]
    public void EffectiveGenerationKey_IgnoresConcreteTargetIdentityWithinSameImportedKind()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        SetEffectiveFrameProfile(instance, outputHdr: false, EAntiAliasingMode.None, msaaSamples: 1u);

        ResourceGenerationKey first;
        using (instance.RenderState.PushMainAttributes(
            viewport: null,
            scene: null,
            camera: null,
            stereoRightEyeCamera: null,
            target: new XRFrameBuffer(),
            shadowPass: false,
            stereoPass: false,
            globalMaterialOverride: null,
            screenSpaceUI: null,
            meshRenderCommands: null))
        {
            first = BuildGenerationKey(instance, 640, 360, 320, 180);
        }

        ResourceGenerationKey second;
        using (instance.RenderState.PushMainAttributes(
            viewport: null,
            scene: null,
            camera: null,
            stereoRightEyeCamera: null,
            target: new XRFrameBuffer(),
            shadowPass: false,
            stereoPass: false,
            globalMaterialOverride: null,
            screenSpaceUI: null,
            meshRenderCommands: null))
        {
            second = BuildGenerationKey(instance, 640, 360, 320, 180);
        }

        first.ExternalTargetKind.ShouldBe(RenderPipelineExternalTargetKind.CallerProvidedFrameBuffer);
        second.ShouldBe(first);
    }

    [Test]
    public void EffectiveGenerationKey_UsesViewportCameraOverridesAndCapturePolicy()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        XRCamera camera = new()
        {
            OutputHDROverride = true,
            AntiAliasingModeOverride = EAntiAliasingMode.Taa,
            MsaaSampleCountOverride = 4u,
        };
        XRViewport viewport = new(null) { SetRenderPipelineFromCamera = false };
        viewport.Camera = camera;

        ResourceGenerationKey overridden = BuildGenerationKey(instance, 640, 360, 320, 180, viewport);
        overridden.OutputHDR.ShouldBeTrue();
        overridden.AntiAliasingMode.ShouldBe(EAntiAliasingMode.Taa);
        overridden.MsaaSampleCount.ShouldBe(4u);

        viewport.ApplyCapturePolicy(RenderCapturePolicy.GenericSceneCapture);
        ResourceGenerationKey capture = BuildGenerationKey(instance, 640, 360, 320, 180, viewport);
        capture.OutputHDR.ShouldBe(viewport.CapturePolicy.OutputHDR);
        capture.AntiAliasingMode.ShouldBe(EAntiAliasingMode.None);
        capture.MsaaSampleCount.ShouldBe(1u);
    }

    [Test]
    public void FailedReplacementGeneration_PreservesActiveGenerationAndDisposesPendingResources()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);

        instance.RequestResourceGeneration(64, 32, 64, 32, "Initial", force: true).ShouldBeTrue();
        PreparePendingGeneration(instance).ShouldBeTrue();
        RenderResourceGeneration active = instance.ActiveGeneration.ShouldNotBeNull();
        active.Registry.TryGetTexture("StableColor", out XRTexture? activeTexture).ShouldBeTrue();
        activeTexture.ShouldNotBeNull();

        pipeline.FailFactories = true;
        instance.RequestResourceGeneration(96, 48, 96, 48, "FailingReplacement", force: true).ShouldBeTrue();
        PreparePendingGeneration(instance).ShouldBeFalse();

        instance.ActiveGeneration.ShouldBeSameAs(active);
        instance.PendingGeneration.ShouldBeNull();
        active.Registry.TryGetTexture("StableColor", out XRTexture? preservedTexture).ShouldBeTrue();
        preservedTexture.ShouldBeSameAs(activeTexture);
        active.Status.ShouldBe(RenderResourceGenerationStatus.Active);

        instance.DestroyCache();
    }

    [TestCase("image")]
    [TestCase("buffer")]
    [TestCase("framebuffer/view")]
    public void FailedBackendPreparation_PreservesLogicalAndPhysicalActiveGeneration(string failureKind)
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        instance.RequestResourceGeneration(64, 32, 64, 32, "Initial", force: true).ShouldBeTrue();
        PreparePendingGeneration(instance).ShouldBeTrue();
        RenderResourceGeneration active = instance.ActiveGeneration.ShouldNotBeNull();
        int logicalGeneration = instance.ResourceGeneration;

        BackendGenerationSnapshot activeSnapshot = new(
            PlannerRevision: 7,
            AllocatorOwnershipId: 11,
            DescriptorSignature: 13,
            AllocationSignature: 17,
            ImageHandle: 19,
            BufferHandle: 23,
            FrameBufferHandle: 29,
            MetadataRevision: 31);
        TestResourceGenerationBackend backend = new(activeSnapshot)
        {
            FailureReason = $"Injected Vulkan {failureKind} allocation failure.",
        };
        instance.ResourceGenerationBackendOverride = backend;

        instance.RequestResourceGeneration(96, 48, 96, 48, "BackendFailure", force: true).ShouldBeTrue();
        PreparePendingGeneration(instance).ShouldBeFalse();

        instance.ActiveGeneration.ShouldBeSameAs(active);
        instance.ResourceGeneration.ShouldBe(logicalGeneration);
        instance.PendingGeneration.ShouldBeNull();
        backend.ActiveSnapshot.ShouldBe(activeSnapshot);
        backend.CommitCount.ShouldBe(0);
        active.Status.ShouldBe(RenderResourceGenerationStatus.Active);

        instance.DestroyCache();
    }

    [Test]
    public void SuccessfulBackendCommit_PublishesLogicalAndPhysicalGenerationTogether()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        instance.RequestResourceGeneration(64, 32, 64, 32, "Initial", force: true).ShouldBeTrue();
        PreparePendingGeneration(instance).ShouldBeTrue();
        RenderResourceGeneration previous = instance.ActiveGeneration.ShouldNotBeNull();

        BackendGenerationSnapshot pendingSnapshot = new(
            PlannerRevision: 37,
            AllocatorOwnershipId: 41,
            DescriptorSignature: 43,
            AllocationSignature: 47,
            ImageHandle: 53,
            BufferHandle: 59,
            FrameBufferHandle: 61,
            MetadataRevision: 67);
        TestResourceGenerationBackend backend = new(default)
        {
            PendingSnapshot = pendingSnapshot,
        };
        instance.ResourceGenerationBackendOverride = backend;

        instance.RequestResourceGeneration(96, 48, 96, 48, "AtomicReplacement", force: true).ShouldBeTrue();
        RenderResourceGeneration pending = instance.PendingGeneration.ShouldNotBeNull();
        backend.OnCommit = () =>
        {
            instance.ActiveGeneration.ShouldBeSameAs(pending);
            instance.ResourceGeneration.ShouldBe(2);
            pending.Status.ShouldBe(RenderResourceGenerationStatus.Active);
        };

        PreparePendingGeneration(instance).ShouldBeTrue();

        instance.ActiveGeneration.ShouldBeSameAs(pending);
        backend.ActiveSnapshot.ShouldBe(pendingSnapshot);
        backend.CommitCount.ShouldBe(1);
        backend.RollbackCount.ShouldBe(0);
        previous.Status.ShouldBe(RenderResourceGenerationStatus.Disposed);

        instance.DestroyCache();
    }

    [Test]
    public void ImportedResourceKindMismatch_ReportsExpectedAndDeclaredContracts()
    {
        GenerationFailureTestPipeline pipeline = new() { IncludeExternalTexture = true };
        XRRenderPipelineInstance instance = new(pipeline);
        instance.RequestResourceGeneration(64, 32, 64, 32, "Initial", force: true).ShouldBeTrue();
        PreparePendingGeneration(instance).ShouldBeTrue();

        XRDataBuffer buffer = new("ImportedProbe", EBufferTarget.ArrayBuffer, integral: false);
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => instance.BindImportedBuffer(buffer));

        exception.Message.ShouldContain("Imported resource mismatch.");
        exception.Message.ShouldContain("Resource=ImportedProbe");
        exception.Message.ShouldContain("ExpectedExternalKind=Buffer");
        exception.Message.ShouldContain("Actual=ExternalKind=Texture");
        exception.Message.ShouldContain("Ownership=Scene");
        exception.Message.ShouldContain("Synchronization=BackendManaged");

        instance.DestroyCache();
    }

    [Test]
    public void SupersededPendingGeneration_IsDisposedAndPendingCountRemainsOne()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        instance.RequestResourceGeneration(64, 32, 64, 32, "Initial", force: true).ShouldBeTrue();
        PreparePendingGeneration(instance).ShouldBeTrue();

        instance.RequestResourceGeneration(96, 48, 96, 48, "ResizeA", force: true).ShouldBeTrue();
        RenderResourceGeneration superseded = instance.PendingGeneration.ShouldNotBeNull();
        new RenderPipelineResourceManager().MaterializeIncremental(
            instance,
            superseded,
            TimeSpan.MaxValue,
            maxSpecsPerSlice: 1,
            out bool completed).ShouldBeTrue();
        completed.ShouldBeFalse();
        superseded.MaterializedSpecCount.ShouldBe(1);

        instance.RequestResourceGeneration(128, 64, 128, 64, "ResizeB", force: true).ShouldBeTrue();

        superseded.Status.ShouldBe(RenderResourceGenerationStatus.Disposed);
        instance.PendingGeneration.ShouldNotBeNull().ShouldNotBeSameAs(superseded);
        instance.PendingGeneration!.Key.InternalWidth.ShouldBe(128u);

        instance.DestroyCache();
    }

    [Test]
    public void RapidResizeAndFeatureToggleBurst_CoalescesPendingAndBoundsRetiredGenerations()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        instance.RequestResourceGeneration(64, 32, 64, 32, "Initial", force: true).ShouldBeTrue();
        PreparePendingGeneration(instance).ShouldBeTrue();

        for (int iteration = 0; iteration < 16; iteration++)
        {
            int firstWidth = 80 + iteration * 2;
            pipeline.FeatureMask = (ulong)(iteration & 1);
            instance.RequestResourceGeneration(
                firstWidth,
                48,
                firstWidth,
                48,
                $"RapidResize{iteration}A",
                force: true).ShouldBeTrue();
            RenderResourceGeneration superseded = instance.PendingGeneration.ShouldNotBeNull();
            new RenderPipelineResourceManager().MaterializeIncremental(
                instance,
                superseded,
                TimeSpan.MaxValue,
                maxSpecsPerSlice: 1,
                out bool completed).ShouldBeTrue();
            completed.ShouldBeFalse();

            int finalWidth = firstWidth + 1;
            pipeline.FeatureMask ^= 1UL;
            instance.RequestResourceGeneration(
                finalWidth,
                48,
                finalWidth,
                48,
                $"RapidResize{iteration}B",
                force: true).ShouldBeTrue();

            superseded.Status.ShouldBe(RenderResourceGenerationStatus.Disposed);
            instance.PendingGeneration.ShouldNotBeNull().Key.InternalWidth.ShouldBe((uint)finalWidth);
            PreparePendingGeneration(instance).ShouldBeTrue();
            instance.PendingGeneration.ShouldBeNull();
            instance.ActiveGeneration.ShouldNotBeNull().Key.InternalWidth.ShouldBe((uint)finalWidth);
            instance.RetiredGenerations.Count.ShouldBeLessThanOrEqualTo(3);
        }

        instance.DestroyCache();
    }

    [Test]
    public void SteadyStateCommandExecutionAndResourcePlanLookup_DoNotAllocatePerIteration()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        instance.RequestResourceGeneration(64, 32, 64, 32, "Initial", force: true).ShouldBeTrue();
        PreparePendingGeneration(instance).ShouldBeTrue();

        bool allResourcesResolved = true;
        int signatureAccumulator = 0;
        using (RuntimeEngine.Rendering.State.PushRenderingPipeline(instance))
        {
            pipeline.CommandChain.Execute();
            instance.TryGetTexture("StableColor", out _).ShouldBeTrue();
            _ = instance.Resources.DescriptorSignature;

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 256; iteration++)
            {
                pipeline.CommandChain.Execute();
                allResourcesResolved &= instance.TryGetTexture("StableColor", out _);
                signatureAccumulator ^= instance.Resources.DescriptorSignature;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            allocated.ShouldBeLessThanOrEqualTo(128L);
        }

        allResourcesResolved.ShouldBeTrue();
        signatureAccumulator.ShouldBe(0);
        instance.DestroyCache();
    }

    [Test]
    public void DefaultRenderPipeline_ResourceFeatureMaskIsStableUntilStructuralFeatureChanges()
    {
        DefaultRenderPipeline pipeline = new();
        XRRenderPipelineInstance instance = new();

        ulong first = pipeline.BuildResourceFeatureMaskForGenerationKey(instance, null);
        ulong second = pipeline.BuildResourceFeatureMaskForGenerationKey(instance, null);

        second.ShouldBe(first);

        pipeline.EnableDeferredMsaa = !pipeline.EnableDeferredMsaa;

        pipeline.BuildResourceFeatureMaskForGenerationKey(instance, null).ShouldNotBe(first);
    }

    [TestCase(typeof(DefaultRenderPipeline))]
    [TestCase(typeof(DefaultRenderPipeline2))]
    public void DefaultPipeline_CapturePolicyChangesFeatureMaskOnlyForMinimalResourceLayout(Type pipelineType)
    {
        RenderPipeline pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;
        XRRenderPipelineInstance instance = new();
        XRViewport viewport = new(null);

        ulong fullLayoutMask = pipeline.BuildResourceFeatureMaskForGenerationKey(instance, viewport);

        viewport.ApplyCapturePolicy(RenderCapturePolicy.GenericSceneCapture);
        viewport.CapturePolicy.UsesMinimalDirectFboPath.ShouldBeTrue();
        ulong captureMask = pipeline.BuildResourceFeatureMaskForGenerationKey(instance, viewport);

        captureMask.ShouldNotBe(fullLayoutMask);
        RenderPipelineResourceLayout captureLayout = pipeline.BuildResourceLayout(new RenderPipelineResourceProfile(
            640u,
            360u,
            320u,
            180u,
            OutputHDR: viewport.CapturePolicy.OutputHDR,
            EAntiAliasingMode.None,
            1u,
            Stereo: false,
            FeatureMask: captureMask,
            ExternalTargetKind: RenderPipelineExternalTargetKind.CallerProvidedFrameBuffer));
        captureLayout.OrderedSpecs.ShouldBeEmpty();
    }

    [TestCase(typeof(DefaultRenderPipeline))]
    [TestCase(typeof(DefaultRenderPipeline2))]
    public void DefaultPipelines_AtmosphereFogExactTransparencyAndDebugLayoutsDeclareDependencies(Type pipelineType)
    {
        const ulong exactTransparency = 1UL << 9;
        const ulong atmosphere = 1UL << 18;
        const ulong volumetricFog = 1UL << 19;
        const ulong debugVisualization = 1UL << 25;
        RenderPipeline pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;
        RenderPipelineResourceLayout layout = pipeline.BuildResourceLayout(CreateProfile(
            EAntiAliasingMode.Fxaa,
            msaaSamples: 1u,
            featureMask: exactTransparency | atmosphere | volumetricFog | debugVisualization));

        AssertQuadDependencies(
            layout,
            DefaultRenderPipeline.AtmosphereReprojectQuadFBOName,
            DefaultRenderPipeline.AtmosphereHalfScatterTextureName,
            DefaultRenderPipeline.AtmosphereHalfHistoryTextureName,
            DefaultRenderPipeline.AtmosphereHalfDepthTextureName);
        AssertQuadDependencies(
            layout,
            DefaultRenderPipeline.VolumetricFogReprojectQuadFBOName,
            DefaultRenderPipeline.VolumetricFogHalfScatterTextureName,
            DefaultRenderPipeline.VolumetricFogHalfHistoryTextureName,
            DefaultRenderPipeline.VolumetricFogHalfDepthTextureName);

        layout.ResourcesByName[DefaultRenderPipeline.PpllHeadPointerTextureName]
            .ShouldBeOfType<TextureSpec>().RequiresStorageUsage.ShouldBeTrue();
        layout.ResourcesByName.Keys.ShouldContain("PpllNodeBuffer");
        layout.ResourcesByName.Keys.ShouldContain("PpllCounterBuffer");
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.PpllResolveFBOName);

        AssertQuadDependencies(
            layout,
            DefaultRenderPipeline.FullOverdrawDebugFBOName,
            DefaultRenderPipeline.FullOverdrawCountTextureName,
            DefaultRenderPipeline.PostProcessOutputTextureName);
    }

    [Test]
    public void ResourceManager_RejectsFrameBufferFormatMismatch()
    {
        XRRenderPipelineInstance instance = new();
        RenderPipelineResourceLayoutBuilder builder = new();

        builder.Texture("Color")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
            .Format(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
            .SizedFormat(ESizedInternalFormat.Rgba8)
            .Factory(() => XRTexture2D.CreateFrameBufferTexture(
                64u,
                32u,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0))
            .Add();

        builder.FrameBuffer("ColorFBO")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, "Color")
            .Factory(() =>
            {
                XRTexture color = instance.GetTexture<XRTexture>("Color")
                    ?? throw new InvalidOperationException("Color texture was not available in build context.");
                return new XRFrameBuffer(((IFrameBufferAttachement)color, EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = "ColorFBO"
                };
            })
            .Add();

        RenderResourceGeneration generation = new(CreateKey(), builder.Build(RenderPipelineResourceProfile.Empty));
        try
        {
            RenderPipelineResourceManager manager = new();
            manager.Materialize(instance, generation).ShouldBeFalse();

            generation.Status.ShouldBe(RenderResourceGenerationStatus.Failed);
            generation.Diagnostics.ShouldContain(static x => x.Contains("format mismatch", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            generation.Dispose();
        }
    }

    [Test]
    public void ResourceManager_RejectsStereoTextureFactoryThatProducesOneLayer()
    {
        XRRenderPipelineInstance instance = new();
        RenderPipelineResourceLayoutBuilder builder = new();
        builder.Texture("StereoColor")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Layers(2u)
            .StereoCompatible()
            .Factory(() => XRTexture2D.CreateFrameBufferTexture(
                64u,
                32u,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0))
            .Add();

        RenderResourceGeneration generation = new(CreateKey(), builder.Build(RenderPipelineResourceProfile.Empty));
        try
        {
            new RenderPipelineResourceManager().Materialize(instance, generation).ShouldBeFalse();
            generation.Status.ShouldBe(RenderResourceGenerationStatus.Failed);
            generation.Diagnostics.ShouldContain(static x => x.Contains("layer mismatch", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            generation.Dispose();
        }
    }

    [Test]
    public void ResourceManager_RejectsStereoFramebufferThatSelectsOneArrayLayer()
    {
        XRRenderPipelineInstance instance = new();
        RenderPipelineResourceLayoutBuilder builder = new();
        builder.Texture("StereoColor")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Layers(2u)
            .StereoCompatible()
            .Factory(() =>
            {
                XRTexture2DArray texture = XRTexture2DArray.CreateFrameBufferTexture(
                    2u,
                    64u,
                    32u,
                    EPixelInternalFormat.Rgba16f,
                    EPixelFormat.Rgba,
                    EPixelType.HalfFloat,
                    EFrameBufferAttachment.ColorAttachment0);
                texture.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
                texture.OVRMultiViewParameters = new(0, 2u);
                return texture;
            })
            .Add();
        builder.FrameBuffer("StereoFBO")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, "StereoColor", mipLevel: 0, layerIndex: 0)
            .Factory(() =>
            {
                XRTexture2DArray texture = instance.GetTexture<XRTexture2DArray>("StereoColor")!;
                return new XRFrameBuffer((texture, EFrameBufferAttachment.ColorAttachment0, 0, 0));
            })
            .Add();

        RenderResourceGeneration generation = new(CreateKey(), builder.Build(RenderPipelineResourceProfile.Empty));
        try
        {
            new RenderPipelineResourceManager().Materialize(instance, generation).ShouldBeFalse();
            generation.Status.ShouldBe(RenderResourceGenerationStatus.Failed);
            generation.Diagnostics.ShouldContain(static x => x.Contains("layerIndex=-1", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            generation.Dispose();
        }
    }

    [Test]
    public void ResourceManager_RejectsTextureViewRangeMismatch()
    {
        XRRenderPipelineInstance instance = new();
        RenderPipelineResourceLayoutBuilder builder = new();

        builder.Texture("DepthStencil")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Format(EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248)
            .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
            .Factory(() => XRTexture2D.CreateFrameBufferTexture(
                64u,
                32u,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment))
            .Add();

        builder.TextureView("DepthView", "DepthStencil")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
            .DepthStencilAspect(EDepthStencilFmt.Depth)
            .MipRange(1u, 1u)
            .Factory(() =>
            {
                XRTexture2D source = instance.GetTexture<XRTexture2D>("DepthStencil")
                    ?? throw new InvalidOperationException("DepthStencil was not available in build context.");
                return new XRTexture2DView(source, 0u, 1u, ESizedInternalFormat.Depth24Stencil8, array: false, multisample: false)
                {
                    DepthStencilViewFormat = EDepthStencilFmt.Depth
                };
            })
            .Add();

        RenderResourceGeneration generation = new(CreateKey(), builder.Build(RenderPipelineResourceProfile.Empty));
        try
        {
            RenderPipelineResourceManager manager = new();
            manager.Materialize(instance, generation).ShouldBeFalse();

            generation.Status.ShouldBe(RenderResourceGenerationStatus.Failed);
            generation.Diagnostics.ShouldContain(static x => x.Contains("mip range mismatch", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            generation.Dispose();
        }
    }

    [Test]
    public void ResourceManager_AllowsSeededHistoryResourceWithoutInitialInstance()
    {
        XRRenderPipelineInstance instance = new();
        RenderPipelineResourceLayoutBuilder builder = new();

        builder.Texture("HistoryColor")
            .Size(RenderResourceSizePolicy.Absolute(64u, 32u))
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .History(RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            .Add();

        RenderResourceGeneration generation = new(CreateKey(), builder.Build(RenderPipelineResourceProfile.Empty));
        try
        {
            RenderPipelineResourceManager manager = new();
            manager.Materialize(instance, generation).ShouldBeTrue();

            generation.Status.ShouldBe(RenderResourceGenerationStatus.Ready);
            generation.Registry.TryGetTexture("HistoryColor", out _).ShouldBeFalse();
            generation.Diagnostics.ShouldContain(static x => x.Contains("seeded from the current frame", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            generation.Dispose();
        }
    }

    [Test]
    public void ResourceManager_MaterializesPendingGenerationIncrementally()
    {
        XRRenderPipelineInstance instance = new();
        RenderPipelineResourceLayoutBuilder builder = new();

        for (int i = 0; i < 3; i++)
        {
            string name = $"Color{i}";
            builder.Texture(name)
                .Size(RenderResourceSizePolicy.Absolute(16u, 16u))
                .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
                .Format(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
                .SizedFormat(ESizedInternalFormat.Rgba8)
                .Factory(() => XRTexture2D.CreateFrameBufferTexture(
                    16u,
                    16u,
                    EPixelInternalFormat.Rgba8,
                    EPixelFormat.Rgba,
                    EPixelType.UnsignedByte,
                    EFrameBufferAttachment.ColorAttachment0))
                .Add();
        }

        RenderResourceGeneration generation = new(CreateKey(), builder.Build(RenderPipelineResourceProfile.Empty));
        try
        {
            RenderPipelineResourceManager manager = new();

            manager.MaterializeIncremental(instance, generation, TimeSpan.MaxValue, maxSpecsPerSlice: 1, out bool completed).ShouldBeTrue();
            completed.ShouldBeFalse();
            generation.Status.ShouldBe(RenderResourceGenerationStatus.Building);
            generation.MaterializedSpecCount.ShouldBe(1);

            manager.MaterializeIncremental(instance, generation, TimeSpan.MaxValue, maxSpecsPerSlice: 1, out completed).ShouldBeTrue();
            completed.ShouldBeFalse();
            generation.MaterializedSpecCount.ShouldBe(2);

            manager.MaterializeIncremental(instance, generation, TimeSpan.MaxValue, maxSpecsPerSlice: 1, out completed).ShouldBeTrue();
            completed.ShouldBeTrue();
            generation.Status.ShouldBe(RenderResourceGenerationStatus.Ready);
            generation.MaterializedSpecCount.ShouldBe(3);
        }
        finally
        {
            generation.Dispose();
        }
    }

    [Test]
    public void PendingDescriptorParity_RejectsCompleteTextureContractDrift()
    {
        GenerationFailureTestPipeline pipeline = new();
        XRRenderPipelineInstance instance = new(pipeline);
        RenderPipelineResourceProfile profile = new(
            64u,
            32u,
            64u,
            32u,
            OutputHDR: false,
            EAntiAliasingMode.None,
            1u,
            Stereo: false);
        RenderResourceGeneration generation = new(
            CreateKey(),
            pipeline.BuildResourceLayout(profile));

        try
        {
            RenderPipelineResourceManager manager = new();
            manager.Materialize(instance, generation).ShouldBeTrue();
            RenderTextureResource record = generation.Registry.TextureRecords["StableColor"];
            generation.Registry.RegisterTextureDescriptor(record.Descriptor with
            {
                Usage = RenderPipelineResourceUsage.StorageImage,
                Samples = 4u,
                MipPolicy = new RenderResourceMipPolicy(0u, 3u),
            });

            InvalidOperationException exception = Should.Throw<InvalidOperationException>(
                () => RenderPipelineResourceManager.ValidateDescriptorLayoutParity(generation));

            exception.Message.ShouldContain("Pending descriptor/layout mismatch");
            exception.Message.ShouldContain("StableColor");
            exception.Message.ShouldContain("Expected=");
            exception.Message.ShouldContain("Actual=");
        }
        finally
        {
            generation.Dispose();
        }
    }

    [Test]
    public void PendingDescriptorParity_RejectsFrameBufferAttachmentDrift()
    {
        RenderPipelineResourceLayoutBuilder builder = new();
        builder.Texture("Color")
            .Size(RenderResourceSizePolicy.Absolute(16u, 16u))
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Format(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
            .SizedFormat(ESizedInternalFormat.Rgba8)
            .Factory(() => XRTexture2D.CreateFrameBufferTexture(
                16u,
                16u,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte,
                EFrameBufferAttachment.ColorAttachment0))
            .Add();
        builder.FrameBuffer("ColorFBO")
            .Color(0, "Color")
            .Factory(() => new XRFrameBuffer(
                (new XRTexture2D(), EFrameBufferAttachment.ColorAttachment0, 0, -1)))
            .Add();

        RenderResourceGeneration generation = new(CreateKey(), builder.Build(RenderPipelineResourceProfile.Empty));
        TextureResourceDescriptor textureDescriptor = generation.Layout
            .OrderedSpecs
            .OfType<TextureSpec>()
            .Single()
            .ToDescriptor();
        FrameBufferResourceDescriptor expected = generation.Layout
            .OrderedSpecs
            .OfType<FrameBufferSpec>()
            .Single()
            .ToDescriptor();
        generation.Registry.RegisterTextureDescriptor(textureDescriptor);
        generation.Registry.RegisterFrameBufferDescriptor(expected with
        {
            Attachments = Array.Empty<FrameBufferAttachmentDescriptor>(),
        });

        try
        {
            InvalidOperationException exception = Should.Throw<InvalidOperationException>(
                () => RenderPipelineResourceManager.ValidateDescriptorLayoutParity(generation));

            exception.Message.ShouldContain("Pending descriptor/layout mismatch");
            exception.Message.ShouldContain("ColorFBO");
        }
        finally
        {
            generation.Dispose();
        }
    }

    [Test]
    public void ExternalSwapchainViewportResourceDimensionsPreserveDisplayAndScaledInternalEyeExtents()
    {
        XRViewport viewport = new(null)
        {
            Width = 896,
            Height = 1007,
            RendersToExternalSwapchainTarget = true
        };
        viewport.SetInternalResolution(600, 674, correctAspect: false);

        var dimensions = XRRenderPipelineInstance.ResolveViewportResourceDimensions(viewport);

        dimensions.DisplayWidth.ShouldBe(896);
        dimensions.DisplayHeight.ShouldBe(1007);
        dimensions.InternalWidth.ShouldBe(600);
        dimensions.InternalHeight.ShouldBe(674);
    }

    [Test]
    public void ExternalSwapchainFrameOpResourceDimensionsPreserveSubNativeInternalExtent()
    {
        Extent2D externalExtent = new(896u, 1007u);

        var dimensions = VulkanRenderer.ResolveExternalFrameOpResourceDimensions(
            externalExtent,
            pipelineInternalWidth: 600u,
            pipelineInternalHeight: 674u,
            viewportInternalWidth: 896,
            viewportInternalHeight: 1007,
            contextInternalWidth: 896u,
            contextInternalHeight: 1007u);

        dimensions.DisplayWidth.ShouldBe(896u);
        dimensions.DisplayHeight.ShouldBe(1007u);
        dimensions.InternalWidth.ShouldBe(600u);
        dimensions.InternalHeight.ShouldBe(674u);
    }

    [Test]
    public void ExternalSwapchainFrameOpResourceDimensionsFallBackToViewportInternalExtent()
    {
        Extent2D externalExtent = new(896u, 1007u);

        var dimensions = VulkanRenderer.ResolveExternalFrameOpResourceDimensions(
            externalExtent,
            pipelineInternalWidth: null,
            pipelineInternalHeight: null,
            viewportInternalWidth: 600,
            viewportInternalHeight: 674);

        dimensions.DisplayWidth.ShouldBe(896u);
        dimensions.DisplayHeight.ShouldBe(1007u);
        dimensions.InternalWidth.ShouldBe(600u);
        dimensions.InternalHeight.ShouldBe(674u);
    }

    [Test]
    public void WindowViewportResourceDimensionsPreserveDisplayAndInternalExtents()
    {
        XRViewport viewport = new(null)
        {
            Width = 1920,
            Height = 1080
        };
        viewport.SetInternalResolution(1280, 720, correctAspect: false);

        var dimensions = XRRenderPipelineInstance.ResolveViewportResourceDimensions(viewport);

        dimensions.DisplayWidth.ShouldBe(1920);
        dimensions.DisplayHeight.ShouldBe(1080);
        dimensions.InternalWidth.ShouldBe(1280);
        dimensions.InternalHeight.ShouldBe(720);
    }

    [Test]
    public void RetainedNonV2Pipelines_DeclareTheirOwnedResources()
    {
        RenderPipelineResourceProfile profile = CreateProfile(EAntiAliasingMode.None, 1u);

        RenderPipelineResourceLayout ui = new UserInterfaceRenderPipeline().BuildResourceLayout(profile);
        ui.ResourcesByName.Keys.OrderBy(static x => x).ShouldBe(new[]
        {
            UserInterfaceRenderPipeline.DepthStencilTextureName,
            UserInterfaceRenderPipeline.DepthViewTextureName,
            UserInterfaceRenderPipeline.StencilViewTextureName,
        }.OrderBy(static x => x));
        ui.ResourcesByName[UserInterfaceRenderPipeline.DepthViewTextureName].ShouldBeOfType<TextureViewSpec>();
        ui.ResourcesByName[UserInterfaceRenderPipeline.StencilViewTextureName].ShouldBeOfType<TextureViewSpec>();

        RenderPipelineResourceLayout test = new XREngine.Rendering.TestRenderPipeline().BuildResourceLayout(profile);
        test.ResourcesByName.Count.ShouldBe(3);
        test.ResourcesByName["DepthStencil"].ShouldBeOfType<TextureSpec>();
        test.ResourcesByName["HDRSceneTex"].ShouldBeOfType<TextureSpec>();
        test.ResourcesByName["InternalResFBO"].ShouldBeOfType<FrameBufferSpec>();

        RenderPipelineResourceLayout surfel = new SurfelDebugRenderPipeline().BuildResourceLayout(profile);
        surfel.ResourcesByName.Count.ShouldBe(11);
        surfel.ResourcesByName[SurfelDebugRenderPipeline.GBufferFBOName].ShouldBeOfType<FrameBufferSpec>();
        surfel.ResourcesByName[SurfelDebugRenderPipeline.ForwardPassFBOName].ShouldBeOfType<FrameBufferSpec>();
        surfel.ResourcesByName[SurfelDebugRenderPipeline.TransformIdDebugFBOName].ShouldBeOfType<QuadMaterialSpec>();
        surfel.ResourcesByName[SurfelDebugRenderPipeline.SurfelDebugFBOName].ShouldBeOfType<QuadMaterialSpec>();
    }

    [Test]
    public void DefaultRenderPipeline2_DeclaresCompleteCoreAndSmaaLayouts()
    {
        DefaultRenderPipeline2 pipeline = new();
        RenderPipelineResourceLayout core = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, 1u));
        RenderPipelineResourceLayout smaa = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Smaa, 1u));

        core.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline2.DepthStencilTextureName);
        core.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline2.DeferredGBufferFBOName);
        core.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline2.PostProcessOutputFBOName);
        core.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline2.FxaaFBOName);

        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline2.SmaaEdgeTextureName);
        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline2.SmaaBlendTextureName);
        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline2.SmaaOutputTextureName);
        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline2.SmaaEdgeFBOName);
        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline2.SmaaBlendFBOName);
        smaa.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline2.SmaaFBOName);
    }

    [TestCase(typeof(DefaultRenderPipeline))]
    [TestCase(typeof(DefaultRenderPipeline2))]
    public void DefaultPipelines_DeclareSceneOwnedProbeImports(Type pipelineType)
    {
        RenderPipeline pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;
        RenderPipelineResourceLayout layout = pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.None, 1u));
        string[] textureNames = ["LightProbeIrradianceArray", "LightProbePrefilterArray"];
        string[] bufferNames =
        [
            "LightProbePositions",
            "LightProbeParameters",
            "LightProbeTetrahedra",
            "LightProbeGridCells",
            "LightProbeGridIndices",
        ];

        foreach (string name in textureNames)
            AssertProbeImport(layout, name, ExternalRenderResourceKind.Texture);
        foreach (string name in bufferNames)
            AssertProbeImport(layout, name, ExternalRenderResourceKind.Buffer);
    }

    [TestCase(typeof(DefaultRenderPipeline), RenderPipelineExternalTargetKind.Window,
        ExternalRenderResourceOwnership.Window, ExternalRenderResourceSynchronization.FrameBoundary)]
    [TestCase(typeof(DefaultRenderPipeline), RenderPipelineExternalTargetKind.CallerProvidedFrameBuffer,
        ExternalRenderResourceOwnership.Caller, ExternalRenderResourceSynchronization.CallerProvided)]
    [TestCase(typeof(DefaultRenderPipeline), RenderPipelineExternalTargetKind.ExternalSwapchain,
        ExternalRenderResourceOwnership.XrRuntime, ExternalRenderResourceSynchronization.AcquireRelease)]
    [TestCase(typeof(DefaultRenderPipeline2), RenderPipelineExternalTargetKind.Window,
        ExternalRenderResourceOwnership.Window, ExternalRenderResourceSynchronization.FrameBoundary)]
    [TestCase(typeof(DefaultRenderPipeline2), RenderPipelineExternalTargetKind.CallerProvidedFrameBuffer,
        ExternalRenderResourceOwnership.Caller, ExternalRenderResourceSynchronization.CallerProvided)]
    [TestCase(typeof(DefaultRenderPipeline2), RenderPipelineExternalTargetKind.ExternalSwapchain,
        ExternalRenderResourceOwnership.XrRuntime, ExternalRenderResourceSynchronization.AcquireRelease)]
    public void DefaultPipelines_DeclareImportedOutputOwnershipBoundaries(
        Type pipelineType,
        RenderPipelineExternalTargetKind targetKind,
        ExternalRenderResourceOwnership ownership,
        ExternalRenderResourceSynchronization synchronization)
    {
        RenderPipeline pipeline = (RenderPipeline)Activator.CreateInstance(pipelineType)!;
        RenderPipelineResourceProfile baselineProfile = CreateProfile(EAntiAliasingMode.None, 1u);
        RenderPipelineResourceLayout baseline = pipeline.BuildResourceLayout(baselineProfile);
        RenderPipelineResourceLayout imported = pipeline.BuildResourceLayout(
            baselineProfile with { ExternalTargetKind = targetKind });

        ExternalResourceSpec output = imported.ResourcesByName["$ExternalOutput"].ShouldBeOfType<ExternalResourceSpec>();
        output.ExternalKind.ShouldBe(ExternalRenderResourceKind.FrameBuffer);
        output.Ownership.ShouldBe(ownership);
        output.Synchronization.ShouldBe(synchronization);
        imported.OrderedSpecs.Count(spec => spec.Lifetime != RenderResourceLifetime.External)
            .ShouldBe(baseline.OrderedSpecs.Count(spec => spec.Lifetime != RenderResourceLifetime.External));
    }

    [Test]
    public void DefaultPipelines_UseImportedProbeBindingsWithoutDirectRegistryMutation()
    {
        string[] sources =
        [
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default2/DefaultRenderPipeline2.cs",
        ];

        foreach (string sourcePath in sources)
        {
            string source = ReadWorkspaceFile(sourcePath);
            source.ShouldContain("BindImportedTexture(");
            source.ShouldContain("BindImportedBuffer(");
            source.ShouldContain("UnbindImportedTexture(");
            source.ShouldContain("UnbindImportedBuffer(");
            source.ShouldNotContain("Resources.RemoveTexture(");
            source.ShouldNotContain("Resources.RemoveBuffer(");
        }
    }

    [Test]
    public void RetirementBackpressure_WaitsForEveryRendererBackend()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        int helperStart = source.IndexOf(
            "private static void WaitForGpuBeforePhysicalResourceDestruction",
            StringComparison.Ordinal);
        int helperEnd = source.IndexOf("\n    }", helperStart, StringComparison.Ordinal);
        string helper = source[helperStart..helperEnd];

        helper.ShouldContain("renderer.WaitForGpu();");
        helper.ShouldContain("renderer is not VulkanRenderer vulkanRenderer");
        helper.ShouldNotContain("Current is not VulkanRenderer");
    }

    [Test]
    public void VulkanReadbackScope_UsesCapturedRenderedFrameGenerationAndTarget()
    {
        string plannerSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        int matchStart = plannerSource.IndexOf(
            "private static bool FrameOpContextMatchesPlannerStateKey",
            StringComparison.Ordinal);
        int matchEnd = plannerSource.IndexOf(";", matchStart, StringComparison.Ordinal);
        string matchBody = plannerSource[matchStart..matchEnd];
        matchBody.ShouldContain("context.ResourceGeneration == key.ResourceGeneration");
        matchBody.ShouldContain("ResolveResourcePlanOutputTargetIdentity(context) == key.OutputTargetIdentity");

        string readbackSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Readback.cs");
        Regex.Matches(
            readbackSource,
            "_lastWindowPresentFrameOpContext is \\{ \\} context\\s+\\? EnterFrameOpResourcePlannerReadbackScope\\(in context\\)")
            .Count
            .ShouldBe(2);
        readbackSource.ShouldContain("_lastWindowPresentFrameBuffer");
        readbackSource.ShouldContain("_lastWindowPresentColorTexture");
    }

    [Test]
    public void VulkanSwapchainRecreation_DoesNotWaitForWholeDeviceIdle()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");
        int methodStart = source.IndexOf("private bool RecreateSwapChain()", StringComparison.Ordinal);
        int methodEnd = source.IndexOf("private void DestroyAllSwapChainObjects()", methodStart, StringComparison.Ordinal);
        string method = source[methodStart..methodEnd];

        method.ShouldContain("WaitForAllInFlightWork();");
        method.ShouldContain("WaitForQueueIdleTracked(presentQueue)");
        method.ShouldNotContain("DeviceWaitIdle();");
    }

    [Test]
    public void RuntimeCommandAssembly_HasNoCacheOrCreateCommandTypes()
    {
        string retiredNameFragment = "Cache" + "OrCreate";
        typeof(ViewportRenderCommand).Assembly.GetTypes()
            .Where(type => type.Name.Contains(retiredNameFragment, StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [Test]
    public void PipelineCommandTrees_DoNotAuthorResourceLifecycleMutation()
    {
        string[] commandSources =
        [
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default2/DefaultRenderPipeline2.CommandChain.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/UserInterfaceRenderPipeline.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/SurfelDebugRenderPipeline.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/TestRenderPipeline.cs",
        ];
        string[] forbidden = ["SetTexture(", "SetFBO(", "SetBuffer(", "SetRenderBuffer(", "Resources.Remove"];

        foreach (string sourcePath in commandSources)
        {
            string source = ReadWorkspaceFile(sourcePath);
            foreach (string token in forbidden)
                source.ShouldNotContain(token);
        }
    }

    [Test]
    public void ReachableAoAndBloomCommands_DoNotMutateGenerationRegistries()
    {
        string[] commandSources =
        [
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_AODisabledPass.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_SSAOPass.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_MVAOPass.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_MSVO.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_HBAOPlusPass.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_GTAOPass.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_SpatialHashAOPass.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_BloomPass.cs",
        ];
        string[] forbidden = ["SetTexture(", "SetFBO(", "SetBuffer(", "SetRenderBuffer(", "Resources.Remove", ".Destroy("];

        foreach (string sourcePath in commandSources)
        {
            string source = ReadWorkspaceFile(sourcePath);
            foreach (string token in forbidden)
                source.ShouldNotContain(token);
        }
    }

    [Test]
    public void ReachableGiAndForwardPlusCommands_DoNotCreateOrDestroyPipelineBuffers()
    {
        string[] commandSources =
        [
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/GI/VPRC_ReSTIRPass.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/GI/VPRC_RadianceCascadesPass.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/GI/VPRC_SurfelGIPass.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardPlusLightCullingPass.cs",
        ];
        string[] forbidden = ["EnsureBuffers(", "DestroyBuffer(", ".Destroy(", "instance.SetBuffer(", "Resources.RemoveBuffer("];

        foreach (string sourcePath in commandSources)
        {
            string source = ReadWorkspaceFile(sourcePath);
            foreach (string token in forbidden)
                source.ShouldNotContain(token);
        }
    }

    [Test]
    public void LegacyRegistryMutatingCommands_AreNotScriptRegistered()
    {
        string[] commandSources =
        [
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ApplyLUT.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_BilateralFilter.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ColorGrading.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_DownsampleChain.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_GaussianBlur.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_ConvolveCubemap.cs",
        ];

        foreach (string sourcePath in commandSources)
            ReadWorkspaceFile(sourcePath).ShouldNotContain("[RenderPipelineScriptCommand]");
    }

    [Test]
    public void NonV2PipelineSources_DoNotAuthorCacheOrCreateCommands()
    {
        string[] sources =
        [
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default2/DefaultRenderPipeline2.CommandChain.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/UserInterfaceRenderPipeline.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/SurfelDebugRenderPipeline.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/TestRenderPipeline.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/RvcRenderPipeline.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/CustomRenderPipeline.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DebugOpaqueRenderPipeline.cs",
        ];

        string retiredNameFragment = "VPRC_" + "CacheOrCreate";
        foreach (string sourcePath in sources)
            ReadWorkspaceFile(sourcePath).ShouldNotContain(retiredNameFragment);
    }

    private static void AssertGtaoScratchScale(RenderPipelineResourceLayout layout, float expectedScale)
    {
        TextureSpec raw = layout.ResourcesByName[DefaultRenderPipeline.GTAORawTextureName].ShouldBeOfType<TextureSpec>();
        TextureSpec intermediate = layout.ResourcesByName[DefaultRenderPipeline.GTAOBlurIntermediateTextureName].ShouldBeOfType<TextureSpec>();

        raw.SizePolicy.SizeClass.ShouldBe(RenderResourceSizeClass.InternalResolution);
        intermediate.SizePolicy.SizeClass.ShouldBe(RenderResourceSizeClass.InternalResolution);
        raw.SizePolicy.ScaleX.ShouldBe(expectedScale, 0.0001f);
        raw.SizePolicy.ScaleY.ShouldBe(expectedScale, 0.0001f);
        intermediate.SizePolicy.ScaleX.ShouldBe(expectedScale, 0.0001f);
        intermediate.SizePolicy.ScaleY.ShouldBe(expectedScale, 0.0001f);
    }

    private static void AssertProbeImport(
        RenderPipelineResourceLayout layout,
        string name,
        ExternalRenderResourceKind kind)
    {
        ExternalResourceSpec spec = layout.ResourcesByName[name].ShouldBeOfType<ExternalResourceSpec>();
        spec.ExternalKind.ShouldBe(kind);
        spec.Ownership.ShouldBe(ExternalRenderResourceOwnership.Scene);
        spec.Synchronization.ShouldBe(ExternalRenderResourceSynchronization.FrameBoundary);
    }

    private static void AssertStereoTextureFactory(RenderPipelineResourceLayout layout, string textureName)
    {
        TextureSpec spec = layout.ResourcesByName[textureName].ShouldBeOfType<TextureSpec>();
        spec.Layers.ShouldBe(2u, textureName);
        spec.StereoCompatible.ShouldBeTrue(textureName);
        spec.Factory.ShouldNotBeNull(textureName);

        XRTexture2DArray texture = spec.Factory!().ShouldBeOfType<XRTexture2DArray>(textureName);
        texture.Depth.ShouldBe(2u, textureName);
        texture.OVRMultiViewParameters.ShouldNotBeNull(textureName);
        texture.OVRMultiViewParameters!.Offset.ShouldBe(0, textureName);
        texture.OVRMultiViewParameters.NumViews.ShouldBe(2u, textureName);
        texture.Mipmaps.ShouldNotBeEmpty(textureName);
        texture.Mipmaps[0].InternalFormat.ShouldBe(spec.InternalFormat!.Value, textureName);
        texture.Mipmaps[0].PixelFormat.ShouldBe(spec.PixelFormat!.Value, textureName);
        texture.Mipmaps[0].PixelType.ShouldBe(spec.PixelType!.Value, textureName);
        texture.SizedInternalFormat.ShouldBe(spec.SizedInternalFormat!.Value, textureName);
    }

    private static void AssertStereoFramebufferAttachment(
        RenderPipelineResourceLayout layout,
        string frameBufferName,
        string textureName)
    {
        FrameBufferSpec frameBuffer = layout.ResourcesByName[frameBufferName].ShouldBeOfType<FrameBufferSpec>();
        FrameBufferAttachmentDescriptor attachment = frameBuffer.Attachments.Single(x =>
            string.Equals(x.ResourceName, textureName, StringComparison.Ordinal));
        attachment.MipLevel.ShouldBe(0);
        attachment.LayerIndex.ShouldBe(-1);
    }

    private static void AssertQuadDependencies(
        RenderPipelineResourceLayout layout,
        string quadName,
        params string[] dependencies)
    {
        QuadMaterialSpec quad = layout.ResourcesByName[quadName].ShouldBeOfType<QuadMaterialSpec>();
        foreach (string dependency in dependencies)
            quad.Dependencies.ShouldContain(dependency, quadName);
    }

    private static RenderPipelineResourceProfile CreateProfile(
        EAntiAliasingMode aaMode,
        uint msaaSamples,
        ulong featureMask = 0UL,
        bool stereo = false)
        => new(
            DisplayWidth: 1280u,
            DisplayHeight: 720u,
            InternalWidth: 1280u,
            InternalHeight: 720u,
            OutputHDR: false,
            AntiAliasingMode: aaMode,
            MsaaSampleCount: msaaSamples,
            Stereo: stereo,
            FeatureMask: featureMask);

    private static VulkanResourcePlanner CreateSingleTexturePlanner(string textureName, EPixelFormat pixelFormat)
    {
        RenderResourceRegistry registry = new();
        registry.RegisterTextureDescriptor(new TextureResourceDescriptor(
            textureName,
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(128u, 64u),
            FormatLabel: ESizedInternalFormat.Rgba16f.ToString(),
            Usage: RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment,
            SizedInternalFormat: ESizedInternalFormat.Rgba16f,
            PixelFormat: pixelFormat));

        VulkanResourcePlanner planner = new();
        planner.Sync(registry);
        return planner;
    }

    private static ResourceGenerationKey CreateKey()
        => new(
            PipelineName: "TestPipeline",
            DisplayWidth: 64u,
            DisplayHeight: 32u,
            InternalWidth: 64u,
            InternalHeight: 32u,
            OutputHDR: false,
            AntiAliasingMode: EAntiAliasingMode.None,
            MsaaSampleCount: 1u,
            Stereo: false);

    private static bool PreparePendingGeneration(XRRenderPipelineInstance instance)
    {
        MethodInfo method = typeof(XRRenderPipelineInstance).GetMethod(
            "TryPreparePendingGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(bool), typeof(TimeSpan), typeof(int)],
            modifiers: null).ShouldNotBeNull();
        return (bool)method.Invoke(instance, ["UnitTest", true, TimeSpan.MaxValue, int.MaxValue])!;
    }

    private static ResourceGenerationKey BuildGenerationKey(
        XRRenderPipelineInstance instance,
        int displayWidth,
        int displayHeight,
        int internalWidth,
        int internalHeight,
        XRViewport? viewport = null)
    {
        MethodInfo method = typeof(XRRenderPipelineInstance).GetMethod(
            "BuildResourceGenerationKey",
            BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
        return (ResourceGenerationKey)method.Invoke(
            instance,
            [displayWidth, displayHeight, internalWidth, internalHeight, viewport])!;
    }

    private static bool RequestGenerationKey(
        XRRenderPipelineInstance instance,
        ResourceGenerationKey key,
        string reason,
        bool force = false)
    {
        MethodInfo method = typeof(XRRenderPipelineInstance).GetMethod(
            "RequestResourceGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(ResourceGenerationKey), typeof(string), typeof(bool)],
            modifiers: null).ShouldNotBeNull();
        return (bool)method.Invoke(instance, [key, reason, force])!;
    }

    private static void SetEffectiveFrameProfile(
        XRRenderPipelineInstance instance,
        bool outputHdr,
        EAntiAliasingMode antiAliasingMode,
        uint msaaSamples)
    {
        SetPrivateProperty(instance, nameof(XRRenderPipelineInstance.EffectiveOutputHDRThisFrame), outputHdr);
        SetPrivateProperty(instance, nameof(XRRenderPipelineInstance.EffectiveAntiAliasingModeThisFrame), antiAliasingMode);
        SetPrivateProperty(instance, nameof(XRRenderPipelineInstance.EffectiveMsaaSampleCountThisFrame), msaaSamples);
    }

    private static void SetPrivateProperty<T>(XRRenderPipelineInstance instance, string propertyName, T value)
        => typeof(XRRenderPipelineInstance).GetProperty(propertyName)!.SetValue(instance, value);

    private static void AssertOnlyKeyFieldsChanged(
        ResourceGenerationKey baseline,
        ResourceGenerationKey changed,
        params string[] expectedChangedFields)
    {
        string[] changedFields = typeof(ResourceGenerationKey)
            .GetProperties()
            .Where(property => !Equals(property.GetValue(baseline), property.GetValue(changed)))
            .Select(property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        changedFields.ShouldBe(expectedChangedFields.OrderBy(static name => name, StringComparer.Ordinal).ToArray());
    }

    private readonly record struct BackendGenerationSnapshot(
        long PlannerRevision,
        long AllocatorOwnershipId,
        long DescriptorSignature,
        long AllocationSignature,
        ulong ImageHandle,
        ulong BufferHandle,
        ulong FrameBufferHandle,
        long MetadataRevision);

    private sealed class TestResourceGenerationBackend(BackendGenerationSnapshot activeSnapshot)
        : IRenderResourceGenerationBackend
    {
        public BackendGenerationSnapshot ActiveSnapshot { get; private set; } = activeSnapshot;
        public BackendGenerationSnapshot PendingSnapshot { get; init; }
        public string? FailureReason { get; init; }
        public Action? OnCommit { get; set; }
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }

        public bool TryPrepareRenderResourceGeneration(
            XRRenderPipelineInstance pipeline,
            RenderResourceGeneration generation,
            XRViewport? viewport,
            out IRenderResourceGenerationTransaction? transaction,
            out string? failureReason)
        {
            if (!string.IsNullOrWhiteSpace(FailureReason))
            {
                transaction = null;
                failureReason = FailureReason;
                return false;
            }

            transaction = new Transaction(this, PendingSnapshot);
            failureReason = null;
            return true;
        }

        private sealed class Transaction(
            TestResourceGenerationBackend backend,
            BackendGenerationSnapshot pendingSnapshot) : IRenderResourceGenerationTransaction
        {
            private bool _committed;

            public void Commit()
            {
                backend.OnCommit?.Invoke();
                backend.ActiveSnapshot = pendingSnapshot;
                backend.CommitCount++;
                _committed = true;
            }

            public void Dispose()
            {
                if (!_committed)
                    backend.RollbackCount++;
            }
        }
    }

    private sealed class GenerationFailureTestPipeline : RenderPipeline
    {
        public bool FailFactories { get; set; }
        public bool StereoResources { get; set; }
        public ulong FeatureMask { get; set; }
        public bool IncludeExternalTexture { get; set; }

        public GenerationFailureTestPipeline() : base(deferCommandChainGeneration: true)
        {
            CommandChain = GenerateCommandChain();
            PassIndicesAndSorters = GetPassIndicesAndSorters();
        }

        protected override Lazy<XRMaterial> InvalidMaterialFactory => new(() => new XRMaterial());

        protected override ViewportRenderCommandContainer GenerateCommandChain() => [];

        protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters() => [];

        internal override bool UsesStereoResources(XRRenderPipelineInstance instance, XRViewport? viewport)
            => StereoResources;

        internal override ulong BuildResourceFeatureMaskForGenerationKey(XRRenderPipelineInstance instance, XRViewport? viewport)
            => FeatureMask;

        protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
        {
            uint width = Math.Max(builder.Profile.InternalWidth, 1u);
            uint height = Math.Max(builder.Profile.InternalHeight, 1u);
            builder.Texture("StableColor")
                .Size(RenderResourceSizePolicy.Internal())
                .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
                .Format(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
                .SizedFormat(ESizedInternalFormat.Rgba8)
                .Factory(() => CreateTexture("StableColor", width, height))
                .Add();
            builder.Texture("SecondColor")
                .Size(RenderResourceSizePolicy.Internal())
                .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
                .Format(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
                .SizedFormat(ESizedInternalFormat.Rgba8)
                .Factory(() => CreateTexture("SecondColor", width, height))
                .Add();

            if (IncludeExternalTexture)
            {
                builder.External("ImportedProbe")
                    .Contract(
                        ExternalRenderResourceKind.Texture,
                        ExternalRenderResourceOwnership.Scene,
                        ExternalRenderResourceSynchronization.BackendManaged)
                    .Add();
            }
        }

            private XRTexture CreateTexture(string name, uint width, uint height)
        {
            if (FailFactories)
                throw new InvalidOperationException($"Injected factory failure for {name}.");

            XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
                width,
                height,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte,
                EFrameBufferAttachment.ColorAttachment0);
            texture.Name = name;
            return texture;
        }
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        foreach (string start in new[] { TestContext.CurrentContext.TestDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                    return File.ReadAllText(Path.Combine(directory.FullName, relativePath)).Replace("\r\n", "\n");
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the XRENGINE workspace root.");
    }
}
