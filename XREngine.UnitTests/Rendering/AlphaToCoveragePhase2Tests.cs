using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Runtime.Bootstrap;

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
    public void LightCombineQuad_DisablesMaterialDerivedTargets_ToMatchItsRecreateValidator()
    {
        string pipelineFboSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs");
        pipelineFboSource.ShouldContain("new XRQuadFrameBuffer(lightCombineMat, useTriangle: true, deriveRenderTargetsFromMaterial: false)");

        string pipeline2FboSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.FBOs.cs");
        pipeline2FboSource.ShouldContain("new XRQuadFrameBuffer(lightCombineMat, useTriangle: true, deriveRenderTargetsFromMaterial: false)");
    }

    [Test]
    public void MsaaAttachmentFbos_ValidateCurrentDepthAndColorAttachments()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("MsaaGBufferFBOName => !HasMsaaGBufferTargets(fbo)");
        pipelineSource.ShouldContain("MsaaLightingFBOName => !HasMsaaLightingTargets(fbo)");
        pipelineSource.ShouldContain("ForwardPassMsaaFBOName => !HasForwardPassMsaaTargets(fbo)");
        pipelineSource.ShouldContain("HasTextureAttachment(targets[4], MsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment)");
        pipelineSource.ShouldContain("HasTextureAttachment(targets[1], ForwardPassMsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment)");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("MsaaGBufferFBOName => !HasMsaaGBufferTargets(fbo)");
        pipeline2Source.ShouldContain("MsaaLightingFBOName => !HasMsaaLightingTargets(fbo)");
        pipeline2Source.ShouldContain("ForwardPassMsaaFBOName => !HasForwardPassMsaaTargets(fbo)");
        pipeline2Source.ShouldContain("HasTextureAttachment(targets[4], MsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment)");
        pipeline2Source.ShouldContain("HasTextureAttachment(targets[1], ForwardPassMsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment)");
    }

    [Test]
    public void Pipeline2_PostAaFbos_UseAttachmentIdentityPredicates()
    {
        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("private bool NeedsRecreatePostProcessOutputFbo(XRFrameBuffer fbo)");
        pipeline2Source.ShouldContain("private bool NeedsRecreateFxaaFbo(XRFrameBuffer fbo)");
        pipeline2Source.ShouldContain("private bool NeedsRecreateTsrHistoryColorFbo(XRFrameBuffer fbo)");
        pipeline2Source.ShouldContain("private bool NeedsRecreateTsrUpscaleFbo(XRFrameBuffer fbo)");
        pipeline2Source.ShouldContain("return !HasSingleColorTarget(fbo, PostProcessOutputTextureName);");
        pipeline2Source.ShouldContain("!ReferenceEquals(textures[0], GetTexture<XRTexture>(PostProcessOutputTextureName))");
        pipeline2Source.ShouldContain("!ReferenceEquals(textures[4], GetTexture<XRTexture>(TsrHistoryColorTextureName))");

        string pipeline2CommandChainSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs").Replace("\r\n", "\n");
        pipeline2CommandChainSource.ShouldContain("PostProcessOutputFBOName,\n            CreatePostProcessOutputFBO,\n            GetDesiredFBOSizeInternal,\n            NeedsRecreatePostProcessOutputFbo);");
        pipeline2CommandChainSource.ShouldContain("FxaaFBOName,\n            CreateFxaaFBO,\n            GetDesiredFBOSizeFull,\n            NeedsRecreateFxaaFbo);");
        pipeline2CommandChainSource.ShouldContain("TsrHistoryColorFBOName,\n            CreateTsrHistoryColorFBO,\n            GetDesiredFBOSizeFull,\n            NeedsRecreateTsrHistoryColorFbo);");
        pipeline2CommandChainSource.ShouldContain("TsrUpscaleFBOName,\n            CreateTsrUpscaleFBO,\n            GetDesiredFBOSizeFull,\n            NeedsRecreateTsrUpscaleFbo);");
        pipeline2CommandChainSource.ShouldNotContain("TsrUpscaleFBOName,\n            CreateTsrUpscaleFBO,\n            GetDesiredFBOSizeFull,\n            NeedsRecreateFboDueToOutputFormat);");
    }

    [Test]
    public void ForwardPassMsaaColorBuffer_UsesHdrSceneFormat()
    {
        string pipelineFboSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs").Replace("\r\n", "\n");
        pipelineFboSource.ShouldContain("private ERenderBufferStorage GetForwardMsaaColorFormat()");
        pipelineFboSource.ShouldContain("=> ERenderBufferStorage.Rgba16f;");

        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("renderBuffer.Type != GetForwardMsaaColorFormat())");

        string pipeline2FboSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.FBOs.cs").Replace("\r\n", "\n");
        pipeline2FboSource.ShouldContain("private ERenderBufferStorage GetForwardMsaaColorFormat()");
        pipeline2FboSource.ShouldContain("=> ERenderBufferStorage.Rgba16f;");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("renderBuffer.Type != GetForwardMsaaColorFormat())");
    }

    [Test]
    public void AntiAliasingInvalidation_ResetsTemporalHistoryState()
    {
        string temporalSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs").Replace("\r\n", "\n");
        temporalSource.ShouldContain("internal static void ResetHistory(XRRenderPipelineInstance? instance)");
        temporalSource.ShouldContain("state.HistoryReady = false;");
        temporalSource.ShouldContain("state.HistoryExposureReady = false;");
        temporalSource.ShouldContain("state.PendingHistoryReady = false;");

        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("VPRC_TemporalAccumulationPass.ResetHistory(instance);");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("VPRC_TemporalAccumulationPass.ResetHistory(instance);");
    }


    [Test]
    public void ViewportResize_EvictsPostProcessSourceChain_AndRequestsRenderRecheck()
    {
        string instanceSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/XRRenderPipelineInstance.cs").Replace("\r\n", "\n");
        instanceSource.ShouldContain("case DefaultRenderPipeline pipeline:");
        instanceSource.ShouldContain("pipeline.HandleViewportResized(this, width, height);");
        instanceSource.ShouldContain("case DefaultRenderPipeline2 pipeline:");

        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("internal void HandleViewportResized(XRRenderPipelineInstance instance, int width, int height)");
        pipelineSource.ShouldContain("const string reason = \"ViewportResized\";");
        pipelineSource.ShouldContain("foreach (string name in AntiAliasingFrameBufferDependencies)");
        pipelineSource.ShouldContain("foreach (string name in ResizeRecoveryFrameBufferDependencies)");
        pipelineSource.ShouldContain("foreach (string name in AntiAliasingTextureDependencies)");
        pipelineSource.ShouldContain("foreach (string name in ResizeRecoveryTextureDependencies)");
        pipelineSource.ShouldContain("HDRSceneTextureName");
        pipelineSource.ShouldContain("BloomBlurTextureName");
        pipelineSource.ShouldContain("PostProcessOutputTextureName");
        pipelineSource.ShouldContain("FxaaOutputTextureName");
        pipelineSource.ShouldContain("SceneCopyFBOName");
        pipelineSource.ShouldContain("PostProcessOutputFBOName");
        pipelineSource.ShouldContain("FxaaFBOName");
        pipelineSource.ShouldContain("RequestRenderStateRecheck(resetCircuitBreaker: true);");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("internal void HandleViewportResized(XRRenderPipelineInstance instance, int width, int height)");
        pipeline2Source.ShouldContain("const string reason = \"ViewportResized\";");
        pipeline2Source.ShouldContain("foreach (string name in AntiAliasingFrameBufferDependencies)");
        pipeline2Source.ShouldContain("foreach (string name in ResizeRecoveryFrameBufferDependencies)");
        pipeline2Source.ShouldContain("foreach (string name in AntiAliasingTextureDependencies)");
        pipeline2Source.ShouldContain("foreach (string name in ResizeRecoveryTextureDependencies)");
        pipeline2Source.ShouldContain("HDRSceneTextureName");
        pipeline2Source.ShouldContain("BloomBlurTextureName");
        pipeline2Source.ShouldContain("PostProcessOutputTextureName");
        pipeline2Source.ShouldContain("FxaaOutputTextureName");
        pipeline2Source.ShouldContain("SceneCopyFBOName");
        pipeline2Source.ShouldContain("PostProcessOutputFBOName");
        pipeline2Source.ShouldContain("FxaaFBOName");
        pipeline2Source.ShouldContain("RequestRenderStateRecheck(resetCircuitBreaker: true);");
    }

    [Test]
    public void SurfaceDetailNormalMapping_FallsBackToHeightReconstructionForGrayscaleInputs()
    {
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Snippets/SurfaceDetailNormalMapping.glsl").Replace("\r\n", "\n");
        shaderSource.ShouldContain("vec3 T = tangentWS - N * dot(N, tangentWS);");
        shaderSource.ShouldContain("float grayscaleDelta = max(abs(sampledColor.r - sampledColor.g)");
        shaderSource.ShouldContain("if (grayscaleDelta <= 0.02)");
        shaderSource.ShouldContain("tangentNormal = XRENGINE_HeightToNormalSobel(uv);");
    }

    [Test]
    public void PostProcessOutput_IsMaterialized_BeforeFinalPresentation()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("c.Add<VPRC_RenderQuadToFBO>().SetTargets(PostProcessFBOName, PostProcessOutputFBOName);");
        pipelineSource.ShouldContain("upscaleOutputChoice.FalseCommands = CreateFinalBlitCommands(PostProcessOutputFBOName, bypassVendorUpscale);");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("c.Add<VPRC_RenderQuadToFBO>().SetTargets(PostProcessFBOName, PostProcessOutputFBOName);");
        pipeline2Source.ShouldContain("upscaleOutputChoice.FalseCommands = CreateFinalBlitCommands(PostProcessOutputFBOName, bypassVendorUpscale);");
    }

    [Test]
    public void UnitTestingWorldVolumetricFogSources_MatchRuntimeDefaults()
    {
        var editorFog = new EditorUnitTests.Settings.VolumetricFogVolumeInitSettings();
        var bootstrapFog = new UnitTestingWorldSettings.VolumetricFogVolumeInitSettings();
        var runtimeFog = new VolumetricFogSettings();

        editorFog.MaxDistance.ShouldBe(runtimeFog.MaxDistance);
        editorFog.StepSize.ShouldBe(runtimeFog.StepSize);
        editorFog.JitterStrength.ShouldBe(runtimeFog.JitterStrength);

        bootstrapFog.MaxDistance.ShouldBe(runtimeFog.MaxDistance);
        bootstrapFog.StepSize.ShouldBe(runtimeFog.StepSize);
        bootstrapFog.JitterStrength.ShouldBe(runtimeFog.JitterStrength);
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

    [Test]
    public void PostAaTextures_UseStableHdrIntermediateFormat()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("private static EPixelInternalFormat ResolvePostProcessIntermediateInternalFormat()\n        => EPixelInternalFormat.Rgba16f;");
        pipelineSource.ShouldContain("private static bool NeedsRecreatePostProcessTextureInternalSize(XRTexture texture)");
        pipelineSource.ShouldContain("private static bool NeedsRecreatePostProcessTextureFullSize(XRTexture texture)");
        pipelineSource.ShouldContain("private static bool NeedsRecreateFboDueToPostProcessIntermediateFormat(XRFrameBuffer fbo)");
        pipelineSource.ShouldContain("NeedsRecreateFboDueToPostProcessIntermediateFormat(fbo)");
        pipelineSource.ShouldContain("PostProcessOutputTextureName,\n            CreatePostProcessOutputTexture,\n            NeedsRecreatePostProcessTextureInternalSize,");
        pipelineSource.ShouldContain("FxaaOutputTextureName,\n                CreateFxaaOutputTexture,\n                NeedsRecreatePostProcessTextureFullSize,");
        pipelineSource.ShouldContain("TsrHistoryColorTextureName,\n                CreateTsrHistoryColorTexture,\n                NeedsRecreatePostProcessTextureFullSize,");

        string texturesSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs").Replace("\r\n", "\n");
        texturesSource.ShouldContain("EPixelInternalFormat internalFormat = ResolvePostProcessIntermediateInternalFormat();");
        texturesSource.ShouldContain("EPixelType pixelType = ResolvePostProcessIntermediatePixelType();");
        texturesSource.ShouldContain("ESizedInternalFormat sized = ResolvePostProcessIntermediateSizedInternalFormat();");

        string pipeline2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs").Replace("\r\n", "\n");
        pipeline2Source.ShouldContain("private static EPixelInternalFormat ResolvePostProcessIntermediateInternalFormat()\n        => EPixelInternalFormat.Rgba16f;");
        pipeline2Source.ShouldContain("private static bool NeedsRecreatePostProcessTextureInternalSize(XRTexture texture)");
        pipeline2Source.ShouldContain("private static bool NeedsRecreatePostProcessTextureFullSize(XRTexture texture)");
        pipeline2Source.ShouldContain("private static bool NeedsRecreateFboDueToPostProcessIntermediateFormat(XRFrameBuffer fbo)");
        pipeline2Source.ShouldContain("NeedsRecreateFboDueToPostProcessIntermediateFormat(fbo)");

        string pipeline2CommandChainSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs").Replace("\r\n", "\n");
        pipeline2CommandChainSource.ShouldContain("PostProcessOutputTextureName,\n            CreatePostProcessOutputTexture,\n            NeedsRecreatePostProcessTextureInternalSize,");
        pipeline2CommandChainSource.ShouldContain("FxaaOutputTextureName,\n            CreateFxaaOutputTexture,\n            NeedsRecreatePostProcessTextureFullSize,");
        pipeline2CommandChainSource.ShouldContain("TsrHistoryColorTextureName,\n            CreateTsrHistoryColorTexture,\n            NeedsRecreatePostProcessTextureFullSize,");

        string textures2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.Textures.cs").Replace("\r\n", "\n");
        textures2Source.ShouldContain("EPixelInternalFormat internalFormat = ResolvePostProcessIntermediateInternalFormat();");
        textures2Source.ShouldContain("EPixelType pixelType = ResolvePostProcessIntermediatePixelType();");
        textures2Source.ShouldContain("ESizedInternalFormat sized = ResolvePostProcessIntermediateSizedInternalFormat();");
    }

    [Test]
    public void TsrUpscale_RequiresExposureReadyBeforeUsingHistory()
    {
        string postProcessSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        postProcessSource.ShouldContain("historyReady = temporalData.HistoryReady && temporalData.HistoryExposureReady;");

        string postProcess2Source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.PostProcessing.cs").Replace("\r\n", "\n");
        postProcess2Source.ShouldContain("historyReady = temporalData.HistoryReady && temporalData.HistoryExposureReady;");
    }

    [Test]
    public void DeferredMsaaComposite_UsesResolvedLightCombineQuad()
    {
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("Present the resolved deferred light buffer for both standard and MSAA modes.");
        pipelineSource.ShouldContain("c.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = LightCombineFBOName;");
        pipelineSource.ShouldNotContain("msaaCmds.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = MsaaLightCombineFBOName;");

        string pipeline2CommandChainSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs").Replace("\r\n", "\n");
        pipeline2CommandChainSource.ShouldContain("Present the resolved deferred light buffer for both standard and MSAA modes.");
        pipeline2CommandChainSource.ShouldContain("c.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = LightCombineFBOName;");
        pipeline2CommandChainSource.ShouldNotContain("msaaCmds.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = MsaaLightCombineFBOName;");
    }

    [Test]
    public void VolumetricFog_UsesLightDrivenStableDither()
    {
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/PostProcess.fs").Replace("\r\n", "\n");
        shaderSource.ShouldContain("float interleavedGradientNoise(vec2 pixelCoord)");
        shaderSource.ShouldContain("return vec3(0.0f);");
        shaderSource.ShouldContain("return lightColor * shadowFactor * lightContribution * phase * 4.0f;");
        shaderSource.ShouldContain("float jitter = interleavedGradientNoise(gl_FragCoord.xy) * stepSize * VolumetricFog.JitterStrength;");
        shaderSource.ShouldNotContain("return vec3(1.0f);");
        shaderSource.ShouldNotContain("vec3(1.0f) + lightColor");
        shaderSource.ShouldNotContain("fract(RenderTime * 7.0)");
    }

    [Test]
    public void VolumetricFog_DisabledPath_UploadsInertShaderState()
    {
        string settingsSource = ReadWorkspaceFile("XRENGINE/Rendering/Camera/VolumetricFogSettings.cs").Replace("\r\n", "\n");
        settingsSource.ShouldContain("_activeVolumes[i] = null;");
        settingsSource.ShouldContain("_worldToLocal[i] = Matrix4x4.Identity;");
        settingsSource.ShouldContain("_lightParams[i] = Vector4.Zero;");
        settingsSource.ShouldContain("bool shaderEnabled = activeCount > 0;");
        settingsSource.ShouldContain("program.Uniform($\"{StructUniformName}.Enabled\", shaderEnabled);");
        settingsSource.ShouldContain("program.Uniform($\"{StructUniformName}.Intensity\", shaderEnabled ? Intensity : 0.0f);");
        settingsSource.ShouldContain("program.Uniform($\"{StructUniformName}.MaxDistance\", shaderEnabled ? MaxDistance : 0.0f);");
        settingsSource.ShouldContain("program.Uniform($\"{StructUniformName}.JitterStrength\", shaderEnabled ? JitterStrength : 0.0f);");
        settingsSource.ShouldContain("program.Uniform($\"{StructUniformName}.VolumeCount\", activeCount);");
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
