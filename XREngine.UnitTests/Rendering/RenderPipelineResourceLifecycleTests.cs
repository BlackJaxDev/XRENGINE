using System.IO;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering;
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
        layout.ResourcesByName.Keys.ShouldNotContain(DefaultRenderPipeline.LightCombineFBOName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.PostProcessOutputFBOName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.FinalPostProcessOutputFBOName);
        layout.ResourcesByName.Keys.ShouldContain(DefaultRenderPipeline.FxaaFBOName);
        layout.ResourcesByName.Keys.ShouldNotContain(DefaultRenderPipeline.TsrUpscaleFBOName);

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
    public void DefaultRenderPipeline_LightCombineFbo_RemainsCommandOwnedUntilAoMigration()
    {
        string source = File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs"))
            .Replace("\r\n", "\n");

        source.ShouldContain("LightCombineFBOName,\n            CreateLightCombineFBO,\n            GetDesiredFBOSizeInternal,\n            NeedsRecreateLightCombineFbo)");
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

    private static RenderPipelineResourceProfile CreateProfile(EAntiAliasingMode aaMode, uint msaaSamples)
        => new(
            DisplayWidth: 1280u,
            DisplayHeight: 720u,
            InternalWidth: 1280u,
            InternalHeight: 720u,
            OutputHDR: false,
            AntiAliasingMode: aaMode,
            MsaaSampleCount: msaaSamples,
            Stereo: false,
            UseVulkanSafeFeatureProfile: false);

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
