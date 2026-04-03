using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AlphaToCoveragePhase2Tests
{
    [Test]
    public void AlphaToCoverageTransparency_RoutesToMaskedPass_AndRequestsA2CState()
    {
        XRMaterial material = new();
        material.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;

        material.TransparencyMode = ETransparencyMode.AlphaToCoverage;

        material.RenderPass.ShouldBe((int)EDefaultRenderPass.MaskedForward);
        material.RenderOptions.ShouldNotBeNull();
        material.RenderOptions!.BlendModeAllDrawBuffers.ShouldNotBeNull();
        material.RenderOptions.BlendModeAllDrawBuffers!.Enabled.ShouldBe(ERenderParamUsage.Disabled);
        material.RenderOptions.DepthTest.ShouldNotBeNull();
        material.RenderOptions.DepthTest!.Enabled.ShouldBe(ERenderParamUsage.Enabled);
        material.RenderOptions.DepthTest.UpdateDepth.ShouldBeTrue();
        material.RenderOptions.AlphaToCoverage.ShouldBe(ERenderParamUsage.Enabled);
        material.InferTransparencyMode().ShouldBe(ETransparencyMode.AlphaToCoverage);

        material.TransparencyMode = ETransparencyMode.Masked;

        material.RenderOptions.AlphaToCoverage.ShouldBe(ERenderParamUsage.Disabled);
        material.InferTransparencyMode().ShouldBe(ETransparencyMode.Masked);
    }

    [Test]
    public void FrameBuffer_MultisampleDetection_ReflectsAttachmentSampleCounts()
    {
        XRTexture2D singleSampleTexture = new();
        XRFrameBuffer singleSampleFbo = new((singleSampleTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1));

        singleSampleFbo.IsMultisampled.ShouldBeFalse();
        singleSampleFbo.EffectiveSampleCount.ShouldBe(1u);

        XRRenderBuffer msaaColor = new(64u, 64u, ERenderBufferStorage.Rgba32f, 4u)
        {
            FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
        };
        XRFrameBuffer msaaFbo = new((msaaColor, EFrameBufferAttachment.ColorAttachment0, 0, -1));

        msaaFbo.IsMultisampled.ShouldBeTrue();
        msaaFbo.EffectiveSampleCount.ShouldBe(4u);
    }

    [Test]
    public void Phase2_HostContracts_ArePresent()
    {
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.cs");
        materialSource.ShouldContain("RenderOptions.AlphaToCoverage = ERenderParamUsage.Enabled;");
        materialSource.ShouldContain("if (alphaToCoverage && hasAlphaCutoff && depthWrites)");

        string framebufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/RenderTargets/XRFrameBuffer.cs");
        framebufferSource.ShouldContain("public bool IsMultisampled => EffectiveSampleCount > 1u;");
        framebufferSource.ShouldContain("XRRenderBuffer renderBuffer => renderBuffer.MultisampleCount > 1u ? renderBuffer.MultisampleCount : 1u");

        string glSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs");
        glSource.ShouldContain("ApplyAlphaToCoverage(parameters);");
        glSource.ShouldContain("EnableCap.SampleAlphaToCoverage");
        glSource.ShouldContain("XRFrameBuffer.BoundForWriting");
        glSource.ShouldContain("RenderingTargetOutputFBO");

        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("public bool EnableDeferredMsaa { get; set; } = false;");
        pipelineSource.ShouldContain("&& (Engine.Rendering.State.CurrentRenderingPipeline?.Pipeline as DefaultRenderPipeline)?.EnableDeferredMsaa == true;");
        pipelineSource.ShouldContain("public const string ForwardPassMsaaDepthViewTextureName = \"ForwardPassMsaaDepthView\";");
        pipelineSource.ShouldContain("depthViewTextureName: ForwardPassMsaaDepthViewTextureName");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs");
        pipeline2Source.ShouldContain("public bool EnableDeferredMsaa { get; set; } = false;");
        pipeline2Source.ShouldContain("&& (Engine.Rendering.State.CurrentRenderingPipeline?.Pipeline as DefaultRenderPipeline2)?.EnableDeferredMsaa == true;");
        pipeline2Source.ShouldContain("public const string ForwardPassMsaaDepthViewTextureName = \"ForwardPassMsaaDepthView\";");

        string resolveSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_ResolveMsaaGBuffer.cs");
        resolveSource.ShouldContain("public string DepthViewTextureName { get; set; } = DefaultRenderPipeline.MsaaDepthViewTextureName;");
        resolveSource.ShouldContain("ActivePipelineInstance.GetTexture<XRTexture>(DepthViewTextureName)");

        string vkSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.RenderState.cs");
        vkSource.ShouldContain("_state.SetAlphaToCoverageEnabled(parameters.AlphaToCoverage == ERenderParamUsage.Enabled);");

        string vkMeshSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs");
        vkMeshSource.ShouldContain("SampleCountFlags RasterizationSamples");
        vkMeshSource.ShouldContain("bool AlphaToCoverageEnabled");
        vkMeshSource.ShouldContain("bool requestedAlphaToCoverage = matOpts?.AlphaToCoverage == ERenderParamUsage.Enabled;");
        vkMeshSource.ShouldContain("alphaToCoverageEnabled = requestedAlphaToCoverage && rasterizationSamples != SampleCountFlags.Count1Bit;");
        vkMeshSource.ShouldContain("private static SampleCountFlags ResolveRasterizationSamples(XRFrameBuffer? target)");

        string vkPipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Pipeline.cs");
        vkPipelineSource.ShouldContain("draw.RasterizationSamples");
        vkPipelineSource.ShouldContain("draw.AlphaToCoverageEnabled");
        vkPipelineSource.ShouldContain("AlphaToCoverageEnable = draw.AlphaToCoverageEnabled ? Vk.True : Vk.False");
    }

    [Test]
    public void TransparencySceneCopy_UsesDedicatedHdrCopyPass()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("public const string SceneCopyFBOName = \"SceneCopyFBO\";");
        pipelineSource.ShouldContain("CreateSceneCopyFBO");
        pipelineSource.ShouldContain("SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName)");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs");
        pipeline2Source.ShouldContain("public const string SceneCopyFBOName = \"SceneCopyFBO\";");

        string exactTransparencySource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.ExactTransparency.cs");
        exactTransparencySource.ShouldContain("SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName)");
        exactTransparencySource.ShouldNotContain("SetTargets(ForwardPassFBOName, TransparentSceneCopyFBOName)");

        string exactTransparency2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.ExactTransparency.cs");
        exactTransparency2Source.ShouldContain("SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName)");
        exactTransparency2Source.ShouldNotContain("SetTargets(ForwardPassFBOName, TransparentSceneCopyFBOName)");

        string sceneCopyShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/SceneCopy.fs");
        sceneCopyShader.ShouldContain("uniform sampler2D HDRSceneTex;");
        sceneCopyShader.ShouldContain("OutColor = texture(HDRSceneTex, uv);");

        string sceneCopyStereoShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/SceneCopyStereo.fs");
        sceneCopyStereoShader.ShouldContain("uniform sampler2DArray HDRSceneTex;");
        sceneCopyStereoShader.ShouldContain("OutColor = texture(HDRSceneTex, uv);");
    }

    [Test]
    public void DeferredGeometry_UsesDedicatedGBufferFbo_InsteadOfAoQuadFbo()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("public const string DeferredGBufferFBOName = \"DeferredGBufferFBO\";");
        pipelineSource.ShouldContain("private bool NeedsRecreateDeferredGBufferFbo(XRFrameBuffer fbo)");
        pipelineSource.ShouldContain("x.DynamicName = () => RuntimeEnableMsaaDeferred ? MsaaGBufferFBOName : DeferredGBufferFBOName;");
        pipelineSource.ShouldContain("CreateDeferredGBufferFBO,\n                GetDesiredFBOSizeInternal,\n                NeedsRecreateDeferredGBufferFbo);");
        pipelineSource.ShouldContain("MsaaGBufferFBOName,\n                        DeferredGBufferFBOName,");
        pipelineSource.ShouldNotContain("x.DynamicName = () => RuntimeEnableMsaaDeferred ? MsaaGBufferFBOName : AmbientOcclusionFBOName;");

        string pipelineFboSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs");
        pipelineFboSource.ShouldContain("private XRFrameBuffer CreateDeferredGBufferFBO()");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("public const string DeferredGBufferFBOName = \"DeferredGBufferFBO\";");
        pipeline2Source.ShouldContain("DeferredGBufferFBOName,");
        pipeline2Source.ShouldContain("private bool NeedsRecreateDeferredGBufferFbo(XRFrameBuffer fbo)");

        string pipeline2CommandChainSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs").Replace("\r\n", "\n");
        pipeline2CommandChainSource.ShouldContain("x.DynamicName = () => RuntimeEnableMsaaDeferred ? MsaaGBufferFBOName : DeferredGBufferFBOName;");
        pipeline2CommandChainSource.ShouldContain("CreateDeferredGBufferFBO,\n            GetDesiredFBOSizeInternal,\n            NeedsRecreateDeferredGBufferFbo);");
        pipeline2CommandChainSource.ShouldContain("MsaaGBufferFBOName,\n                    DeferredGBufferFBOName,");
        pipeline2CommandChainSource.ShouldNotContain("x.DynamicName = () => RuntimeEnableMsaaDeferred ? MsaaGBufferFBOName : AmbientOcclusionFBOName;");

        string pipeline2FboSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.FBOs.cs");
        pipeline2FboSource.ShouldContain("private XRFrameBuffer CreateDeferredGBufferFBO()");
    }

    [Test]
    public void MsaaLightCombineQuad_UsesMaterialIdentityPredicate_InsteadOfMsaaAttachmentPredicate()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("private bool NeedsRecreateMsaaLightCombineFbo(XRFrameBuffer fbo)");
        pipelineSource.ShouldContain("if (fbo is not XRQuadFrameBuffer quadFbo || quadFbo.Material is not XRMaterial material)");
        pipelineSource.ShouldContain("MsaaLightCombineFBOName,\n                CreateMsaaLightCombineFBO,\n                GetDesiredFBOSizeInternal,\n                NeedsRecreateMsaaLightCombineFbo);");
        pipelineSource.ShouldNotContain("MsaaLightCombineFBOName,\n                CreateMsaaLightCombineFBO,\n                GetDesiredFBOSizeInternal,\n                NeedsRecreateMsaaFbo);");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("private bool NeedsRecreateMsaaLightCombineFbo(XRFrameBuffer fbo)");

        string pipeline2CommandChainSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs").Replace("\r\n", "\n");
        pipeline2CommandChainSource.ShouldContain("MsaaLightCombineFBOName,\n            CreateMsaaLightCombineFBO,\n            GetDesiredFBOSizeInternal,\n            NeedsRecreateMsaaLightCombineFbo);");
        pipeline2CommandChainSource.ShouldNotContain("MsaaLightCombineFBOName,\n            CreateMsaaLightCombineFBO,\n            GetDesiredFBOSizeInternal,\n            NeedsRecreateMsaaFbo);");
    }

    [Test]
    public void LightCombineQuad_UsesMaterialIdentityPredicate_InsteadOfSizeOnlyCache()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("private bool NeedsRecreateLightCombineFbo(XRFrameBuffer fbo)");
        pipelineSource.ShouldContain("if (!HasSingleColorTarget(fbo, DiffuseTextureName))");
        pipelineSource.ShouldContain("!ReferenceEquals(textures[5], GetTexture<XRTexture>(DiffuseTextureName))");
        pipelineSource.ShouldContain("LightCombineFBOName,\n                CreateLightCombineFBO,\n                GetDesiredFBOSizeInternal,\n                NeedsRecreateLightCombineFbo)\n                .UseLifetime(RenderResourceLifetime.Transient);");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("private bool NeedsRecreateLightCombineFbo(XRFrameBuffer fbo)");
        pipeline2Source.ShouldContain("var (target, attachment, mipLevel, layerIndex) = targets[0];");
        pipeline2Source.ShouldContain("!ReferenceEquals(textures[5], GetTexture<XRTexture>(DiffuseTextureName))");

        string pipeline2CommandChainSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs").Replace("\r\n", "\n");
        pipeline2CommandChainSource.ShouldContain("LightCombineFBOName,\n            CreateLightCombineFBO,\n            GetDesiredFBOSizeInternal,\n            NeedsRecreateLightCombineFbo)\n            .UseLifetime(RenderResourceLifetime.Transient);");
    }

    [Test]
    public void AmbientOcclusionModeEvaluation_UsesResolvedCameraFallbacks()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("private AmbientOcclusionSettings? ResolveAmbientOcclusionSettings()");
        pipelineSource.ShouldContain("var camera = State.SceneCamera\n            ?? State.RenderingCamera\n            ?? CurrentRenderingPipeline?.LastSceneCamera\n            ?? CurrentRenderingPipeline?.LastRenderingCamera;");
        pipelineSource.ShouldContain("AmbientOcclusionSettings? aoSettings = ResolveAmbientOcclusionSettings();");
        pipelineSource.ShouldContain("if (aoSettings is null || !aoSettings.Enabled)");
        pipelineSource.ShouldContain("AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings();");
        pipelineSource.ShouldContain("return settings?.Enabled == true;");
        pipelineSource.ShouldNotContain("var aoStage = State.SceneCamera?.GetPostProcessStageState<AmbientOcclusionSettings>();");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("private AmbientOcclusionSettings? ResolveAmbientOcclusionSettings()");
        pipeline2Source.ShouldContain("var camera = State.SceneCamera\n            ?? State.RenderingCamera\n            ?? CurrentRenderingPipeline?.LastSceneCamera\n            ?? CurrentRenderingPipeline?.LastRenderingCamera;");
        pipeline2Source.ShouldContain("AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings();");
        pipeline2Source.ShouldContain("return settings?.Enabled == true;");
        pipeline2Source.ShouldNotContain("var aoStage = State.SceneCamera?.GetPostProcessStageState<AmbientOcclusionSettings>();");

        string pipeline2CommandChainSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs").Replace("\r\n", "\n");
        pipeline2CommandChainSource.ShouldContain("AmbientOcclusionSettings? aoSettings = ResolveAmbientOcclusionSettings();");
        pipeline2CommandChainSource.ShouldContain("if (aoSettings is null || !aoSettings.Enabled)");
        pipeline2CommandChainSource.ShouldNotContain("var aoStage = State.SceneCamera?.GetPostProcessStageState<AmbientOcclusionSettings>();");
    }

    [Test]
    public void AmbientOcclusionNoiseTextures_UseShaderSamplerName()
    {
        string ssaoSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/AO/VPRC_SSAOPass.cs");
        ssaoSource.ShouldContain("SamplerName = \"AONoiseTexture\"");

        string mvaoSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/AO/VPRC_MVAOPass.cs");
        mvaoSource.ShouldContain("SamplerName = \"AONoiseTexture\"");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}
