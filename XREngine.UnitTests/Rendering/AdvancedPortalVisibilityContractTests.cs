using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedPortalVisibilityContractTests
{
    [Test]
    public void StencilTransitionWriteMask_TransitionsAllPortalNodePairsWithoutTouchingNonPortalBits()
    {
        for (uint fromNode = 0u; fromNode <= AdvancedStencilBitLayout.MaxPortalNodeId; ++fromNode)
        {
            for (uint toNode = 0u; toNode <= AdvancedStencilBitLayout.MaxPortalNodeId; ++toNode)
            {
                uint writeMask = AdvancedStencilBitLayout.ComputePortalTransitionWriteMask(fromNode, toNode);
                (writeMask & ~AdvancedStencilBitLayout.PortalNodeMask).ShouldBe(0u);

                // Verify against every combination of highlight bits (0..1) and gizmo bit (7).
                uint[] nonPortalStates = [0x00u, 0x01u, 0x02u, 0x03u, 0x80u, 0x81u, 0x82u, 0x83u];
                foreach (uint nonPortal in nonPortalStates)
                {
                    uint initial = AdvancedStencilBitLayout.EncodePortalNode(fromNode) | nonPortal;
                    // Hardware StencilOp.Invert flips every bit enabled in writeMask.
                    uint transitioned = (initial & ~writeMask) | ((~initial) & writeMask);
                    AdvancedStencilBitLayout.DecodePortalNode(transitioned).ShouldBe(toNode);
                    (transitioned & ~AdvancedStencilBitLayout.PortalNodeMask).ShouldBe(nonPortal);
                }
            }
        }
    }

    [Test]
    public void PortalVisibilityPhase_SitsBetweenLateRasterAndMultisampleResolve()
    {
        ((byte)EAdvancedVisibilityStageBackendPhase.PortalVisibility)
            .ShouldBeGreaterThan((byte)EAdvancedVisibilityStageBackendPhase.LateRaster);
        ((byte)EAdvancedVisibilityStageBackendPhase.PortalVisibility)
            .ShouldBeLessThan((byte)EAdvancedVisibilityStageBackendPhase.MultisampleResolve);
        ((byte)EAdvancedVisibilityStageBackendPhase.MultisampleResolve)
            .ShouldBeLessThan((byte)EAdvancedVisibilityStageBackendPhase.DecalBuffer);
    }

    [Test]
    public void RenderGraph_OrdersPortalVisibilityBetweenLateRasterAndMultisampleResolve()
    {
        RenderPipelineResourceLayoutBuilder layoutBuilder = new();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.IdentityMultisample)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.MetadataMultisample)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.SelectionMultisample)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.DepthStencilMultisample)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.FrameBuffer(AdvancedVisibilityResourceNames.FrameBufferMultisample)
            .Color(0, AdvancedVisibilityResourceNames.IdentityMultisample)
            .Color(1, AdvancedVisibilityResourceNames.MetadataMultisample)
            .Color(2, AdvancedVisibilityResourceNames.SelectionMultisample)
            .DepthStencil(AdvancedVisibilityResourceNames.DepthStencilMultisample)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.Identity)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.Metadata)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.Selection)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.DepthStencil)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.FrameBuffer(AdvancedVisibilityResourceNames.FrameBuffer)
            .Color(0, AdvancedVisibilityResourceNames.Identity)
            .Color(1, AdvancedVisibilityResourceNames.Metadata)
            .Color(2, AdvancedVisibilityResourceNames.Selection)
            .DepthStencil(AdvancedVisibilityResourceNames.DepthStencil)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedDecalBufferResourceNames.Albedo)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedDecalBufferResourceNames.Surface)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.FrameBuffer(AdvancedDecalBufferResourceNames.FrameBuffer)
            .Color(0, AdvancedDecalBufferResourceNames.Albedo)
            .Color(1, AdvancedDecalBufferResourceNames.Surface)
            .DepthStencil(AdvancedVisibilityResourceNames.DepthStencil)
            .Factory(() => throw new NotSupportedException()).Add();

        RenderPipelineResourceProfile profile = new(
            DisplayWidth: 1280u,
            DisplayHeight: 720u,
            InternalWidth: 1280u,
            InternalHeight: 720u,
            OutputHDR: true,
            AntiAliasingMode: EAntiAliasingMode.Msaa,
            MsaaSampleCount: 4u,
            Stereo: false);
        RenderPipelineResourceLayout layout = layoutBuilder.Build(profile);

        RenderPassMetadataCollection metadata = new();
        RenderGraphDescribeContext context = new(metadata, layout);

        VPRC_AdvancedRenderStage lateStage = new VPRC_AdvancedRenderStage()
            .SetStage(EAdvancedRenderStage.DepthPyramidAndLateVisibility);
        lateStage.DescribeRenderPass(context);

        var passes = metadata.Build();
        RenderPassMetadata lateRaster = passes.Single(static p =>
            string.Equals(p.Name, VPRC_AdvancedRenderStage.LateVisibilityRasterPassName, StringComparison.Ordinal));
        RenderPassMetadata portalPass = passes.Single(static p =>
            string.Equals(p.Name, VPRC_AdvancedRenderStage.PortalVisibilityPassName, StringComparison.Ordinal));
        RenderPassMetadata resolvePass = passes.Single(static p =>
            string.Equals(p.Name, VPRC_AdvancedRenderStage.MultisampleVisibilityResolvePassName, StringComparison.Ordinal));
        RenderPassMetadata decalPass = passes.Single(static p =>
            string.Equals(p.Name, VPRC_AdvancedRenderStage.DecalBufferPassName, StringComparison.Ordinal));

        portalPass.ExplicitDependencies.ShouldContain(lateRaster.PassIndex);
        resolvePass.ExplicitDependencies.ShouldContain(portalPass.PassIndex);
        decalPass.ExplicitDependencies.ShouldContain(resolvePass.PassIndex);
    }

    [Test]
    public void PortalVisibilitySegmentPool_ResolvesDisjointSegmentsWithoutWideningPrimaryLimits()
    {
        RenderFrameViewSet.MaxViewCount.ShouldBe(8);

        uint portalViewCount = (uint)AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame;
        const uint payloadCapacity = 128u;
        const uint rangeCapacity = 16u;
        VulkanAdvancedPortalVisibilityState state = new(
            FrameSlot: 0,
            FrameGeneration: 1u,
            DescriptorSet: new DescriptorSet(1u),
            VisibleIndices: default,
            RangeCounts: default,
            IndirectArguments: default,
            MeshArguments: default,
            MeshPayloads: default,
            EarlyIndexedGroupCounts: default,
            Counters: default,
            PrimaryViewCount: 2u,
            PortalViewCount: portalViewCount,
            PayloadCapacity: payloadCapacity,
            RangeCapacity: rangeCapacity,
            IndexedInstanceGroupCount: 32u);

        for (uint slot = 0u; slot < portalViewCount; ++slot)
        {
            state.TryGetPortalViewSegment(slot, out uint payloadBase, out uint rangeBase).ShouldBeTrue();
            payloadBase.ShouldBe(slot * payloadCapacity);
            rangeBase.ShouldBe(slot * rangeCapacity);
        }

        state.TryGetPortalViewSegment(portalViewCount, out _, out _).ShouldBeFalse();

        VulkanAdvancedVisibilityResourceRuntime.TryGetPortalVisibilityRequiredBytes(
            primaryViewCount: 2u,
            portalViewCount: portalViewCount,
            payloadCapacity: payloadCapacity,
            groupCount: 32u,
            rangeCapacity: rangeCapacity,
            out ulong requiredBytes).ShouldBeTrue();
        requiredBytes.ShouldBeGreaterThan(0u);

        VulkanAdvancedVisibilityResourceRuntime.TryGetPortalVisibilityRequiredBytes(
            primaryViewCount: 2u,
            portalViewCount: portalViewCount + 1u,
            payloadCapacity: payloadCapacity,
            groupCount: 32u,
            rangeCapacity: rangeCapacity,
            out _).ShouldBeFalse();
    }

    [Test]
    public void DecalBufferPipelineFactory_BlocksPrimaryDecalsOnPortalStencilPixels()
    {
        StencilOpState stencil = VulkanDecalBufferPipelineFactory.PortalExclusionStencilState;
        stencil.CompareOp.ShouldBe(CompareOp.Equal);
        stencil.CompareMask.ShouldBe(AdvancedStencilBitLayout.PortalNodeMask);
        stencil.WriteMask.ShouldBe(0u);
        stencil.Reference.ShouldBe(0u);
        stencil.FailOp.ShouldBe(StencilOp.Keep);
        stencil.PassOp.ShouldBe(StencilOp.Keep);
        stencil.DepthFailOp.ShouldBe(StencilOp.Keep);
    }

    [TestCase("Build/CommonAssets/Shaders/Advanced/Preparation/EarlyVisibility.comp", EShaderType.Compute, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Preparation/BuildVisibilityIndirect.comp", EShaderType.Compute, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.task", EShaderType.Task, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.task", EShaderType.Task, "#define XR_ADV_MULTIVIEW_RASTER 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.vert", EShaderType.Vertex, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.vert", EShaderType.Vertex, "#define XR_ADV_VISIBILITY_CLIP_PLANE 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.vert", EShaderType.Vertex, "#define XR_ADV_MULTIVIEW_RASTER 1\n#define XR_ADV_VISIBILITY_CLIP_PLANE 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.mesh", EShaderType.Mesh, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.mesh", EShaderType.Mesh, "#define XR_ADV_VISIBILITY_CLIP_PLANE 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.mesh", EShaderType.Mesh, "#define XR_ADV_MULTIVIEW_RASTER 1\n#define XR_ADV_VISIBILITY_CLIP_PLANE 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.frag", EShaderType.Fragment, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRasterMasked.frag", EShaderType.Fragment, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/PortalQuad.vert", EShaderType.Vertex, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/PortalQuad.vert", EShaderType.Vertex, "#define XR_ADV_MULTIVIEW_RASTER 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/PortalStencilCarve.frag", EShaderType.Fragment, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/PortalDepthReset.frag", EShaderType.Fragment, "")]
    public void PortalAndVisibilityShaders_CompileToSpirvForVulkan(
        string relativePath,
        EShaderType shaderType,
        string extraDefines)
    {
        string fullPath = ResolveWorkspaceFile(relativePath);
        string shaderSource = File.ReadAllText(fullPath);
        if (!relativePath.EndsWith("PortalStencilCarve.frag", StringComparison.Ordinal))
        {
            string preamble = extraDefines + AdvancedShaderAccessLibrary.BuildPreamble(
                RuntimeGraphicsApiKind.Vulkan,
                EAdvancedTextureIndirectionMode.VulkanDescriptorHeap,
                descriptorSet: 3u,
                resourceDescriptorSet: 2u);
            shaderSource = InsertPreambleAfterVersion(shaderSource, preamble);
        }

        TextFile source = new()
        {
            FilePath = fullPath,
            Text = shaderSource,
        };
        XRShader shader = new(shaderType, source);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNullOrWhiteSpace();
    }

    private static string ResolveWorkspaceFile(string relativePath)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"Could not locate workspace file '{relativePath}'.");
    }

    private static string InsertPreambleAfterVersion(string source, string preamble)
    {
        int newline = source.IndexOf('\n');
        newline.ShouldBeGreaterThan(0);
        return string.Concat(source.AsSpan(0, newline + 1), preamble, source.AsSpan(newline + 1));
    }
}
