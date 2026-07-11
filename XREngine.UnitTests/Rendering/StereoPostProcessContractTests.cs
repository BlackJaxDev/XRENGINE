using System;
using System.IO;
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

        coverage.RecordTsrColor(0b10u);
        coverage.IsComplete.ShouldBeTrue();

        coverage.Clear();
        coverage.IsComplete.ShouldBeFalse();
        coverage.ExpectedLayerMask.ShouldBe(0u);
    }

    [Test]
    public void TemporalHistoryCoverage_MonoNonTsrDoesNotRequireTsrColor()
    {
        VPRC_TemporalAccumulationPass.TemporalHistoryCoverage coverage = new();
        coverage.Begin(expectedLayerCount: 1u, requiresTsrColor: false);
        coverage.RecordColorAndDepth(0b1u, 0b1u);
        coverage.IsComplete.ShouldBeTrue();
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
            source.ShouldContain("bool canUseHistory = HistoryReady && IsValidUV(historyUV);");
            source.ShouldContain("mix(currentYCoCg, clippedHistory, historyWeight)");
            source.ShouldContain("PreviousJitterUv - CurrentJitterUv");
        }

        stereo.ShouldContain("vec3 SampleCatmullRom(sampler2DArray tex");
        stereo.ShouldContain("SampleCatmullRom(TsrHistoryColor, historySampleUv, historyTexelSize)");
        stereo.ShouldContain("float(gl_ViewID_OVR)");
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
