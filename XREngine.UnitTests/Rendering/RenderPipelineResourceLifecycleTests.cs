using System.IO;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering;
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
    public void VulkanBarrierPlanner_PhysicalTrackingWinsOverLogicalViewSyncEdges()
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
            barrier.PassIndex == 101 &&
            barrier.ResourceName.Equals("DepthStencil", StringComparison.OrdinalIgnoreCase) &&
            barrier.Previous.Layout == ImageLayout.DepthStencilAttachmentOptimal &&
            barrier.Next.Layout == ImageLayout.DepthStencilReadOnlyOptimal);
    }

    [Test]
    public void QuadBlit_PostProcessOutputMetadata_SamplesDepthStencilWithoutAttachingDestinationDepth()
    {
        RenderPassMetadataCollection metadata = new();
        RenderGraphDescribeContext context = new(metadata);

        new VPRC_RenderQuadToFBO()
            .SetTargets(DefaultRenderPipeline.PostProcessFBOName, DefaultRenderPipeline.PostProcessOutputFBOName)
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

        string source = File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs"))
            .Replace("\r\n", "\n");

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
    public void DefaultRenderPipeline_GtaoScratchResources_FollowResolutionFeatureMask()
    {
        DefaultRenderPipeline pipeline = new();

        AssertGtaoScratchScale(
            pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, msaaSamples: 1u)),
            0.5f);
        AssertGtaoScratchScale(
            pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, msaaSamples: 1u, featureMask: 1UL << 6)),
            1.0f);
        AssertGtaoScratchScale(
            pipeline.BuildResourceLayout(CreateProfile(EAntiAliasingMode.Fxaa, msaaSamples: 1u, featureMask: 1UL << 7)),
            0.25f);
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
            nameof(ResourceGenerationKey.InternalWidth),
            nameof(ResourceGenerationKey.InternalHeight),
            nameof(ResourceGenerationKey.OutputHDR),
            nameof(ResourceGenerationKey.AntiAliasingMode),
            nameof(ResourceGenerationKey.MsaaSampleCount),
            nameof(ResourceGenerationKey.Stereo),
            nameof(ResourceGenerationKey.UseVulkanSafeFeatureProfile),
            nameof(ResourceGenerationKey.FeatureMask),
            nameof(ResourceGenerationKey.ReservedViewCount),
            nameof(ResourceGenerationKey.ReservedEyeIndex),
        ];

        propertyNames.ShouldBe(expectedProperties.OrderBy(static name => name, StringComparer.Ordinal).ToArray());

        propertyNames.ShouldNotContain("Camera");
        propertyNames.ShouldNotContain("CameraTransform");
        propertyNames.ShouldNotContain("ViewMatrix");
        propertyNames.ShouldNotContain("ProjectionMatrix");
    }

    [Test]
    public void DefaultRenderPipeline_ResourceFeatureMaskIsStableUntilStructuralFeatureChanges()
    {
        DefaultRenderPipeline pipeline = new();

        ulong first = pipeline.BuildResourceFeatureMaskForGenerationKey();
        ulong second = pipeline.BuildResourceFeatureMaskForGenerationKey();

        second.ShouldBe(first);

        pipeline.EnableDeferredMsaa = !pipeline.EnableDeferredMsaa;

        pipeline.BuildResourceFeatureMaskForGenerationKey().ShouldNotBe(first);
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

    private static RenderPipelineResourceProfile CreateProfile(EAntiAliasingMode aaMode, uint msaaSamples, ulong featureMask = 0UL)
        => new(
            DisplayWidth: 1280u,
            DisplayHeight: 720u,
            InternalWidth: 1280u,
            InternalHeight: 720u,
            OutputHDR: false,
            AntiAliasingMode: aaMode,
            MsaaSampleCount: msaaSamples,
            Stereo: false,
            UseVulkanSafeFeatureProfile: false,
            FeatureMask: featureMask);

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
            Stereo: false,
            UseVulkanSafeFeatureProfile: false);
}
