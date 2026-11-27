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
public sealed class AdvancedPortalShadingContractTests
{
    [Test]
    public void FroxelGridDimensions_CalculatesPrimaryAndPortalViewCapacitiesDeterministically()
    {
        uint tilesX = AdvancedFroxelGridDimensions.CalculateTilesX(1920u);
        uint tilesY = AdvancedFroxelGridDimensions.CalculateTilesY(1080u);
        const uint depthSlices = AdvancedFroxelGridDimensions.DefaultDepthSlices;

        AdvancedFroxelGridDimensions.CalculateTotalViewCapacity(primaryViewCount: 1u, portalViewBudget: 0u)
            .ShouldBe(1u);
        AdvancedFroxelGridDimensions.CalculateTotalViewCapacity(primaryViewCount: 2u, portalViewBudget: 8u)
            .ShouldBe(10u);
        AdvancedFroxelGridDimensions.CalculateTotalViewCapacity(primaryViewCount: 2u, portalViewBudget: 99u)
            .ShouldBe(2u + (uint)AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame);

        uint totalViews = AdvancedFroxelGridDimensions.CalculateTotalViewCapacity(2u, 8u);
        uint froxelCount = checked(tilesX * tilesY * depthSlices * totalViews);
        ulong lightGridBytes = checked((ulong)froxelCount * 16UL);
        ulong decalGridBytes = checked((ulong)froxelCount * 8UL);

        AdvancedFroxelGridDimensions.CalculateAvailableViewCapacity(
                lightGridBytes, tilesX, tilesY, depthSlices, recordByteStride: 16u)
            .ShouldBe(totalViews);
        AdvancedFroxelGridDimensions.CalculateAvailableViewCapacity(
                decalGridBytes, tilesX, tilesY, depthSlices, recordByteStride: 8u)
            .ShouldBe(totalViews);
        AdvancedFroxelGridDimensions.CalculateAvailableViewCapacity(
                lightGridBytes - 16UL, tilesX, tilesY, depthSlices, recordByteStride: 16u)
            .ShouldBe(totalViews - 1u);
    }

    [Test]
    public void PortalFroxelFeatureMask_EncodesAndDecodesWithoutCollidingWithExistingBits()
    {
        AdvancedRenderPipeline.EncodePortalFroxelSlotCount(0u).ShouldBe(0UL);

        const ulong existingBits =
            (1UL << 30) |
            (0x3FUL << 32) |
            (1UL << 38) |
            (1UL << 39) |
            (1UL << 40) |
            (1UL << 41) |
            (0x3FUL << 42) |
            (0xFFUL << 48) |
            (0xFFUL << 56);

        for (uint budget = 0u; budget <= (uint)AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame; ++budget)
        {
            ulong encoded = AdvancedRenderPipeline.EncodePortalFroxelSlotCount(budget);
            (encoded & existingBits).ShouldBe(0UL);

            RenderPipelineResourceProfile profile = new(
                DisplayWidth: 1280u,
                DisplayHeight: 720u,
                InternalWidth: 1280u,
                InternalHeight: 720u,
                OutputHDR: true,
                AntiAliasingMode: EAntiAliasingMode.None,
                MsaaSampleCount: 1u,
                Stereo: false,
                FeatureMask: existingBits | encoded);

            AdvancedRenderPipeline.DecodePortalFroxelSlotCount(profile).ShouldBe(budget);
        }

        ulong overflowEncoded = AdvancedRenderPipeline.EncodePortalFroxelSlotCount(63u);
        RenderPipelineResourceProfile overflowProfile = new(
            DisplayWidth: 1280u,
            DisplayHeight: 720u,
            InternalWidth: 1280u,
            InternalHeight: 720u,
            OutputHDR: true,
            AntiAliasingMode: EAntiAliasingMode.None,
            MsaaSampleCount: 1u,
            Stereo: false,
            FeatureMask: overflowEncoded);
        AdvancedRenderPipeline.DecodePortalFroxelSlotCount(overflowProfile)
            .ShouldBe((uint)AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame);
    }

