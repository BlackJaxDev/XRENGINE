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
        material.RenderPass = (int)EDefaultRenderPass.OpaqueForward;

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

        string glSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Commands/OpenGLRenderer.RenderParameters.cs");
        glSource.ShouldContain("ApplyAlphaToCoverage(parameters);");
        glSource.ShouldContain("EnableCap.SampleAlphaToCoverage");
        glSource.ShouldContain("XRFrameBuffer.BoundForWriting");
        glSource.ShouldContain("RenderingTargetOutputFBO");

        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("public bool EnableDeferredMsaa { get; set; } = true;");
        pipelineSource.ShouldContain("&& !UseOpenXrVulkanDesktopStartupSafePath\n        && (RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline as DefaultRenderPipeline)?.EnableDeferredMsaa == true;");
        pipelineSource.ShouldContain("public const string ForwardPassMsaaDepthViewTextureName = \"ForwardPassMsaaDepthView\";");
        pipelineSource.ShouldContain("depthViewTextureName: ForwardPassMsaaDepthViewTextureName");

        string advancedPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        advancedPipelineSource.ShouldContain("public bool EnableDeferredMsaa { get; set; } = true;");
        advancedPipelineSource.ShouldContain("&& !UseOpenXrVulkanDesktopStartupSafePath\n        && (RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline as AdvancedRenderPipeline)?.EnableDeferredMsaa == true;");
        advancedPipelineSource.ShouldContain("public const string ForwardPassMsaaDepthViewTextureName = \"ForwardPassMsaaDepthView\";");

        string resolveSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ResolveMsaaGBuffer.cs");
        resolveSource.ShouldContain("public string DepthViewTextureName { get; set; } = DefaultRenderPipeline.MsaaDepthViewTextureName;");
        resolveSource.ShouldContain("ActivePipelineInstance.GetTexture<XRTexture>(DepthViewTextureName)");

        string vkSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.RenderState.cs");
        vkSource.ShouldContain("ActiveState.SetAlphaToCoverageEnabled(parameters.AlphaToCoverage == ERenderParamUsage.Enabled);");

        string vkMeshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        vkMeshSource.ShouldContain("SampleCountFlags RasterizationSamples");
        vkMeshSource.ShouldContain("bool AlphaToCoverageEnabled");
        vkMeshSource.ShouldContain("bool requestedAlphaToCoverage = matOpts?.AlphaToCoverage == ERenderParamUsage.Enabled;");
        vkMeshSource.ShouldContain("alphaToCoverageEnabled = requestedAlphaToCoverage && rasterizationSamples != SampleCountFlags.Count1Bit;");
        vkMeshSource.ShouldContain("private static SampleCountFlags ResolveRasterizationSamples(XRFrameBuffer? target)");

        string vkPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        vkPipelineSource.ShouldContain("draw.RasterizationSamples");
        vkPipelineSource.ShouldContain("draw.AlphaToCoverageEnabled");
        vkPipelineSource.ShouldContain("AlphaToCoverageEnable = effectiveDraw.AlphaToCoverageEnabled ? Vk.True : Vk.False");
    }

    [Test]
    public void TransparencySceneCopy_UsesDedicatedHdrCopyPass()
    {
        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("public const string SceneCopyFBOName = \"SceneCopyFBO\";");
        pipelineSource.ShouldContain("CreateSceneCopyFBO");
        pipelineSource.ShouldContain("SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName)");

        string advancedPipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        advancedPipelineSource.ShouldContain("public const string SceneCopyFBOName = \"SceneCopyFBO\";");

        string exactTransparencySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.ExactTransparency.cs");
        exactTransparencySource.ShouldContain("SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName)");
        exactTransparencySource.ShouldNotContain("SetTargets(ForwardPassFBOName, TransparentSceneCopyFBOName)");

        string exactTransparency2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.ExactTransparency.cs");
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
    public void BloomPass_UsesRawHdrForwardPassCopy_InsteadOfLegacyBrightPass()
    {
        string pipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs").Replace("\r\n", "\n");
        pipelineFboSource.ShouldContain("private XRFrameBuffer CreateForwardPassFBO()");
        pipelineFboSource.ShouldContain("Path.Combine(SceneShaderPath, SceneCopyShaderName())");
        pipelineFboSource.ShouldNotContain("Path.Combine(SceneShaderPath, BrightPassShaderName())");
        pipelineFboSource.ShouldNotContain("fbo.SettingUniforms += BrightPassFBO_SettingUniforms;");

        string advancedPipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.FBOs.cs").Replace("\r\n", "\n");
        advancedPipelineFboSource.ShouldContain("private XRFrameBuffer CreateForwardPassFBO()");
        advancedPipelineFboSource.ShouldContain("Path.Combine(SceneShaderPath, SceneCopyShaderName())");
        advancedPipelineFboSource.ShouldNotContain("Path.Combine(SceneShaderPath, BrightPassShaderName())");
        advancedPipelineFboSource.ShouldNotContain("fbo.SettingUniforms += BrightPassFBO_SettingUniforms;");

        string bloomPassSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_BloomPass.cs").Replace("\r\n", "\n");
        bloomPassSource.ShouldContain("// Step 1: Copy HDR scene into bloom texture mip 0.");
        bloomPassSource.ShouldContain("mip0.Render();");
    }

    [Test]
    public void BloomCombine_DefaultsUseTunedMipBlend()
    {
        string bloomSettingsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Camera/BloomSettings.cs").Replace("\r\n", "\n");
        bloomSettingsSource.ShouldContain("private bool _enabled = true;");
        bloomSettingsSource.ShouldContain("private float _intensity = 0.530f;");
        bloomSettingsSource.ShouldContain("private float _threshold = 0.138f;");
        bloomSettingsSource.ShouldContain("private float _radius = 1.495f;");
        bloomSettingsSource.ShouldContain("private float _scatter = 0.919f;");
        bloomSettingsSource.ShouldContain("private float _strength = 0.5805f;");
        bloomSettingsSource.ShouldContain("private int _startMip = 1;");
        bloomSettingsSource.ShouldContain("private int _endMip = 4;");
        bloomSettingsSource.ShouldContain("private float _lod1Weight = 1.0f;");
        bloomSettingsSource.ShouldContain("private float _lod2Weight = 0.649f;");
        bloomSettingsSource.ShouldContain("private float _lod3Weight = 0.397f;");
        bloomSettingsSource.ShouldContain("private float _lod4Weight = 0.102f;");
        bloomSettingsSource.ShouldNotContain("usesLegacySingleMipProfile");

        string pipelinePostProcessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
    pipelinePostProcessSource.ShouldContain("nameof(BloomSettings.Enabled),\n            PostProcessParameterKind.Bool,\n            true,");
        pipelinePostProcessSource.ShouldContain("nameof(BloomSettings.StartMip),\n            PostProcessParameterKind.Int,\n            1,");
        pipelinePostProcessSource.ShouldContain("nameof(BloomSettings.EndMip),\n            PostProcessParameterKind.Int,\n            4,");
        pipelinePostProcessSource.ShouldContain("nameof(BloomSettings.Lod1Weight),\n            PostProcessParameterKind.Float,\n            1.0f,");
        pipelinePostProcessSource.ShouldContain("nameof(BloomSettings.Lod2Weight),\n            PostProcessParameterKind.Float,\n            0.649f,");
        pipelinePostProcessSource.ShouldContain("nameof(BloomSettings.Lod3Weight),\n            PostProcessParameterKind.Float,\n            0.397f,");
        pipelinePostProcessSource.ShouldContain("nameof(BloomSettings.Lod4Weight),\n            PostProcessParameterKind.Float,\n            0.102f,");

        string advancedPipelinePostProcessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
    advancedPipelinePostProcessSource.ShouldContain("nameof(BloomSettings.Enabled),\n            PostProcessParameterKind.Bool,\n            true,");
        advancedPipelinePostProcessSource.ShouldContain("nameof(BloomSettings.StartMip),\n            PostProcessParameterKind.Int,\n            1,");
        advancedPipelinePostProcessSource.ShouldContain("nameof(BloomSettings.EndMip),\n            PostProcessParameterKind.Int,\n            4,");
        advancedPipelinePostProcessSource.ShouldContain("nameof(BloomSettings.Lod1Weight),\n            PostProcessParameterKind.Float,\n            1.0f,");
        advancedPipelinePostProcessSource.ShouldContain("nameof(BloomSettings.Lod2Weight),\n            PostProcessParameterKind.Float,\n            0.649f,");
        advancedPipelinePostProcessSource.ShouldContain("nameof(BloomSettings.Lod3Weight),\n            PostProcessParameterKind.Float,\n            0.397f,");
        advancedPipelinePostProcessSource.ShouldContain("nameof(BloomSettings.Lod4Weight),\n            PostProcessParameterKind.Float,\n            0.102f,");

        string postProcessShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/PostProcess.fs").Replace("\r\n", "\n");
        postProcessShader.ShouldContain("uniform int BloomStartMip = 1;");
        postProcessShader.ShouldContain("uniform int BloomEndMip = 4;");
        postProcessShader.ShouldContain("uniform float BloomLodWeights[5] = float[](0.0, 1.0, 0.649, 0.397, 0.102);");

        string postProcessStereoShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/PostProcessStereo.fs").Replace("\r\n", "\n");
        postProcessStereoShader.ShouldContain("uniform int BloomStartMip = 1;");
        postProcessStereoShader.ShouldContain("uniform int BloomEndMip = 4;");
        postProcessStereoShader.ShouldContain("uniform float BloomLodWeights[5] = float[](0.0, 1.0, 0.649, 0.397, 0.102);");
    }

    [Test]
    public void BloomStage_EnabledToggle_DisablesBloomPassAndHidesDependentControls()
    {
        string bloomSettingsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Camera/BloomSettings.cs").Replace("\r\n", "\n");
        bloomSettingsSource.ShouldContain("public bool Enabled");
        bloomSettingsSource.ShouldContain("program.Uniform(\"BloomStrength\", enabled ? MathF.Max(0.0f, Strength) : 0.0f);");
        bloomSettingsSource.ShouldContain("program.Uniform(\"DebugBloomOnly\", enabled && _debugBloomOnly);");

        string pipelinePostProcessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        pipelinePostProcessSource.ShouldContain("bool IsEnabled(object o) => ((BloomSettings)o).Enabled;");
        pipelinePostProcessSource.ShouldContain("visibilityCondition: IsEnabled");
        pipelinePostProcessSource.ShouldContain("bool settingsDisabled = GetBloomSettings() is { Enabled: false };");

        string advancedPipelinePostProcessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        advancedPipelinePostProcessSource.ShouldContain("bool IsEnabled(object o) => ((BloomSettings)o).Enabled;");
        advancedPipelinePostProcessSource.ShouldContain("visibilityCondition: IsEnabled");
        advancedPipelinePostProcessSource.ShouldContain("GetBloomSettings() is not { Enabled: false };");

        string pipelineCommandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        pipelineCommandChainSource.ShouldContain("bloomChoice.ConditionEvaluator = ShouldUseBloom;");

        string pipelineLegacySource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineLegacySource.ShouldContain("bloomChoice.ConditionEvaluator = ShouldUseBloom;");
    }

    [Test]
    public void DeferredGeometry_UsesDedicatedGBufferFbo_InsteadOfAoQuadFbo()
    {
        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("public const string DeferredGBufferFBOName = \"DeferredGBufferFBO\";");
        pipelineSource.ShouldContain("private bool NeedsRecreateDeferredGBufferFbo(XRFrameBuffer fbo)");

        string pipelineCommandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        pipelineCommandChainSource.ShouldContain("x.DynamicName = () => RuntimeEnableMsaaDeferred ? MsaaGBufferFBOName : DeferredGBufferFBOName;");
        pipelineSource.ShouldContain("builder.FrameBuffer(DeferredGBufferFBOName)");
        pipelineSource.ShouldContain(".Factory(CreateDeferredGBufferFBO)");
        pipelineCommandChainSource.ShouldContain("MsaaGBufferFBOName,");
        pipelineCommandChainSource.ShouldContain("DeferredGBufferFBOName,");
        pipelineCommandChainSource.ShouldNotContain("x.DynamicName = () => RuntimeEnableMsaaDeferred ? MsaaGBufferFBOName : AmbientOcclusionFBOName;");

        string pipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs");
        pipelineFboSource.ShouldContain("private XRFrameBuffer CreateDeferredGBufferFBO()");

    }

    [Test]
    public void MsaaLightCombineQuad_UsesMaterialIdentityPredicate_InsteadOfMsaaAttachmentPredicate()
    {
        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("private bool NeedsRecreateMsaaLightCombineFbo(XRFrameBuffer fbo)");
        pipelineSource.ShouldContain("if (fbo is not XRQuadFrameBuffer quadFbo || quadFbo.Material is not XRMaterial material)");

        string pipelineCommandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("builder.QuadMaterial(MsaaLightCombineFBOName)");
        pipelineSource.ShouldContain(".Factory(CreateMsaaLightCombineFBO)");
        pipelineCommandChainSource.ShouldNotContain("MsaaLightCombineFBOName,\n            CreateMsaaLightCombineFBO,\n            GetDesiredFBOSizeInternal,\n            NeedsRecreateMsaaFbo);");

    }

    [Test]
    public void LightCombineQuad_UsesMaterialIdentityPredicate_InsteadOfSizeOnlyCache()
    {
        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("private bool NeedsRecreateLightCombineFbo(XRFrameBuffer fbo)");
        pipelineSource.ShouldContain("if (!HasSingleColorTarget(fbo, DiffuseTextureName))");
        pipelineSource.ShouldContain("!ReferenceEquals(textures[5], GetTexture<XRTexture>(LightingAccumTextureName))");
        pipelineSource.ShouldContain("private bool NeedsRecreateLightingAccumFbo(XRFrameBuffer fbo)");
        pipelineSource.ShouldContain("return !HasSingleColorTarget(fbo, LightingAccumTextureName);");

        string pipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs").Replace("\r\n", "\n");
        pipelineFboSource.ShouldContain("BlendModeAllDrawBuffers = BlendMode.Disabled()");

        string pipelineResourceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Resources.cs").Replace("\r\n", "\n");
        pipelineResourceSource.ShouldContain("builder.FrameBuffer(LightingAccumFBOName)");
        pipelineResourceSource.ShouldContain(".Factory(CreateLightingAccumFBO)");
        pipelineResourceSource.ShouldContain("builder.FrameBuffer(LightCombineFBOName)");
        pipelineResourceSource.ShouldContain(".Factory(CreateLightCombineFBO)");

        string pipelineCommandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        pipelineCommandChainSource.ShouldContain("x.SetOptions(LightingAccumFBOName, clearDepth: false, clearStencil: false)");
        pipelineCommandChainSource.ShouldNotContain("LightCombineFBOName,\n            CreateLightCombineFBO,\n            GetDesiredFBOSizeInternal,\n            NeedsRecreateLightCombineFbo)");

    }

    [Test]
    public void LightCombineQuad_DisablesMaterialDerivedTargets_ToMatchItsRecreateValidator()
    {
        string pipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs");
        pipelineFboSource.ShouldContain("new XRQuadFrameBuffer(lightCombineMat, useTriangle: true, deriveRenderTargetsFromMaterial: false)");

        string advancedPipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.FBOs.cs");
        advancedPipelineFboSource.ShouldContain("new XRQuadFrameBuffer(lightCombineMat, useTriangle: true, deriveRenderTargetsFromMaterial: false)");
    }

    [Test]
    public void ForwardPassQuad_UsesAttachmentIdentityPredicate_InsteadOfSizeOnlyCache()
    {
        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("private bool NeedsRecreateForwardPassFbo(XRFrameBuffer fbo)");
        pipelineSource.ShouldContain("HasTextureAttachment(targets[0], HDRSceneTextureName, EFrameBufferAttachment.ColorAttachment0)");
        pipelineSource.ShouldContain("HasTextureAttachment(targets[1], DepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment)");

        string pipelineCommandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("builder.FrameBuffer(ForwardPassFBOName)");
        pipelineSource.ShouldContain(".Factory(CreateForwardPassFBO)");

    }

    [Test]
    public void ForwardPassQuad_DisablesMaterialDerivedTargets_ToMatchItsRecreateValidator()
    {
        string pipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs");
        pipelineFboSource.ShouldContain("new XRQuadFrameBuffer(sceneCopyMat, useTriangle: false, deriveRenderTargetsFromMaterial: false)");

        string advancedPipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.FBOs.cs");
        advancedPipelineFboSource.ShouldContain("new XRQuadFrameBuffer(sceneCopyMat, useTriangle: false, deriveRenderTargetsFromMaterial: false)");
    }

    [Test]
    public void TransparencyResolveQuads_RenderIntoTheirOwnExplicitTargets()
    {
        string pipelineCommandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        pipelineCommandSource.ShouldContain(".SetOptions(DeferredTransparencyBlurFBOName, renderToSourceFrameBuffer: true);");
        pipelineCommandSource.ShouldContain(".SetOptions(TransparentResolveFBOName, renderToSourceFrameBuffer: true);");

        string pipelineExactSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.ExactTransparency.cs").Replace("\r\n", "\n");
        pipelineExactSource.ShouldContain("c.Add<VPRC_RenderQuadToFBO>().SetOptions(PpllResolveFBOName, renderToSourceFrameBuffer: true);");
        pipelineExactSource.ShouldContain("c.Add<VPRC_RenderQuadToFBO>().SetOptions(DepthPeelingResolveFBOName, renderToSourceFrameBuffer: true);");

    }

    [Test]
    public void TransparencyResolveQuads_DisableMaterialDerivedTargets()
    {
        string pipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs");
        pipelineFboSource.ShouldContain("new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = DeferredTransparencyBlurFBOName }");
        pipelineFboSource.ShouldContain("new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = TransparentResolveFBOName }");

        string pipelineExactSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.ExactTransparency.cs");
        pipelineExactSource.ShouldContain("new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = PpllResolveFBOName }");
        pipelineExactSource.ShouldContain("new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = DepthPeelingResolveFBOName }");

        string advancedPipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.FBOs.cs");
        advancedPipelineFboSource.ShouldContain("new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = DeferredTransparencyBlurFBOName }");
        advancedPipelineFboSource.ShouldContain("new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = TransparentResolveFBOName }");

        string advancedPipelineExactSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.ExactTransparency.cs");
        advancedPipelineExactSource.ShouldContain("new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = PpllResolveFBOName }");
        advancedPipelineExactSource.ShouldContain("new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = DepthPeelingResolveFBOName }");
    }

    [Test]
    public void MsaaAttachmentFbos_ValidateCurrentDepthAndColorAttachments()
    {
        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("MsaaGBufferFBOName => !HasMsaaGBufferTargets(fbo)");
        pipelineSource.ShouldContain("MsaaLightingFBOName => !HasMsaaLightingTargets(fbo)");
        pipelineSource.ShouldContain("ForwardPassMsaaFBOName => !HasForwardPassMsaaTargets(fbo)");
        pipelineSource.ShouldContain("HasTextureAttachment(targets[4], MsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment)");
        pipelineSource.ShouldContain("HasTextureAttachment(targets[1], ForwardPassMsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment)");

        string advancedPipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        advancedPipelineSource.ShouldContain("MsaaGBufferFBOName => !HasMsaaGBufferTargets(fbo)");
        advancedPipelineSource.ShouldContain("MsaaLightingFBOName => !HasMsaaLightingTargets(fbo)");
        advancedPipelineSource.ShouldContain("ForwardPassMsaaFBOName => !HasForwardPassMsaaTargets(fbo)");
        advancedPipelineSource.ShouldContain("HasTextureAttachment(targets[4], MsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment)");
        advancedPipelineSource.ShouldContain("HasTextureAttachment(targets[1], ForwardPassMsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment)");
    }

    [Test]
    public void AdvancedPipeline_PostAaResourcesRemainDisconnectedFromTheFrameSkeleton()
    {
        string resources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.Resources.cs");
        string commandChain = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.CommandChain.cs");

        resources.ShouldNotContain("PostProcessOutputFBOName");
        resources.ShouldNotContain("FxaaFBOName");
        resources.ShouldNotContain("TsrHistoryColorFBOName");
        resources.ShouldNotContain("TsrUpscaleFBOName");
        commandChain.ShouldContain("AdvancedRenderPipelineFrameContract.OrderedStages");
    }

    [Test]
    public void ForwardPassMsaaColorBuffer_UsesHdrSceneFormat()
    {
        string pipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs").Replace("\r\n", "\n");
        pipelineFboSource.ShouldContain("private ERenderBufferStorage GetForwardMsaaColorFormat()");
        pipelineFboSource.ShouldContain("=> ERenderBufferStorage.Rgba16f;");

        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("renderBuffer.Type != GetForwardMsaaColorFormat())");

        string advancedPipelineFboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.FBOs.cs").Replace("\r\n", "\n");
        advancedPipelineFboSource.ShouldContain("private ERenderBufferStorage GetForwardMsaaColorFormat()");
        advancedPipelineFboSource.ShouldContain("=> ERenderBufferStorage.Rgba16f;");

        string advancedPipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        advancedPipelineSource.ShouldContain("renderBuffer.Type != GetForwardMsaaColorFormat())");
    }

    [Test]
    public void AntiAliasingInvalidation_ResetsTemporalHistoryState()
    {
        string temporalSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs").Replace("\r\n", "\n");
        temporalSource.ShouldContain("internal static void ResetHistory(XRRenderPipelineInstance? instance)");
        temporalSource.ShouldContain("state.HistoryReady = false;");
        temporalSource.ShouldContain("state.HistoryExposureReady = false;");
        temporalSource.ShouldContain("state.PendingHistoryReady = false;");

        string helperSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/RenderPipelineAntiAliasingResources.cs").Replace("\r\n", "\n");
        helperSource.ShouldContain("internal static void InvalidateAntiAliasingResources(XRRenderPipelineInstance instance, string reason = \"AntiAliasingSettingsChanged\")");
        helperSource.ShouldContain("VPRC_TemporalAccumulationPass.ResetHistory(instance);");
        helperSource.ShouldContain("VPRC_AtmosphereHistoryPass.ResetHistory(instance);");
        helperSource.ShouldContain("VPRC_VolumetricFogHistoryPass.ResetHistory(instance);");
        helperSource.ShouldNotContain("RemoveFrameBufferResource");
        helperSource.ShouldNotContain("RemoveTextureResource");
        helperSource.ShouldNotContain("Dependencies =");

        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("RenderPipelineAntiAliasingResources.InvalidateAntiAliasingResources(instance);");

        string advancedPipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        advancedPipelineSource.ShouldContain("RenderPipelineAntiAliasingResources.InvalidateAntiAliasingResources(instance);");
    }

    [Test]
    public void ProbeSyncCommand_MovesPerFrameWork_OutOfLiveBindPath()
    {
        string syncCommandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_SyncLightProbeResources.cs").Replace("\r\n", "\n");
        syncCommandSource.ShouldContain("public sealed class VPRC_SyncLightProbeResources : ViewportRenderCommand");
        syncCommandSource.ShouldContain("provider.SyncPbrLightingResourcesForFrame();");

        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("public void SyncPbrLightingResourcesForFrame()");
        pipelineSource.ShouldContain("if (_probeBindingStateFrameId != RuntimeEngine.Rendering.State.RenderFrameId)");
        pipelineSource.ShouldNotContain("UpdatePbrLightingResourcesForFrame(");

        string pipelineCommandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        pipelineCommandChainSource.ShouldContain("c.Add<VPRC_SyncLightProbeResources>();");
        pipelineCommandChainSource.ShouldContain("private void AppendLightingPass(ViewportRenderCommandContainer c)");

        string advancedPipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        advancedPipelineSource.ShouldContain("public void SyncPbrLightingResourcesForFrame()");
        advancedPipelineSource.ShouldContain("if (_probeBindingStateFrameId != RuntimeEngine.Rendering.State.RenderFrameId)");
        advancedPipelineSource.ShouldNotContain("UpdatePbrLightingResourcesForFrame(");

    }

    [Test]
    public void V1CommandChain_UsesDedicatedPartial_WithNamedAppendHelpers()
    {
        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldNotContain("GenerateCommandChainLegacy()");
        pipelineSource.ShouldNotContain("CreateFBOTargetCommandsLegacy()");
        pipelineSource.ShouldNotContain("CreateViewportTargetCommandsLegacy()");

        string commandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        commandChainSource.ShouldContain("protected override ViewportRenderCommandContainer GenerateCommandChain()");
        commandChainSource.ShouldContain("private void AppendAmbientOcclusionSwitch(ViewportRenderCommandContainer c, bool enableComputePasses)");
        commandChainSource.ShouldContain("private void AppendLightingPass(ViewportRenderCommandContainer c)");
        commandChainSource.ShouldContain("private void AppendForwardPass(ViewportRenderCommandContainer c, bool enableComputePasses)");
        commandChainSource.ShouldNotContain("VPRC_" + "CacheOrCreate");
    }


    [Test]
    public void ViewportResize_EvictsPostProcessSourceChain_AndRequestsRenderRecheck()
    {
        string instanceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs").Replace("\r\n", "\n");
        instanceSource.ShouldContain("public void ViewportResized(int width, int height, XRViewport? viewport)");
        instanceSource.ShouldContain("_pipeline?.HandleViewportResized(this, width, height, viewport);");
        instanceSource.ShouldContain("public void InternalResolutionResized(int internalWidth, int internalHeight, XRViewport? viewport)");

        string helperSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/RenderPipelineAntiAliasingResources.cs").Replace("\r\n", "\n");
        helperSource.ShouldContain("internal static void InvalidateViewportResizeResources(XRRenderPipelineInstance instance)");
        helperSource.ShouldContain("InvalidateAntiAliasingResources(instance, \"ViewportResized\");");
        helperSource.ShouldNotContain("const string reason = \"ViewportResized\";");
        helperSource.ShouldContain("VPRC_TemporalAccumulationPass.ResetHistory(instance);");

        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("internal override void HandleViewportResized(XRRenderPipelineInstance instance, int width, int height, XRViewport? viewport = null)");
        pipelineSource.ShouldContain("RenderPipelineAntiAliasingResources.InvalidateViewportResizeResources(instance);");
        pipelineSource.ShouldContain("RequestRenderStateRecheck(resetCircuitBreaker: true);");

        string advancedPipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        advancedPipelineSource.ShouldContain("internal override void HandleViewportResized(XRRenderPipelineInstance instance, int width, int height, XRViewport? viewport = null)");
        advancedPipelineSource.ShouldContain("RenderPipelineAntiAliasingResources.InvalidateViewportResizeResources(instance);");
        advancedPipelineSource.ShouldContain("RequestRenderStateRecheck(resetCircuitBreaker: true);");
    }

    [Test]
    public void SurfaceDetailNormalMapping_UsesExplicitModeSelectionOnly()
    {
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Snippets/SurfaceDetailNormalMapping.glsl").Replace("\r\n", "\n");
        shaderSource.ShouldContain("vec3 T = tangentWS - N * dot(N, tangentWS);");
        shaderSource.ShouldContain("if (NormalMapMode == 1)");
        shaderSource.ShouldContain("tangentNormal = XRENGINE_HeightToNormalSobel(uv);");
        shaderSource.ShouldNotContain("grayscaleDelta");
        shaderSource.ShouldNotContain("sampledColor.r - sampledColor.g");
    }

    [Test]
    public void PostProcessOutput_IsMaterialized_BeforeFinalPresentation()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain(".SetTargets(PostProcessFBOName, PostProcessOutputFBOName");
        pipelineSource.ShouldContain("upscaleOutputChoice.FalseCommands = CreateFinalBlitCommands(ResolveStandardFinalOutputFboName(), bypassVendorUpscale);");

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
    public void DefaultPipelineVolumetricFog_CompositesAfterLateForwardWithTemporalProjection()
    {
        string commandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        commandChainSource.ShouldContain("Fog must composite after the late forward batches");
        commandChainSource.IndexOf("AppendMotionBlurAndDoF(fullSceneCommands);", StringComparison.Ordinal)
            .ShouldBeLessThan(commandChainSource.IndexOf("AppendTemporalAccumulation(fullSceneCommands);", StringComparison.Ordinal));
        commandChainSource.IndexOf("AppendTemporalAccumulation(fullSceneCommands);", StringComparison.Ordinal)
            .ShouldBeLessThan(commandChainSource.IndexOf("AppendPostTemporalForwardPasses(fullSceneCommands);", StringComparison.Ordinal));
        commandChainSource.IndexOf("AppendPostTemporalForwardPasses(fullSceneCommands);", StringComparison.Ordinal)
            .ShouldBeLessThan(commandChainSource.IndexOf("AppendVolumetricFog(fullSceneCommands);", StringComparison.Ordinal));
        commandChainSource.IndexOf("AppendVolumetricFog(fullSceneCommands);", StringComparison.Ordinal)
            .ShouldBeLessThan(commandChainSource.IndexOf("AppendFullOverdrawCountingPass(fullSceneCommands);", StringComparison.Ordinal));

        string postProcessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        postProcessSource.ShouldContain("VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData)");
        postProcessSource.ShouldContain("EEngineUniform.ProjMatrix.ToStringFast(), temporalData.CurrProjection");
        postProcessSource.ShouldContain("EEngineUniform.InverseProjMatrix.ToStringFast(), temporalData.CurrInverseProjection");
        postProcessSource.ShouldContain("EEngineUniform.ViewProjectionMatrix.ToStringFast(), temporalData.CurrViewProjection");

        string temporalSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs").Replace("\r\n", "\n");
        temporalSource.ShouldContain("public Matrix4x4 CurrProjection");
        temporalSource.ShouldContain("public Matrix4x4 CurrInverseProjection");
        temporalSource.ShouldContain("state.CurrProjection = jitteredProjection;");
        temporalSource.ShouldContain("state.CurrInverseProjection = camera.InverseProjectionMatrix;");

        string historyPassSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VolumetricFogHistoryPass.cs").Replace("\r\n", "\n");
        historyPassSource.ShouldContain("? temporalData.CurrViewProjection");
        historyPassSource.ShouldNotContain("state.CurrentViewProjection = camera.ViewProjectionMatrixUnjittered;");
    }

    [Test]
    public void AmbientOcclusionModeEvaluation_UsesResolvedCameraFallbacks()
    {
        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("private AmbientOcclusionSettings? ResolveAmbientOcclusionSettings()");
        pipelineSource.ShouldContain("var camera = ResolveCurrentSettingsCamera(currentPipeline);");
        pipelineSource.ShouldContain("AmbientOcclusionSettings? aoSettings = ResolveAmbientOcclusionSettings();");
        pipelineSource.ShouldContain("if (aoSettings is null || !aoSettings.Enabled)");
        pipelineSource.ShouldContain("AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings();");
        pipelineSource.ShouldContain("return settings?.Enabled == true;");
        pipelineSource.ShouldNotContain("var aoStage = State.SceneCamera?.GetPostProcessStageState<AmbientOcclusionSettings>();");

        string advancedPipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        advancedPipelineSource.ShouldContain("private AmbientOcclusionSettings? ResolveAmbientOcclusionSettings()");
        advancedPipelineSource.ShouldContain("var renderState = currentPipeline?.RenderState;");
        advancedPipelineSource.ShouldContain("var camera = renderState?.SceneCamera");
        advancedPipelineSource.ShouldContain("AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings();");
        advancedPipelineSource.ShouldContain("return settings?.Enabled == true;");
        advancedPipelineSource.ShouldNotContain("var aoStage = State.SceneCamera?.GetPostProcessStageState<AmbientOcclusionSettings>();");

    }

    [Test]
    public void AmbientOcclusionNoiseTextures_UseShaderSamplerName()
    {
        string ssaoSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_SSAOPass.cs");
        ssaoSource.ShouldContain("SamplerName = \"AONoiseTexture\"");

        string mvaoSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_MVAOPass.cs");
        mvaoSource.ShouldContain("SamplerName = \"AONoiseTexture\"");
    }

    [Test]
    public void PostAaTextures_UseStableHdrIntermediateFormat()
    {
        string pipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        pipelineSource.ShouldContain("private static EPixelInternalFormat ResolvePostProcessIntermediateInternalFormat()\n        => EPixelInternalFormat.Rgba16f;");
        pipelineSource.ShouldContain("private static bool NeedsRecreatePostProcessTextureInternalSize(XRTexture texture)");
        pipelineSource.ShouldContain("private static bool NeedsRecreatePostProcessTextureFullSize(XRTexture texture)");
        pipelineSource.ShouldContain("private static bool NeedsRecreateFboDueToPostProcessIntermediateFormat(XRFrameBuffer fbo)");
        pipelineSource.ShouldContain("NeedsRecreateFboDueToPostProcessIntermediateFormat(fbo)");

        pipelineSource.ShouldContain("Texture(builder, PostProcessOutputTextureName");
        pipelineSource.ShouldContain("Texture(builder, FxaaOutputTextureName");
        pipelineSource.ShouldContain("Texture(builder, TsrHistoryColorTextureName");

        string texturesSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Textures.cs").Replace("\r\n", "\n");
        texturesSource.ShouldContain("EPixelInternalFormat internalFormat = ResolvePostProcessIntermediateInternalFormat();");
        texturesSource.ShouldContain("EPixelType pixelType = ResolvePostProcessIntermediatePixelType();");
        texturesSource.ShouldContain("ESizedInternalFormat sized = ResolvePostProcessIntermediateSizedInternalFormat();");

        string advancedPipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        advancedPipelineSource.ShouldContain("private static EPixelInternalFormat ResolvePostProcessIntermediateInternalFormat()\n        => EPixelInternalFormat.Rgba16f;");
        advancedPipelineSource.ShouldContain("private static bool NeedsRecreatePostProcessTextureInternalSize(XRTexture texture)");
        advancedPipelineSource.ShouldContain("private static bool NeedsRecreatePostProcessTextureFullSize(XRTexture texture)");
        advancedPipelineSource.ShouldContain("private static bool NeedsRecreateFboDueToPostProcessIntermediateFormat(XRFrameBuffer fbo)");
        advancedPipelineSource.ShouldContain("NeedsRecreateFboDueToPostProcessIntermediateFormat(fbo)");

        string textures2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.Textures.cs").Replace("\r\n", "\n");
        textures2Source.ShouldContain("EPixelInternalFormat internalFormat = ResolvePostProcessIntermediateInternalFormat();");
        textures2Source.ShouldContain("EPixelType pixelType = ResolvePostProcessIntermediatePixelType();");
        textures2Source.ShouldContain("ESizedInternalFormat sized = ResolvePostProcessIntermediateSizedInternalFormat();");
    }

    [Test]
    public void TsrUpscale_UsesColorHistoryWithoutTaaExposureHistory()
    {
        string postProcessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        postProcessSource.ShouldContain("historyReady = temporalData.HistoryReady;");

        string postProcess2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        postProcess2Source.ShouldContain("historyReady = temporalData.HistoryReady;");
    }

    [Test]
    public void TemporalResolves_ApplyProjectionJitterExactlyWhereRequired()
    {
        string temporalSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/TemporalAccumulation.fs").Replace("\r\n", "\n");
        temporalSource.ShouldContain("vec2 historyUV = uv - velocity * 0.5f;");
        temporalSource.ShouldNotContain("historyUV = uv - velocity * 0.5f + (PreviousJitterUv - CurrentJitterUv);");

        string tsrSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolution.fs").Replace("\r\n", "\n");
        tsrSource.ShouldContain("vec2 historyUV = uv - velocity * 0.5f + PreviousJitterUv - CurrentJitterUv;");
        tsrSource.ShouldContain("PreviousJitterUv - CurrentJitterUv");
    }

    [Test]
    public void DeferredMsaaComposite_UsesResolvedLightCombineQuad()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain(".SetTargets(LightCombineFBOName, ForwardPassFBOName)");
        pipelineSource.ShouldNotContain("msaaCmds.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = MsaaLightCombineFBOName;");

    }

    [Test]
    public void VolumetricFog_ScatterCombinesAmbientFillWithDirectionalShadows()
    {
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/VolumetricFog/VolumetricFogScatter.fs").Replace("\r\n", "\n");
        shaderSource.ShouldContain("float interleavedGradientNoise(vec2 pixelCoord)");
        shaderSource.ShouldContain("uniform vec3 GlobalAmbient;");
        shaderSource.ShouldContain("vec3 ambientLighting = GlobalAmbient * 0.35f;");
        shaderSource.ShouldContain("return ambientLighting;");
        shaderSource.ShouldContain("vec3 directLighting = lightColor * shadowFactor * lightContribution * phase * 4.0f;");
        shaderSource.ShouldContain("return ambientLighting + directLighting;");
        shaderSource.ShouldContain("float ComputeNoisyEdgeFade(float distanceToBounds, float edgeFade, float noiseValue, float noiseAmount)");
        shaderSource.ShouldContain("float edgeErosion = fadeDistance * 0.85f * saturate(noiseAmount) * (1.0f - clamp(noiseValue, 0.0f, 1.0f));");
        shaderSource.ShouldContain("const vec3 VolumetricFogNoiseDomainOffset = vec3(17.37f, 41.13f, 29.91f);");
        shaderSource.ShouldContain("+ VolumetricFogNoiseDomainOffset;");
        shaderSource.ShouldContain("float ComputeRayIntervalFade(int index, vec3 rayDirWS, float sampleT, float tNear, float tFar, bool fadeRayEntry, bool fadeRayExit, float noiseValue, float noiseAmount)");
        shaderSource.ShouldContain("float distanceToEntry = fadeRayEntry ? sampleT - tNear : edgeFadeOnRay;");
        shaderSource.ShouldContain("float distanceToExit = fadeRayExit ? tFar - sampleT : edgeFadeOnRay;");
        shaderSource.ShouldContain("bool volumeFadeEntry[MaxVolumetricFogVolumes];");
        shaderSource.ShouldContain("bool volumeFadeExit[MaxVolumetricFogVolumes];");
        shaderSource.ShouldContain("if (VolumetricFogDebugMode == 16)");
        shaderSource.ShouldContain("float normalizedMarchLength = VolumetricFog.MaxDistance > 0.0f");
        shaderSource.ShouldContain("float densityTerms = EvaluateVolumeDensityTerms(volumeIndex, samplePosWS, edgeMask, noiseMask, noiseValue, noiseAmount);");
        shaderSource.ShouldContain("* rayEdgeMask * VolumetricFog.Intensity;");
        shaderSource.ShouldContain("float temporalSeedOffset = fract(RenderTime * 7.0f) * 64.0f * VolumetricFog.JitterStrength;");
        shaderSource.ShouldContain("float t = unionTNear + ign * stepSize;");
        shaderSource.ShouldNotContain("return vec3(1.0f);");
        shaderSource.ShouldNotContain("vec3(1.0f) + lightColor");
    }

    [Test]
    public void VolumetricFog_TemporalAndUpscaleRespectCurrentVolumeMisses()
    {
        string reprojectSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/VolumetricFog/VolumetricFogReproject.fs").Replace("\r\n", "\n");
        reprojectSource.ShouldContain("bool IsNeutralFog(vec4 fog)");
        reprojectSource.ShouldContain("if (IsNeutralFog(currentFog))");
        reprojectSource.ShouldContain("OutColor = currentFog;\n        return;\n    }\n\n    if (!VolumetricFogHistoryReady");

        string upscaleSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/VolumetricFog/VolumetricFogUpscale.fs").Replace("\r\n", "\n");
        upscaleSource.ShouldContain("uniform mat4 VolumetricFogWorldToLocal[MaxVolumetricFogVolumes];");
        upscaleSource.ShouldContain("uniform vec4 VolumetricFogNoiseScaleThreshold[MaxVolumetricFogVolumes];");
        upscaleSource.ShouldContain("const vec3 VolumetricFogNoiseDomainOffset = vec3(17.37f, 41.13f, 29.91f);");
        upscaleSource.ShouldContain("float SampleVolumeNoise01(int index, vec3 localPos, out float noiseAmount)");
        upscaleSource.ShouldContain("float ComputeRayIntervalFade(int index, vec3 rayDirWS, float sampleT, float tNear, float tFar, bool fadeRayEntry, bool fadeRayExit, float noiseValue, float noiseAmount)");
        upscaleSource.ShouldContain("if (!fadeRayEntry && !fadeRayExit)\n        return 1.0f;");
        upscaleSource.ShouldContain("float distanceToExit = fadeRayExit ? tFar - sampleT : edgeFadeOnRay;");
        upscaleSource.ShouldContain("float ViewRayFogFade(float rawDepth, float resolvedDepth, vec2 uv)");
        upscaleSource.ShouldContain("vec4 ApplyFogOutputFade(vec4 fog, float fade)");
        upscaleSource.ShouldContain("if (volumeFade <= 0.0f)");

        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        pipelineSource.ShouldContain("private void VolumetricFogUpscaleFBO_SettingUniforms(XRRenderProgram materialProgram)\n    {\n        VolumetricFog_SetFragmentCameraUniforms(materialProgram);\n\n        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();\n        var volumetricFog = GetSettings<VolumetricFogSettings>(state);\n        (volumetricFog ?? new VolumetricFogSettings()).SetUniforms(materialProgram);\n    }");
    }

    [Test]
    public void VolumetricFog_DisabledPath_UploadsInertShaderState()
    {
        string settingsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Camera/VolumetricFogSettings.cs").Replace("\r\n", "\n");
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
        => SourceContractWorkspace.ReadFile(relativePath);

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            string? migratedRenderingPath = TryResolveMigratedRenderingPath(dir.FullName, relativePath);
            if (migratedRenderingPath is not null)
                return migratedRenderingPath;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }

    private static string? TryResolveMigratedRenderingPath(string repoCandidate, string relativePath)
    {
        const string legacyPrefix = "XRENGINE/Rendering/";
        string migratedRelativePath = relativePath.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase)
            ? "XREngine.Runtime.Rendering/Rendering/" + relativePath[legacyPrefix.Length..]
            : relativePath;

        const string typesPrefix = "Pipelines/Types/";
        int typesIndex = migratedRelativePath.IndexOf(typesPrefix, StringComparison.OrdinalIgnoreCase);
        if (typesIndex >= 0)
        {
            int fileIndex = typesIndex + typesPrefix.Length;
            string fileName = migratedRelativePath[fileIndex..];
            if (fileName.StartsWith("AdvancedRenderPipeline", StringComparison.OrdinalIgnoreCase))
                migratedRelativePath = migratedRelativePath.Insert(fileIndex, "Advanced/");
            else if (fileName.StartsWith("DefaultRenderPipeline", StringComparison.OrdinalIgnoreCase))
                migratedRelativePath = migratedRelativePath.Insert(fileIndex, "Default/");
        }

        string candidate = Path.Combine(repoCandidate, migratedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(candidate) ? candidate : null;
    }
}
