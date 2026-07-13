using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class StereoPostProcessContractTests
{
    [Test]
    public void BloomMipRegions_PreserveOddNonSquareDestinationExtents()
    {
        VPRC_BloomPass.ResolveBloomMipRegion(896u, 1007u, 0).Width.ShouldBe(896);
        VPRC_BloomPass.ResolveBloomMipRegion(896u, 1007u, 0).Height.ShouldBe(1007);
        VPRC_BloomPass.ResolveBloomMipRegion(896u, 1007u, 1).Width.ShouldBe(448);
        VPRC_BloomPass.ResolveBloomMipRegion(896u, 1007u, 1).Height.ShouldBe(503);
        VPRC_BloomPass.ResolveBloomMipRegion(896u, 1007u, 2).Width.ShouldBe(224);
        VPRC_BloomPass.ResolveBloomMipRegion(896u, 1007u, 2).Height.ShouldBe(251);
        VPRC_BloomPass.ResolveBloomMipRegion(896u, 1007u, 3).Width.ShouldBe(112);
        VPRC_BloomPass.ResolveBloomMipRegion(896u, 1007u, 3).Height.ShouldBe(125);
        VPRC_BloomPass.ResolveBloomMipRegion(896u, 1007u, 4).Width.ShouldBe(56);
        VPRC_BloomPass.ResolveBloomMipRegion(896u, 1007u, 4).Height.ShouldBe(62);
    }

    [Test]
    public void TemporalHistoryCoverage_StereoTsrRequiresEveryColorDepthAndTsrLayer()
    {
        VPRC_TemporalAccumulationPass.TemporalHistoryCoverage coverage = new();
        coverage.Begin(expectedLayerCount: 2u, requiresTsrColor: true);

        coverage.RecordColorAndDepth(0b11u, 0b11u);
        coverage.IsComplete.ShouldBeFalse();

        coverage.RecordTsrColor(0b01u);
        coverage.IsComplete.ShouldBeFalse();
        coverage.CompleteLayerMask.ShouldBe(0b01u);

        coverage.RecordTsrColor(0b10u);
        coverage.IsComplete.ShouldBeTrue();

        coverage.Clear();
        coverage.IsComplete.ShouldBeFalse();
        coverage.ExpectedLayerMask.ShouldBe(0u);
    }

    [Test]
    public void TemporalHistoryGeneration_ProfileChangesResetButSwapchainRotationDoesNot()
    {
        VPRC_TemporalAccumulationPass.TemporalHistoryProfile profile = new(
            896u,
            1007u,
            896u,
            1007u,
            EAntiAliasingMode.Tsr,
            EVrTemporalHistoryPolicy.StereoArrayLayer);
        VPRC_TemporalAccumulationPass.TemporalHistoryGenerationTracker tracker = new();

        tracker.BeginFrame(profile, expectedLayerCount: 2u).ShouldBeTrue();
        tracker.ProfileGeneration.ShouldBe(1ul);
        tracker.RecordCurrentMatrices(0b11u);

        VPRC_TemporalAccumulationPass.TemporalHistoryCoverage coverage = new();
        coverage.Begin(expectedLayerCount: 2u, requiresTsrColor: true);
        coverage.RecordColorAndDepth(0b11u, 0b11u);
        coverage.RecordTsrColor(0b11u);
        tracker.CommitFrame(coverage).ShouldBe(0b11u);
        tracker.HistoryReady.ShouldBeTrue();

        ulong leftGeneration = tracker.LeftEyeResetGeneration;
        ulong rightGeneration = tracker.RightEyeResetGeneration;
        // Acquired OpenXR image rotation is not part of TemporalHistoryProfile.
        tracker.BeginFrame(profile, expectedLayerCount: 2u).ShouldBeFalse();
        tracker.ProfileGeneration.ShouldBe(1ul);
        tracker.LeftEyeResetGeneration.ShouldBe(leftGeneration);
        tracker.RightEyeResetGeneration.ShouldBe(rightGeneration);
        tracker.HistoryReady.ShouldBeTrue();

        VPRC_TemporalAccumulationPass.TemporalHistoryProfile resized = profile with
        {
            InternalWidth = 768u,
            FullWidth = 768u,
        };
        tracker.BeginFrame(resized, expectedLayerCount: 2u).ShouldBeTrue();
        tracker.ProfileGeneration.ShouldBe(2ul);
        tracker.LeftEyeResetGeneration.ShouldBe(leftGeneration + 1u);
        tracker.RightEyeResetGeneration.ShouldBe(rightGeneration + 1u);
        tracker.HistoryReady.ShouldBeFalse();
    }

    [Test]
    public void TemporalHistoryGeneration_ReadinessAndResetRemainPerEye()
    {
        VPRC_TemporalAccumulationPass.TemporalHistoryProfile profile = new(
            896u,
            1007u,
            896u,
            1007u,
            EAntiAliasingMode.Tsr,
            EVrTemporalHistoryPolicy.StereoArrayLayer);
        VPRC_TemporalAccumulationPass.TemporalHistoryGenerationTracker tracker = new();
        tracker.BeginFrame(profile, expectedLayerCount: 2u);
        tracker.RecordCurrentMatrices(0b11u);

        VPRC_TemporalAccumulationPass.TemporalHistoryCoverage complete = new();
        complete.Begin(expectedLayerCount: 2u, requiresTsrColor: true);
        complete.RecordColorAndDepth(0b11u, 0b11u);
        complete.RecordTsrColor(0b11u);
        tracker.CommitFrame(complete);

        ulong leftGeneration = tracker.LeftEyeResetGeneration;
        ulong rightGeneration = tracker.RightEyeResetGeneration;
        tracker.InvalidateLayers(0b10u);
        tracker.LeftEyeHistoryReady.ShouldBeTrue();
        tracker.RightEyeHistoryReady.ShouldBeFalse();
        tracker.LeftEyeResetGeneration.ShouldBe(leftGeneration);
        tracker.RightEyeResetGeneration.ShouldBe(rightGeneration + 1u);
        tracker.HistoryReady.ShouldBeFalse();

        tracker.BeginFrame(profile, expectedLayerCount: 2u);
        tracker.RecordCurrentMatrices(0b11u);
        tracker.CommitFrame(complete).ShouldBe(0b11u);
        tracker.HistoryReady.ShouldBeTrue();

        tracker.BeginFrame(profile, expectedLayerCount: 2u);
        tracker.RecordCurrentMatrices(0b11u);
        VPRC_TemporalAccumulationPass.TemporalHistoryCoverage leftOnly = new();
        leftOnly.Begin(expectedLayerCount: 2u, requiresTsrColor: false);
        leftOnly.RecordColorAndDepth(0b01u, 0b11u);
        tracker.CommitFrame(leftOnly).ShouldBe(0b01u);
        tracker.LeftEyeHistoryReady.ShouldBeTrue();
        tracker.RightEyeHistoryReady.ShouldBeFalse();
        tracker.HistoryReady.ShouldBeFalse();
    }

    [Test]
    public void TemporalMatrixAndCameraCutValidation_RejectsOnlyDiscontinuities()
    {
        VPRC_TemporalAccumulationPass.IsTemporalMatrixFinite(Matrix4x4.Identity).ShouldBeTrue();
        Matrix4x4 invalid = Matrix4x4.Identity;
        invalid.M24 = float.NaN;
        VPRC_TemporalAccumulationPass.IsTemporalMatrixFinite(invalid).ShouldBeFalse();

        Vector3 previousForward = Vector3.UnitZ;
        Vector3 ordinaryHeadRotation = Vector3.Normalize(new Vector3(0.5f, 0.0f, 0.8660254f));
        VPRC_TemporalAccumulationPass.IsCameraDiscontinuity(
            new Vector3(1.0f, 0.0f, 0.0f),
            ordinaryHeadRotation,
            Vector3.Zero,
            previousForward,
            translationThreshold: 2.0f,
            rotationThresholdDegrees: 55.0f).ShouldBeFalse();

        VPRC_TemporalAccumulationPass.IsCameraDiscontinuity(
            new Vector3(3.0f, 0.0f, 0.0f),
            previousForward,
            Vector3.Zero,
            previousForward,
            translationThreshold: 2.0f,
            rotationThresholdDegrees: 55.0f).ShouldBeTrue();

        Vector3 cutRotation = Vector3.Normalize(new Vector3(0.94f, 0.0f, 0.342f));
        VPRC_TemporalAccumulationPass.IsCameraDiscontinuity(
            Vector3.Zero,
            cutRotation,
            Vector3.Zero,
            previousForward,
            translationThreshold: 2.0f,
            rotationThresholdDegrees: 55.0f).ShouldBeTrue();
    }

    [Test]
    public void TemporalHistoryCoverage_MonoNonTsrDoesNotRequireTsrColor()
    {
        VPRC_TemporalAccumulationPass.TemporalHistoryCoverage coverage = new();
        coverage.Begin(expectedLayerCount: 1u, requiresTsrColor: false);
        coverage.RecordColorAndDepth(0b1u, 0b1u);
        coverage.IsComplete.ShouldBeTrue();
    }

    [TestCase(EAntiAliasingMode.Taa, true)]
    [TestCase(EAntiAliasingMode.Tsr, true)]
    [TestCase(EAntiAliasingMode.Dlaa, true)]
    [TestCase(EAntiAliasingMode.None, false)]
    [TestCase(EAntiAliasingMode.Fxaa, false)]
    public void TemporalInputPopulation_MatchesTemporalAaModes(
        EAntiAliasingMode mode,
        bool expected)
    {
        VPRC_TemporalAccumulationPass.ShouldPopulateTemporalInput(mode).ShouldBe(expected);
    }

    [Test]
    public void StereoBloomPass_UsesDestinationMipRegionsAndTwoLayerViews()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_BloomPass.cs");

        source.ShouldContain("ResolveBloomMipRegion(width, height, 1)");
        source.ShouldContain("PushRenderArea(copyRegion)");
        source.ShouldContain("ValidateBloomWriteFboContract");
        source.ShouldContain("layerIndex != -1");
        source.ShouldContain("viewBase.NumLevels != 1u");
        source.ShouldContain("arrayView.NumLayers != 2u");
        source.ShouldContain("arrayView.Array");
        source.ShouldContain("new Vector2(area.X, area.Y)");
    }

    [Test]
    public void StereoFullscreenPasses_UseDestinationExtentAndValidateLocalRasterMapping()
    {
        string chain = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");
        string legacyChain = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        string quadCommand = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.Internal.cs");

        chain.ShouldContain("SetTargets(PostProcessFBOName, PostProcessOutputFBOName, matchDestinationRenderArea: true)");
        chain.ShouldContain("SetTargets(FinalPostProcessFBOName, FinalPostProcessOutputFBOName, matchDestinationRenderArea: true)");
        chain.ShouldContain("SetTargets(TsrUpscaleFBOName, TsrUpscaleFBOName, matchDestinationRenderArea: true)");
        legacyChain.ShouldContain("SetTargets(PostProcessFBOName, PostProcessOutputFBOName, matchDestinationRenderArea: true)");
        quadCommand.ShouldContain("ValidateDestinationScreenRegionContract");
        quadCommand.ShouldContain("screenOrigin=(0,0) screenSize=({3},{4})");
        quadCommand.ShouldContain("uv=localRaster/destinationExtent->[0,1]");
        quadCommand.ShouldContain("expectedViewMask = 0x3u");

        string[] shaderPaths =
        [
            "Build/CommonAssets/Shaders/Scene3D/PostProcessStereo.fs",
            "Build/CommonAssets/Shaders/Scene3D/FinalPostProcessStereo.fs",
            "Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolutionStereo.fs",
            "Build/CommonAssets/Shaders/Scene3D/BloomCopyStereo.fs",
            "Build/CommonAssets/Shaders/Scene3D/BloomDownsampleStereo.fs",
            "Build/CommonAssets/Shaders/Scene3D/BloomUpsampleStereo.fs",
        ];
        foreach (string path in shaderPaths)
        {
            string shader = ReadWorkspaceFile(path);
            shader.ShouldContain("uniform float ScreenWidth");
            shader.ShouldContain("uniform float ScreenHeight");
            shader.ShouldContain("uniform vec2 ScreenOrigin");
            shader.ShouldContain("XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight))");
        }
    }

    [Test]
    public void StereoTsr_NativeResolutionKeepsTemporalResolveWithoutSpatialLowPass()
    {
        string mono = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolution.fs");
        string stereo = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolutionStereo.fs");

        foreach (string source in new[] { mono, stereo })
        {
            source.ShouldContain("bool nativeResolution = all(equal(");
            source.ShouldContain("? currentColorRaw");
            source.ShouldContain("bool canUseHistory = TsrCanSampleHistory(HistoryReady, historyUV);");
            source.ShouldContain("mix(currentYCoCg, clippedHistory, historyWeight)");
            source.ShouldContain("PreviousJitterUv - CurrentJitterUv");
            source.ShouldContain("#pragma snippet \"TemporalSuperResolutionCore\"");
            source.ShouldContain("TsrClipHistoryToNeighborhood");
            source.ShouldContain("TsrComputeHistoryWeight");
            source.ShouldContain("TsrComputeSharpenStrength");
        }

        stereo.ShouldContain("vec3 SampleCatmullRom(sampler2DArray tex");
        stereo.ShouldContain("SampleCatmullRom(TsrHistoryColor, historySampleUv, historyTexelSize)");
        stereo.ShouldContain("float(gl_ViewID_OVR)");
    }

    [Test]
    public void Phase524bMonoReference_RendersEachEyeThroughTheMonoTsrEntryPoint()
    {
        string resources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Resources.cs");
        string frameBuffers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs");
        string chain = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");
        string quadCommand = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.Internal.cs");
        string vulkanFramebuffer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        resources.ShouldContain("TsrMonoReferenceTextureName");
        resources.ShouldContain("CreateTsrMonoReferenceFBO(0)");
        resources.ShouldContain("CreateTsrMonoReferenceFBO(1)");
        resources.ShouldContain(".Color(0, TsrMonoReferenceLeftTextureViewName)");
        resources.ShouldContain(".Color(0, TsrMonoReferenceRightTextureViewName)");
        frameBuffers.ShouldContain("Path.Combine(SceneShaderPath, \"TemporalSuperResolution.fs\")");
        frameBuffers.ShouldContain("existingView.MinLayer + layer");
        frameBuffers.ShouldContain("1u,\n            format,\n            array: false");
        chain.ShouldContain("SetTargets(TsrMonoReferenceLeftFBOName");
        chain.ShouldContain("SetTargets(TsrMonoReferenceRightFBOName");
        chain.ShouldContain(".SetIsolatedMonoReference()");
        chain.ShouldContain("AppendDiagnosticTextureCapture(tsrUpscale, \"13c_MonoTsrReference\"");
        quadCommand.ShouldContain("if (IsolatedMonoReference)");
        quadCommand.ShouldContain("target is not XRTexture2DArrayView { NumLayers: 1u }");
        vulkanFramebuffer.ShouldContain("XRTexture2DArrayView { NumLayers: > 1u }");
    }

    [Test]
    public void TsrCore_HistoryRejectionClampingAndSharpeningAreShared()
    {
        string core = ReadWorkspaceFile("Build/CommonAssets/Shaders/Snippets/TemporalSuperResolutionCore.glsl");
        core.ShouldContain("bool TsrCanSampleHistory");
        core.ShouldContain("bool TsrDepthMatches");
        core.ShouldContain("vec3 TsrClipHistoryToNeighborhood");
        core.ShouldContain("float TsrComputeHistoryWeight");
        core.ShouldContain("float TsrComputeSharpenStrength");

        foreach (string path in new[]
        {
            "Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolution.fs",
            "Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolutionStereo.fs",
        })
        {
            string source = ReadWorkspaceFile(path);
            source.ShouldNotContain("vec3 ClipToAABB(");
            source.ShouldNotContain("bool IsValidUV(");
            source.ShouldContain("TsrDepthMatches(currentDepth, historyDepth, DepthRejectThreshold)");
        }
    }

    [Test]
    public void TsrQuadMaterials_RequestViewportDimensions()
    {
        foreach (string path in new[]
        {
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default2/DefaultRenderPipeline2.FBOs.cs",
        })
        {
            string source = ReadWorkspaceFile(path);
            int methodStart = source.IndexOf("private XRFrameBuffer CreateTsrUpscaleFBO()", StringComparison.Ordinal);
            methodStart.ShouldBeGreaterThanOrEqualTo(0);
            int methodEnd = source.IndexOf("private XRFrameBuffer CreateTsrHistoryColorFBO()", methodStart, StringComparison.Ordinal);
            methodEnd.ShouldBeGreaterThan(methodStart);
            string method = source[methodStart..methodEnd];
            method.ShouldContain("EUniformRequirements.ClipSpacePolicy");
            method.ShouldContain("EUniformRequirements.ViewportDimensions");
        }
    }

    [Test]
    public void MotionVectorDrawSnapshot_UsesIndependentPerEyeCurrentMatricesAndExactKeyDiagnostics()
    {
        string meshSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string uniformSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs");
        meshSource.ShouldContain("viewProjectionMatrixUnjitteredSnapshot = temporalData.CurrViewProjectionUnjittered;");
        meshSource.ShouldContain("rightEyeViewProjectionMatrixUnjitteredSnapshot = temporalData.RightEyeCurrViewProjectionUnjittered;");
        uniformSource.ShouldContain("draw.ViewProjectionMatrixUnjittered");
        uniformSource.ShouldContain("draw.RightEyeViewProjectionMatrixUnjittered");
        uniformSource.ShouldContain("draw.PreviousViewProjectionMatrixUnjittered");
        uniformSource.ShouldContain("draw.PreviousRightEyeViewProjectionMatrixUnjittered");

        string temporalPass = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs");
        temporalPass.ShouldContain("!ReferenceEquals(rightCamera, leftCamera)");
        temporalPass.ShouldContain("camera.ProjectionMatrixUnjittered");
        temporalPass.ShouldContain("Key={2} MatrixMask=0x{3:X} ExpectedMask=0x{4:X}");
    }

    [Test]
    public void StereoBloomCombine_DefaultRangeSamplesAccumulatedMipOnce()
    {
        string source = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/PostProcessStereo.fs");
        source.ShouldContain("if (startMip == 1 && endMip == 4)");
        source.ShouldContain("textureLod(BloomBlurTexture, vec3(duv, gl_ViewID_OVR), 1.0)");
    }

    [TestCase("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolution.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolutionStereo.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/PostProcessStereo.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/FinalPostProcessStereo.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/BloomCopyStereo.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/BloomDownsampleStereo.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/BloomUpsampleStereo.fs")]
    public void StereoPostProcessShader_CompilesToSpirv(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        XRShader shader = new(
            EShaderType.Fragment,
            new TextFile
            {
                FilePath = fullPath,
                Text = File.ReadAllText(fullPath),
            });

        byte[] spirv = VulkanShaderCompiler.Compile(shader, out string entryPoint, out _, out _);
        entryPoint.ShouldBe("main");
        spirv.Length.ShouldBeGreaterThan(0);
    }

    private static string ReadWorkspaceFile(string relativePath)
        => File.ReadAllText(ResolveWorkspacePath(relativePath));

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not resolve workspace path '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }
}