    [Test]
    public void MsaaPortalStencilRepopulateFactory_UsesVulkan10CoreStencilReplaceOnPortalBitsOnly()
    {
        StencilOpState stencil = VulkanAdvancedMsaaResolvePipelineFactory.PortalStencilRepopulateState;
        stencil.CompareOp.ShouldBe(CompareOp.Always);
        stencil.PassOp.ShouldBe(StencilOp.Replace);
        stencil.FailOp.ShouldBe(StencilOp.Keep);
        stencil.DepthFailOp.ShouldBe(StencilOp.Keep);
        stencil.WriteMask.ShouldBe(AdvancedStencilBitLayout.PortalNodeMask);
        stencil.CompareMask.ShouldBe(0xFFu);

        DynamicState[] dynamicStates = VulkanAdvancedMsaaResolvePipelineFactory.PortalStencilRepopulateDynamicStates.ToArray();
        dynamicStates.ShouldContain(DynamicState.Viewport);
        dynamicStates.ShouldContain(DynamicState.Scissor);
        dynamicStates.ShouldContain(DynamicState.StencilReference);
    }

    [Test]
    public void AmbientOcclusionRenderGraphPass_DeclaresVisibilityMetadataSample()
    {
        RenderPipelineResourceLayoutBuilder layoutBuilder = new();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.Metadata)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.DepthStencil)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedAmbientOcclusionContract.ResourceName)
            .Factory(() => throw new NotSupportedException()).Add();

        RenderPipelineResourceProfile profile = new(
            DisplayWidth: 1280u,
            DisplayHeight: 720u,
            InternalWidth: 1280u,
            InternalHeight: 720u,
            OutputHDR: true,
            AntiAliasingMode: EAntiAliasingMode.None,
            MsaaSampleCount: 1u,
            Stereo: false);
        RenderPipelineResourceLayout layout = layoutBuilder.Build(profile);

        RenderPassMetadataCollection metadata = new();
        RenderGraphDescribeContext context = new(metadata, layout);
        VPRC_AdvancedRenderStage aoStage = new VPRC_AdvancedRenderStage()
            .SetStage(EAdvancedRenderStage.AmbientOcclusion);
        aoStage.DescribeRenderPass(context);

        RenderPassMetadata aoPass = metadata.Build()
            .Single(static pass => string.Equals(pass.Name, "Advanced.AmbientOcclusion", StringComparison.Ordinal));
        string expectedMetadata = RenderGraphResourceNames.MakeTexture(AdvancedVisibilityResourceNames.Metadata);
        string expectedDepth = RenderGraphResourceNames.MakeTexture(AdvancedVisibilityResourceNames.DepthStencil);
        aoPass.ResourceUsages.Any(usage =>
                string.Equals(usage.ResourceName, expectedMetadata, StringComparison.Ordinal))
            .ShouldBeTrue();
        aoPass.ResourceUsages.Any(usage =>
                string.Equals(usage.ResourceName, expectedDepth, StringComparison.Ordinal))
            .ShouldBeTrue();
    }

    [Test]
    public void PortalUnwindContracts_VerifyStencilDepthDebugViewAndZeroAllocationDiagnostics()
    {
        ((uint)EAdvancedShadingDebugView.PortalStencilNode).ShouldBe(12u);
        ((byte)EAdvancedVisibilityStageBackendPhase.PortalSkyUnwind)
            .ShouldBeGreaterThan((byte)EAdvancedVisibilityStageBackendPhase.DecalBuffer);
        ((byte)EAdvancedVisibilityStageBackendPhase.PortalUnwind)
            .ShouldBeGreaterThan((byte)EAdvancedVisibilityStageBackendPhase.PortalSkyUnwind);

        // Verify Vulkan 1.0 stencil unwind contracts.
        StencilOpState skyStencil = VulkanPortalQuadPipelineFactory.PortalSkyUnwindStencilState;
        skyStencil.CompareOp.ShouldBe(CompareOp.Equal);
        skyStencil.PassOp.ShouldBe(StencilOp.Keep);
        skyStencil.CompareMask.ShouldBe(AdvancedStencilBitLayout.PortalNodeMask);
        skyStencil.WriteMask.ShouldBe(0u);

        StencilOpState glassStencil = VulkanPortalQuadPipelineFactory.PortalGlassCompositeStencilState;
        glassStencil.CompareOp.ShouldBe(CompareOp.Equal);
        glassStencil.PassOp.ShouldBe(StencilOp.Invert);
        glassStencil.CompareMask.ShouldBe(AdvancedStencilBitLayout.PortalNodeMask);
        glassStencil.WriteMask.ShouldBe(AdvancedStencilBitLayout.PortalNodeMask);

        // Verify deepest-out stencil XOR unwind returns portal bits 2..6 to 0 while preserving bits 0..1 and bit 7.
        const uint nonPortalBits = 0x83u;
        uint stencilValue = nonPortalBits;
        uint carve0To1 = AdvancedStencilBitLayout.ComputePortalTransitionWriteMask(0u, 1u);
        stencilValue ^= carve0To1;
        AdvancedStencilBitLayout.DecodePortalNode(stencilValue).ShouldBe(1u);
        (stencilValue & nonPortalBits).ShouldBe(nonPortalBits);

        uint carve1To2 = AdvancedStencilBitLayout.ComputePortalTransitionWriteMask(1u, 2u);
        stencilValue ^= carve1To2;
        AdvancedStencilBitLayout.DecodePortalNode(stencilValue).ShouldBe(2u);
        (stencilValue & nonPortalBits).ShouldBe(nonPortalBits);

        uint unwind2To1 = AdvancedStencilBitLayout.ComputePortalTransitionWriteMask(2u, 1u);
        stencilValue ^= unwind2To1;
        AdvancedStencilBitLayout.DecodePortalNode(stencilValue).ShouldBe(1u);
        (stencilValue & nonPortalBits).ShouldBe(nonPortalBits);

        uint unwind1To0 = AdvancedStencilBitLayout.ComputePortalTransitionWriteMask(1u, 0u);
        stencilValue ^= unwind1To0;
        AdvancedStencilBitLayout.DecodePortalNode(stencilValue).ShouldBe(0u);
        (stencilValue & nonPortalBits).ShouldBe(nonPortalBits);

        // Verify LatePasses render graph declares PortalSkyUnwind, PortalTransparents, and PortalUnwind.
        RenderPipelineResourceLayoutBuilder layoutBuilder = new();
        layoutBuilder.Texture(AdvancedRenderPipeline.HDRSceneTextureName)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.Texture(AdvancedVisibilityResourceNames.DepthStencil)
            .Factory(() => throw new NotSupportedException()).Add();
        layoutBuilder.FrameBuffer(AdvancedRenderPipeline.ForwardPassFBOName)
            .Color(0, AdvancedRenderPipeline.HDRSceneTextureName)
            .DepthStencil(AdvancedVisibilityResourceNames.DepthStencil)
            .Factory(() => throw new NotSupportedException()).Add();
        RenderPipelineResourceProfile profile = new(
            DisplayWidth: 1280u, DisplayHeight: 720u, InternalWidth: 1280u, InternalHeight: 720u,
            OutputHDR: true, AntiAliasingMode: EAntiAliasingMode.None, MsaaSampleCount: 1u, Stereo: false);
        RenderPipelineResourceLayout layout = layoutBuilder.Build(profile);
        RenderPassMetadataCollection metadata = new();
        RenderGraphDescribeContext context = new(metadata, layout);
        VPRC_AdvancedRenderStage lateStage = new VPRC_AdvancedRenderStage()
            .SetStage(EAdvancedRenderStage.LatePasses);
        lateStage.DescribeRenderPass(context);
        var passes = metadata.Build();
        passes.Any(static pass => string.Equals(pass.Name, VPRC_AdvancedRenderStage.PortalSkyUnwindPassName, StringComparison.Ordinal)).ShouldBeTrue();
        passes.Any(static pass => string.Equals(pass.Name, VPRC_AdvancedRenderStage.PortalTransparentsPassName, StringComparison.Ordinal)).ShouldBeTrue();
        passes.Any(static pass => string.Equals(pass.Name, VPRC_AdvancedRenderStage.PortalUnwindPassName, StringComparison.Ordinal)).ShouldBeTrue();

        // Verify zero-allocation portal diagnostic recording on XRRenderPipelineInstance.
        AdvancedPortalFramePlan plan = new()
        {
            PrimaryViewCount = 2,
            MaxScheduledDepth = 2,
            RejectedBudgetCount = 3,
            RejectedCulledCount = 4,
            RejectedCycleCount = 1,
        };
        XRRenderPipelineInstance instance = new(new AdvancedRenderPipeline());
        instance.RecordAdvancedProfilePortalDiagnostic(42ul, plan, froxelSlotBudget: 8u);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; ++i)
            instance.RecordAdvancedProfilePortalDiagnostic(42ul + (ulong)i, plan, froxelSlotBudget: 8u);
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        (allocAfter - allocBefore).ShouldBe(0L);

        AdvancedProfileDiagnosticsSnapshot snapshot = instance.CaptureAdvancedProfileDiagnostics();
        snapshot.Portals.FrameId.ShouldBe(42ul + 63ul);
        snapshot.Portals.PrimaryViewCount.ShouldBe(2);
        snapshot.Portals.ScheduledCount.ShouldBe(0);
        snapshot.Portals.MaxScheduledDepth.ShouldBe(2);
        snapshot.Portals.RejectedBudgetCount.ShouldBe(3);
        snapshot.Portals.RejectedCulledCount.ShouldBe(4);
        snapshot.Portals.RejectedCycleCount.ShouldBe(1);
        snapshot.Portals.FroxelSlotBudget.ShouldBe(8u);
    }

    [TestCase("Build/CommonAssets/Shaders/Advanced/Lighting/BuildFroxels.comp", EShaderType.Compute, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Classification/ClassifyTiles.comp", EShaderType.Compute, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Classification/ClassifyTiles.comp", EShaderType.Compute, "#extension GL_KHR_shader_subgroup_ballot : require\n#define XR_ADV_CLASSIFICATION_SUBGROUP_BALLOT 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Shading/ShadeNativeOpaque.comp", EShaderType.Compute, "#define XR_ADV_NATIVE_SHADING_FAMILY 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Shading/ShadeNativeOpaque.comp", EShaderType.Compute, "#define XR_ADV_NATIVE_SHADING_FAMILY 1\n#define XR_ADV_SHADE_LEAN 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Shading/ShadeNativeOpaque.comp", EShaderType.Compute, "#define XR_ADV_NATIVE_SHADING_FAMILY 2\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Shading/ShadeNativeOpaqueMsaa.comp", EShaderType.Compute, "#define XR_ADV_NATIVE_SHADING_FAMILY 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Shading/ShadeNativeOpaqueMsaa.comp", EShaderType.Compute, "#define XR_ADV_NATIVE_SHADING_FAMILY 2\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Shading/FoveatedQuadFill.comp", EShaderType.Compute, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/AO/Gtao.comp", EShaderType.Compute, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/ResolveAdvancedMsaaVisibility.vert", EShaderType.Vertex, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/ResolveAdvancedMsaaVisibility.vert", EShaderType.Vertex, "#define XR_ADV_MULTIVIEW_RESOLVE 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/ResolveAdvancedMsaaVisibility.vert", EShaderType.Vertex, "#define XR_ADV_PORTAL_MSAA_STENCIL_REPOPULATE 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/ResolveAdvancedMsaaVisibility.vert", EShaderType.Vertex, "#define XR_ADV_MULTIVIEW_RESOLVE 1\n#define XR_ADV_PORTAL_MSAA_STENCIL_REPOPULATE 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/ResolveAdvancedMsaaVisibility.frag", EShaderType.Fragment, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/PortalMsaaStencilRepopulate.frag", EShaderType.Fragment, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/PortalSkyUnwind.frag", EShaderType.Fragment, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/PortalGlassQuad.vert", EShaderType.Vertex, "")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/PortalGlassQuad.vert", EShaderType.Vertex, "#define XR_ADV_MULTIVIEW_RASTER 1\n")]
    [TestCase("Build/CommonAssets/Shaders/Advanced/Visibility/PortalGlassComposite.frag", EShaderType.Fragment, "")]
    public void PortalShadingAoAndMsaaShaders_CompileToSpirvForVulkan(
        string relativePath,
        EShaderType shaderType,
        string extraDefines)
    {
        string fullPath = ResolveWorkspaceFile(relativePath);
        string shaderSource = File.ReadAllText(fullPath);
        string preamble = extraDefines + AdvancedShaderAccessLibrary.BuildPreamble(
            RuntimeGraphicsApiKind.Vulkan,
            EAdvancedTextureIndirectionMode.VulkanDescriptorHeap,
            descriptorSet: 3u,
            resourceDescriptorSet: 2u);
        shaderSource = InsertPreambleAfterVersion(shaderSource, preamble);

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
