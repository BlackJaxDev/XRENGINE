using Extensions;
using System;
using System.Collections;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Physics.Physx;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline2 : RenderPipeline
{
    public const string SceneShaderPath = "Scene3D";

    private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();
    private readonly FarToNearRenderCommandSorter _farToNearSorter = new();

    //TODO: these options below should not be controlled by this render pipeline object, 
    // but rather in branches in the command chain.

    private readonly Lazy<XRMaterial> _voxelConeTracingVoxelizationMaterial;
    private readonly Lazy<XRMaterial> _motionVectorsMaterial;
    private readonly Lazy<XRMaterial> _depthNormalPrePassMaterial;

    private const float TemporalFeedbackMin = 0.08f;
    private const float TemporalFeedbackMax = 0.94f;
    private const float TemporalVarianceGamma = 1.0f;
    private const float TemporalCatmullRadius = 1.0f;
    private const float TemporalDepthRejectThreshold = 0.0045f;
    private static readonly Vector2 TemporalReactiveTransparencyRange = new(0.4f, 0.85f);
    private const float TemporalReactiveVelocityScale = 0.55f;
    private const float TemporalReactiveLumaThreshold = 0.35f;
    private const float TemporalDepthDiscontinuityScale = 250.0f;
    private const float TemporalConfidencePower = 0.65f;

    private EGlobalIlluminationMode _globalIlluminationMode = EGlobalIlluminationMode.LightProbesAndIbl;
    public EGlobalIlluminationMode GlobalIlluminationMode
    {
        get => _globalIlluminationMode;
        set => SetField(ref _globalIlluminationMode, value);
    }

    public bool UsesRestirGI => _globalIlluminationMode == EGlobalIlluminationMode.Restir;
    public bool UsesVoxelConeTracing => _globalIlluminationMode == EGlobalIlluminationMode.VoxelConeTracing;
    public bool UsesLightVolumes => _globalIlluminationMode == EGlobalIlluminationMode.LightVolumes;
    public bool UsesLightProbeGI => _globalIlluminationMode == EGlobalIlluminationMode.LightProbesAndIbl;
    public bool UsesRadianceCascades => _globalIlluminationMode == EGlobalIlluminationMode.RadianceCascades;
    public bool UsesSurfelGI => _globalIlluminationMode == EGlobalIlluminationMode.SurfelGI;

    // Light probe debug accessors (for editor/state panels)
    public XRTexture2DArray? ProbeIrradianceArray => _probeIrradianceArray;
    public XRTexture2DArray? ProbePrefilterArray => _probePrefilterArray;
    public int ProbeCount => _probePositionBuffer is null ? 0 : (int)_probePositionBuffer.ElementCount;

    protected static bool GPURenderDispatch
        => Engine.Rendering.ResolveGpuRenderDispatchPreference(Engine.EffectiveSettings.GPURenderDispatch);

    private static bool UseVulkanSafeFeatureProfile
        => VulkanFeatureProfile.IsActive;

    private static bool EnableComputeDependentPasses
        => VulkanFeatureProfile.EnableComputeDependentPasses;

    /// <summary>
    /// Resolves the effective HDR output mode for the current rendering camera.
    /// Prefers SceneCamera (the viewport's main camera, unaffected by
    /// <see cref="RenderingState.PushRenderingCamera"/>) so per-camera overrides
    /// survive the null-push inside <see cref="XRQuadFrameBuffer.Render"/>.
    /// Falls back to global engine setting when no camera is available.
    /// </summary>
    internal static bool ResolveOutputHDR()
    {
        var camera = Engine.Rendering.State.RenderingPipelineState?.SceneCamera
                  ?? Engine.Rendering.State.RenderingCamera;
        return camera?.OutputHDROverride ?? Engine.Rendering.Settings.OutputHDR;
    }

    private static EPixelInternalFormat ResolveOutputInternalFormat()
        => ResolveOutputHDR() ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8;

    private static EPixelType ResolveOutputPixelType()
        => ResolveOutputHDR() ? EPixelType.HalfFloat : EPixelType.UnsignedByte;

    private static ESizedInternalFormat ResolveOutputSizedInternalFormat()
        => ResolveOutputHDR() ? ESizedInternalFormat.Rgba16f : ESizedInternalFormat.Rgba8;

    private static ERenderBufferStorage ResolveOutputRenderBufferStorage()
        => ResolveOutputHDR() ? ERenderBufferStorage.Rgba16f : ERenderBufferStorage.Rgba8;

    private static bool NeedsRecreateOutputTextureInternalSize(XRTexture texture)
        => NeedsRecreateTextureInternalSize(texture) || !MatchesOutputTextureFormat(texture);

    private static bool NeedsRecreateOutputTextureFullSize(XRTexture texture)
        => NeedsRecreateTextureFullSize(texture) || !MatchesOutputTextureFormat(texture);

    private static bool MatchesOutputTextureFormat(XRTexture texture)
    {
        ESizedInternalFormat sizedFormat = ResolveOutputSizedInternalFormat();
        EPixelInternalFormat internalFormat = ResolveOutputInternalFormat();
        EPixelType pixelType = ResolveOutputPixelType();

        return texture switch
        {
            XRTexture2D texture2D when texture2D.Mipmaps is { Length: > 0 }
                => texture2D.SizedInternalFormat == sizedFormat
                && texture2D.Mipmaps[0].InternalFormat == internalFormat
                && texture2D.Mipmaps[0].PixelType == pixelType,
            XRTexture2D texture2D
                => texture2D.SizedInternalFormat == sizedFormat,
            _ => false,
        };
    }

    /// <summary>
    /// Returns true when any color attachment on the FBO has a format that no longer
    /// matches the current output HDR mode. Forces FBO recreation so its attachments
    /// and material source textures stay in sync with the freshly-recreated textures.
    /// </summary>
    private static bool NeedsRecreateFboDueToOutputFormat(XRFrameBuffer fbo)
    {
        var targets = fbo.Targets;
        if (targets is null)
            return false;

        for (int i = 0; i < targets.Length; i++)
        {
            var (target, attachment, _, _) = targets[i];
            if (attachment < EFrameBufferAttachment.ColorAttachment0
                || attachment > EFrameBufferAttachment.ColorAttachment7)
                continue;

            if (target is XRTexture tex && !MatchesOutputTextureFormat(tex))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the effective anti-aliasing mode for the current rendering camera.
    /// Prefers SceneCamera so per-camera overrides survive null-push scopes.
    /// </summary>
    private static EAntiAliasingMode ResolveAntiAliasingMode()
    {
        var camera = Engine.Rendering.State.RenderingPipelineState?.SceneCamera
                  ?? Engine.Rendering.State.RenderingCamera;
        return camera?.AntiAliasingModeOverride ?? Engine.EffectiveSettings.AntiAliasingMode;
    }

    internal override float? GetRequestedInternalResolutionForCamera(XRCamera? camera)
    {
        if (Engine.Rendering.Settings.EnableNvidiaDlss || Engine.Rendering.Settings.EnableIntelXess)
            return null;

        EAntiAliasingMode mode = camera?.AntiAliasingModeOverride ?? Engine.EffectiveSettings.AntiAliasingMode;
        return mode == EAntiAliasingMode.Tsr
            ? Math.Clamp(camera?.TsrRenderScaleOverride ?? Engine.Rendering.Settings.TsrRenderScale, 0.5f, 1.0f)
            : null;
    }

    /// <summary>
    /// Resolves the effective MSAA sample count for the current rendering camera.
    /// Prefers SceneCamera so per-camera overrides survive null-push scopes.
    /// </summary>
    internal static uint ResolveEffectiveMsaaSampleCount()
    {
        var camera = Engine.Rendering.State.RenderingPipelineState?.SceneCamera
                  ?? Engine.Rendering.State.RenderingCamera;
        return Math.Max(1u, camera?.MsaaSampleCountOverride ?? Engine.EffectiveSettings.MsaaSampleCount);
    }

    /// <summary>
    /// True when MSAA should be active for the current rendering camera.
    /// Evaluated at render time so per-camera overrides take effect.
    /// </summary>
    private static bool RuntimeEnableMsaa
        => ResolveAntiAliasingMode() == EAntiAliasingMode.Msaa
        && ResolveEffectiveMsaaSampleCount() > 1u;

    /// <summary>
    /// True when FXAA should be active for the current rendering camera.
    /// Evaluated at render time so per-camera overrides take effect.
    /// </summary>
    private static bool RuntimeEnableFxaa
        => ResolveAntiAliasingMode() == EAntiAliasingMode.Fxaa;

    /// <summary>
    /// True when SMAA should be active for the current rendering camera.
    /// Evaluated at render time so per-camera overrides take effect.
    /// </summary>
    private static bool RuntimeEnableSmaa
        => ResolveAntiAliasingMode() == EAntiAliasingMode.Smaa;

    /// <summary>
    /// True when the current camera's AA mode is TSR and internal resolution is
    /// below 100%, meaning a dedicated upscale pass is required.
    /// </summary>
    private static bool RuntimeNeedsTsrUpscale
        => ResolveAntiAliasingMode() == EAntiAliasingMode.Tsr;

    // Build-time checks: used only during command chain generation to decide
    // whether to include FBOs/textures. True if the global setting requests
    // the mode, ensuring resources are available even when no camera is active.
    private bool EnableMsaa
        => Engine.EffectiveSettings.AntiAliasingMode == EAntiAliasingMode.Msaa
        && Engine.EffectiveSettings.MsaaSampleCount > 1u;
    private bool EnableFxaa => Engine.EffectiveSettings.AntiAliasingMode == EAntiAliasingMode.Fxaa;
    private uint MsaaSampleCount => Math.Max(1u, Engine.EffectiveSettings.MsaaSampleCount);

    private bool NeedsRecreateMsaaTextureInternalSize(XRTexture texture)
    {
        if (NeedsRecreateTextureInternalSize(texture))
            return true;

        return texture switch
        {
            XRTexture2D texture2D => texture2D.MultiSampleCount != MsaaSampleCount,
            XRTexture2DArray texture2DArray =>
                !texture2DArray.MultiSample ||
                texture2DArray.Textures.Length == 0 ||
                texture2DArray.Textures[0].MultiSampleCount != MsaaSampleCount,
            _ => true,
        };
    }

    private bool NeedsRecreateTextureView(XRTexture texture, string viewedTextureName)
    {
        XRTexture? viewedTexture = GetTexture<XRTexture>(viewedTextureName);
        return texture switch
        {
            XRTexture2DView texture2DView => viewedTexture is not XRTexture2D expected || !ReferenceEquals(texture2DView.ViewedTexture, expected),
            XRTexture2DArrayView texture2DArrayView => viewedTexture is not XRTexture2DArray expected || !ReferenceEquals(texture2DArrayView.ViewedTexture, expected),
            _ => true,
        };
    }

    private void RetargetTextureView(XRTexture texture, string viewedTextureName)
    {
        XRTexture? viewedTexture = GetTexture<XRTexture>(viewedTextureName);
        switch (texture)
        {
            case XRTexture2DView texture2DView when viewedTexture is XRTexture2D expected && !ReferenceEquals(texture2DView.ViewedTexture, expected):
                texture2DView.ViewedTexture = expected;
                break;
            case XRTexture2DArrayView texture2DArrayView when viewedTexture is XRTexture2DArray expected && !ReferenceEquals(texture2DArrayView.ViewedTexture, expected):
                texture2DArrayView.ViewedTexture = expected;
                break;
        }
    }

    private bool NeedsRecreateMsaaFbo(XRFrameBuffer fbo)
    {
        if (fbo.EffectiveSampleCount != MsaaSampleCount)
            return true;

        if (fbo.Targets is null)
            return false;

        foreach (var (target, attachment, _, _) in fbo.Targets)
        {
            if (attachment == EFrameBufferAttachment.ColorAttachment0
                && target is XRRenderBuffer renderBuffer
                && renderBuffer.Type != ResolveOutputRenderBufferStorage())
                return true;
        }

        return false;
    }

    private bool NeedsRecreateMsaaLightCombineFbo(XRFrameBuffer fbo)
    {
        if (!fbo.IsLastCheckComplete)
            return true;

        if (fbo is not XRQuadFrameBuffer quadFbo || quadFbo.Material is not XRMaterial material)
            return true;

        if (quadFbo.DeriveRenderTargetsFromMaterial)
            return true;

        var textures = material.Textures;
        if (textures.Count != 7)
            return true;

        if (!ReferenceEquals(textures[0], GetTexture<XRTexture>(MsaaAlbedoOpacityTextureName))
            || !ReferenceEquals(textures[1], GetTexture<XRTexture>(MsaaNormalTextureName))
            || !ReferenceEquals(textures[2], GetTexture<XRTexture>(MsaaRMSETextureName))
            || !ReferenceEquals(textures[3], GetTexture<XRTexture>(AmbientOcclusionIntensityTextureName))
            || !ReferenceEquals(textures[4], GetTexture<XRTexture>(MsaaDepthViewTextureName))
            || !ReferenceEquals(textures[5], GetTexture<XRTexture>(MsaaLightingTextureName))
            || !ReferenceEquals(textures[6], GetTexture<XRTexture>(BRDFTextureName)))
            return true;

        var fragmentShaders = material.FragmentShaders;
        if (fragmentShaders.Count != 1)
            return true;

        XRShader baseShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, DeferredLightCombineShaderName()), EShaderType.Fragment);
        XRShader expectedShader = ShaderHelper.CreateDefinedShaderVariant(baseShader, MsaaDeferredDefine) ?? baseShader;
        return !ReferenceEquals(fragmentShaders[0], expectedShader);
    }

    private bool NeedsRecreateDeferredGBufferFbo(XRFrameBuffer fbo)
    {
        if (!fbo.IsLastCheckComplete || fbo.EffectiveSampleCount != 1u)
            return true;

        var targets = fbo.Targets;
        if (targets is null || targets.Length != 5)
            return true;

        XRTexture? albedo = GetTexture<XRTexture>(AlbedoOpacityTextureName);
        XRTexture? normal = GetTexture<XRTexture>(NormalTextureName);
        XRTexture? rmse = GetTexture<XRTexture>(RMSETextureName);
        XRTexture? transformId = GetTexture<XRTexture>(TransformIdTextureName);
        XRTexture? depthStencil = GetTexture<XRTexture>(DepthStencilTextureName);

        return !ReferenceEquals(targets[0].Target, albedo)
            || targets[0].Attachment != EFrameBufferAttachment.ColorAttachment0
            || !ReferenceEquals(targets[1].Target, normal)
            || targets[1].Attachment != EFrameBufferAttachment.ColorAttachment1
            || !ReferenceEquals(targets[2].Target, rmse)
            || targets[2].Attachment != EFrameBufferAttachment.ColorAttachment2
            || !ReferenceEquals(targets[3].Target, transformId)
            || targets[3].Attachment != EFrameBufferAttachment.ColorAttachment3
            || !ReferenceEquals(targets[4].Target, depthStencil)
            || targets[4].Attachment != EFrameBufferAttachment.DepthStencilAttachment;
    }

    /// <summary>
    /// When true the deferred GBuffer renders into an MSAA FBO and deferred lighting
    /// runs with per-sample shading so geometric edges in the deferred path get anti-aliased.
    /// </summary>
    public bool EnableDeferredMsaa { get; set; } = true;

    private string BrightPassShaderName() => 
        Stereo ? "BrightPassStereo.fs" : 
        "BrightPass.fs";

    private string HudFBOShaderName() => 
        Stereo ? "HudFBOStereo.fs" : 
        "HudFBO.fs";

    private string PostProcessShaderName() => 
        Stereo ? "PostProcessStereo.fs" : 
        "PostProcess.fs";

    private string DeferredLightCombineShaderName() => 
        Stereo ? "DeferredLightCombineStereo.fs" : 
        "DeferredLightCombine.fs";

    private string SceneCopyShaderName() =>
        Stereo ? "SceneCopyStereo.fs" : "SceneCopy.fs";

    private string DeferredTransparencyBlurShaderName() =>
        Stereo ? "DeferredTransparencyBlurStereo.fs" : "DeferredTransparencyBlur.fs";

    /// <summary>
    /// Affects how textures and FBOs are created for single-pass stereo rendering.
    /// </summary>
    public bool Stereo { get; }

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => new()
        {
            { (int)EDefaultRenderPass.PreRender, null },
            { (int)EDefaultRenderPass.Background, null },
            { (int)EDefaultRenderPass.OpaqueDeferred, _nearToFarSorter },
            { (int)EDefaultRenderPass.DeferredDecals, _farToNearSorter },
            { (int)EDefaultRenderPass.OpaqueForward, _nearToFarSorter },
            { (int)EDefaultRenderPass.MaskedForward, _nearToFarSorter },
            { (int)EDefaultRenderPass.TransparentForward, _farToNearSorter },
            { (int)EDefaultRenderPass.WeightedBlendedOitForward, null },
            { (int)EDefaultRenderPass.PerPixelLinkedListForward, null },
            { (int)EDefaultRenderPass.DepthPeelingForward, null },
            { (int)EDefaultRenderPass.OnTopForward, null },
            { (int)EDefaultRenderPass.PostRender, null }
        };

    protected override Lazy<XRMaterial> InvalidMaterialFactory => new(MakeInvalidMaterial, LazyThreadSafetyMode.PublicationOnly);

    private XRMaterial MakeInvalidMaterial() =>
        //Debug.Out("Generating invalid material");
        XRMaterial.CreateColorMaterialDeferred();

    //FBOs
    public const string AmbientOcclusionFBOName = "AmbientOcclusionFBO";
    public const string AmbientOcclusionBlurFBOName = "AmbientOcclusionBlurFBO";
    public const string HBAOPlusBlurIntermediateFBOName = "HBAOPlusBlurIntermediateFBO";
    public const string GTAOBlurIntermediateFBOName = "GTAOBlurIntermediateFBO";
    public const string DeferredGBufferFBOName = "DeferredGBufferFBO";
    public const string GBufferFBOName = "GBufferFBO";
    public const string LightCombineFBOName = "LightCombineFBO";
    public const string ForwardPassFBOName = "ForwardPassFBO";
    public const string ForwardPassMsaaFBOName = "ForwardPassMSAAFBO";
    public const string SceneCopyFBOName = "SceneCopyFBO";
    public const string TransparentSceneCopyFBOName = "TransparentSceneCopyFBO";
    public const string DeferredTransparencyBlurFBOName = "DeferredTransparencyBlurFBO";
    public const string TransparentAccumulationFBOName = "TransparentAccumulationFBO";
    public const string TransparentResolveFBOName = "TransparentResolveFBO";
    public const string TransparentAccumulationDebugFBOName = "TransparentAccumulationDebugFBO";
    public const string TransparentRevealageDebugFBOName = "TransparentRevealageDebugFBO";
    public const string TransparentOverdrawDebugFBOName = "TransparentOverdrawDebugFBO";
    public const string PostProcessFBOName = "PostProcessFBO";
    public const string PostProcessOutputTextureName = "PostProcessOutputTexture";
    public const string PostProcessOutputFBOName = "PostProcessOutputFBO";
    public const string FxaaFBOName = "FxaaFBO";
    public const string SmaaFBOName = "SmaaFBO";
    public const string UserInterfaceFBOName = "UserInterfaceFBO";
    public const string TransformIdDebugQuadFBOName = "TransformIdDebugQuadFBO";
    public const string TransformIdDebugOutputTextureName = "TransformIdDebugOutputTexture";
    public const string TransformIdDebugOutputFBOName = "TransformIdDebugOutputFBO";
    public const string RestirCompositeFBOName = "RestirCompositeFBO";
    public const string LightVolumeCompositeFBOName = "LightVolumeCompositeFBO";
    public const string VelocityFBOName = "VelocityFBO";
    public const string HistoryCaptureFBOName = "HistoryCaptureFBO";
    public const string TemporalInputFBOName = "TemporalInputFBO";
    public const string TemporalAccumulationFBOName = "TemporalAccumulationFBO";
    public const string HistoryExposureFBOName = "HistoryExposureFBO";
    public const string MotionBlurCopyFBOName = "MotionBlurCopyFBO";
    public const string MotionBlurFBOName = "MotionBlurFBO";
    public const string DepthOfFieldCopyFBOName = "DepthOfFieldCopyFBO";
    public const string DepthOfFieldFBOName = "DepthOfFieldFBO";
    public const string DepthPreloadFBOName = "DepthPreloadFBO";
    public const string ForwardDepthPrePassFBOName = "ForwardDepthPrePassFBO";
    public const string ForwardDepthPrePassMergeFBOName = "ForwardDepthPrePassMergeFBO";
    public const string FxaaOutputTextureName = "FxaaOutputTexture";
    public const string SmaaOutputTextureName = "SmaaOutputTexture";
    public const string TsrHistoryColorFBOName = "TsrHistoryColorFBO";
    public const string RadianceCascadeCompositeFBOName = "RadianceCascadeCompositeFBO";
    public const string SurfelGICompositeFBOName = "SurfelGICompositeFBO";
    public const string TsrUpscaleFBOName = "TsrUpscaleFBO";

    //Textures
    public const string AmbientOcclusionNoiseTextureName = "AmbientOcclusionNoiseTexture";
    public const string AmbientOcclusionIntensityTextureName = "AmbientOcclusionTexture";
    public const string GTAORawTextureName = "GTAORawTexture";
    public const string GTAOBlurIntermediateTextureName = "GTAOBlurIntermediateTexture";
    public const string HBAOPlusRawTextureName = "HBAOPlusRawTexture";
    public const string HBAOPlusBlurIntermediateTextureName = "HBAOPlusBlurIntermediateTexture";
    public const string NormalTextureName = "Normal";
    public const string ForwardPrePassNormalTextureName = "ForwardPrePassNormal";
    public const string DepthViewTextureName = "DepthView";
    public const string StencilViewTextureName = "StencilView";
    public const string AlbedoOpacityTextureName = "AlbedoOpacity";
    public const string RMSETextureName = "RMSE";
    public const string TransformIdTextureName = "TransformId";
    public const string DepthStencilTextureName = "DepthStencil";
    public const string ForwardPrePassDepthStencilTextureName = "ForwardPrePassDepthStencil";
    public const string ForwardPassMsaaDepthStencilTextureName = "ForwardPassMsaaDepthStencil";
    public const string ForwardPassMsaaDepthViewTextureName = "ForwardPassMsaaDepthView";
    public const string DiffuseTextureName = "LightingTexture";
    public const string HDRSceneTextureName = "HDRSceneTex";
    public const string TransparentSceneCopyTextureName = "TransparentSceneCopyTex";
    public const string TransparentAccumTextureName = "TransparentAccumTex";
    public const string TransparentRevealageTextureName = "TransparentRevealageTex";
    //public const string HDRSceneTexture2Name = "HDRSceneTex2";
    public const string AutoExposureTextureName = "AutoExposureTex";
    public const string BloomBlurTextureName = "BloomBlurTexture";
    public const string UserInterfaceTextureName = "HUDTex";
    public const string BRDFTextureName = "BRDF";
    public const string RestirGITextureName = "RestirGITexture";
    public const string LightVolumeGITextureName = "LightVolumeGITexture";
    public const string VoxelConeTracingVolumeTextureName = "VoxelConeTracingVolume";
    public const string VelocityTextureName = "Velocity";
    public const string HistoryColorTextureName = "HistoryColor";
    public const string HistoryDepthStencilTextureName = "HistoryDepthStencil";
    public const string HistoryDepthViewTextureName = "HistoryDepth";
    public const string TemporalColorInputTextureName = "TemporalColorInput";
    public const string TemporalExposureVarianceTextureName = "TemporalExposureVariance";
    public const string HistoryExposureVarianceTextureName = "HistoryExposureVariance";
    public const string MotionBlurTextureName = "MotionBlur";
    public const string DepthOfFieldTextureName = "DepthOfField";
    public const string TsrHistoryColorTextureName = "TsrHistoryColor";
    public const string RadianceCascadeGITextureName = "RadianceCascadeGI";
    public const string SurfelGITextureName = "SurfelGITexture";

    // MSAA deferred GBuffer texture names
    public const string MsaaAlbedoOpacityTextureName = "MsaaAlbedoOpacity";
    public const string MsaaNormalTextureName = "MsaaNormal";
    public const string MsaaRMSETextureName = "MsaaRMSE";
    public const string MsaaDepthStencilTextureName = "MsaaDepthStencil";
    public const string MsaaDepthViewTextureName = "MsaaDepthView";
    public const string MsaaTransformIdTextureName = "MsaaTransformId";
    public const string MsaaGBufferFBOName = "MsaaGBufferFBO";
    public const string MsaaLightingTextureName = "MsaaLightingTexture";
    public const string MsaaLightingFBOName = "MsaaLightingFBO";
    public const string MsaaLightCombineFBOName = "MsaaLightCombineFBO";
    public const string MsaaDeferredResolveAlbedoFBOName = "MsaaDeferredResolveAlbedoFBO";
    public const string MsaaDeferredResolveNormalFBOName = "MsaaDeferredResolveNormalFBO";
    public const string MsaaDeferredResolveRmseFBOName = "MsaaDeferredResolveRmseFBO";
    private const string MsaaDeferredDefine = "XRENGINE_MSAA_DEFERRED";
    internal const string ProbeDebugFallbackDefine = "XRENGINE_PROBE_DEBUG_FALLBACK";

    /// <summary>
    /// True when the current camera uses MSAA and the deferred pipeline should run in MSAA mode.
    /// </summary>
    internal static bool RuntimeEnableMsaaDeferred
        => RuntimeEnableMsaa
        && (Engine.Rendering.State.CurrentRenderingPipeline?.Pipeline as DefaultRenderPipeline2)?.EnableDeferredMsaa == true;

    private const string TonemappingStageKey = "tonemapping";
    private const string ColorGradingStageKey = "colorGrading";
    private const string BloomStageKey = "bloom";
    private const string AmbientOcclusionStageKey = "ambientOcclusion";
    private const int AmbientOcclusionDisabledMode = -1;
    private const string TemporalAntiAliasingStageKey = "temporalAntiAliasing";
    private const string MotionBlurStageKey = "motionBlur";
    private const string DepthOfFieldStageKey = "depthOfField";
    private const string LensDistortionStageKey = "lensDistortion";
    private const string ChromaticAberrationStageKey = "chromaticAberration";
    private const string FogStageKey = "fog";

    private static readonly string[] AntiAliasingTextureDependencies =
    [
        PostProcessOutputTextureName,
        FxaaOutputTextureName,
        SmaaOutputTextureName,
        HistoryColorTextureName,
        HistoryDepthStencilTextureName,
        HistoryDepthViewTextureName,
        TemporalColorInputTextureName,
        TemporalExposureVarianceTextureName,
        HistoryExposureVarianceTextureName,
        TsrHistoryColorTextureName,
        MsaaAlbedoOpacityTextureName,
        MsaaNormalTextureName,
        MsaaRMSETextureName,
        MsaaDepthStencilTextureName,
        MsaaDepthViewTextureName,
        MsaaTransformIdTextureName,
        MsaaLightingTextureName,
        ForwardPassMsaaDepthStencilTextureName,
        ForwardPassMsaaDepthViewTextureName,
    ];

    private static readonly string[] AntiAliasingFrameBufferDependencies =
    [
        // AmbientOcclusionFBO is managed by AO passes (not CacheOrCreateFBO),
        // so it must not be destroyed here — the AO pass owns its lifecycle.
        LightCombineFBOName,
        ForwardPassFBOName,
        PostProcessOutputFBOName,
        PostProcessFBOName,
        FxaaFBOName,
        SmaaFBOName,
        TsrHistoryColorFBOName,
        TsrUpscaleFBOName,
        HistoryCaptureFBOName,
        TemporalInputFBOName,
        TemporalAccumulationFBOName,
        HistoryExposureFBOName,
        DepthPreloadFBOName,
        ForwardPassMsaaFBOName,
        SceneCopyFBOName,
        TransparentSceneCopyFBOName,
        DeferredTransparencyBlurFBOName,
        TransparentAccumulationFBOName,
        TransparentResolveFBOName,
        VelocityFBOName,
        DeferredGBufferFBOName,
        MsaaGBufferFBOName,
        MsaaLightingFBOName,
        MsaaLightCombineFBOName,
        MsaaDeferredResolveAlbedoFBOName,
        MsaaDeferredResolveNormalFBOName,
        MsaaDeferredResolveRmseFBOName,
    ];

    public DefaultRenderPipeline2() : this(false)
    {
    }

    public DefaultRenderPipeline2(bool stereo = false) : base(true)
    {
        Stereo = stereo;
        GlobalIlluminationMode = Engine.UserSettings.GlobalIlluminationMode;
        _voxelConeTracingVoxelizationMaterial = new Lazy<XRMaterial>(CreateVoxelConeTracingVoxelizationMaterial, LazyThreadSafetyMode.PublicationOnly);
        _motionVectorsMaterial = new Lazy<XRMaterial>(CreateMotionVectorsMaterial, LazyThreadSafetyMode.PublicationOnly);
        _depthNormalPrePassMaterial = new Lazy<XRMaterial>(CreateDepthNormalPrePassMaterial, LazyThreadSafetyMode.PublicationOnly);
        Engine.Rendering.SettingsChanged += HandleRenderingSettingsChanged;
        Engine.Rendering.AntiAliasingSettingsChanged += HandleAntiAliasingSettingsChanged;
        ApplyAntiAliasingResolutionHint();
        CommandChain = GenerateCommandChain();
    }

    private bool EnableTransformIdVisualization
        => !Stereo && Engine.EditorPreferences.Debug.VisualizeTransformId;

    private bool EnableTransparencyAccumulationVisualization
        => !Stereo && Engine.EditorPreferences.Debug.VisualizeTransparencyAccumulation;

    private bool EnableTransparencyRevealageVisualization
        => !Stereo && Engine.EditorPreferences.Debug.VisualizeTransparencyRevealage;

    private bool EnableTransparencyOverdrawVisualization
        => !Stereo && Engine.EditorPreferences.Debug.VisualizeTransparencyOverdrawHeatmap;

    private bool EnablePerPixelLinkedListVisualization
        => !Stereo && Engine.EditorPreferences.Debug.VisualizePerPixelLinkedListFragments;

    private bool EnableDepthPeelingLayerVisualization
        => !Stereo && Engine.EditorPreferences.Debug.VisualizeDepthPeelingLayer;

    private string? ActiveTransparencyDebugFboName
        => EnableTransparencyAccumulationVisualization
            ? TransparentAccumulationDebugFBOName
            : EnableTransparencyRevealageVisualization
                ? TransparentRevealageDebugFBOName
                : EnableTransparencyOverdrawVisualization
                    ? TransparentOverdrawDebugFBOName
                    : EnablePerPixelLinkedListVisualization
                        ? PpllFragmentCountDebugFBOName
                        : EnableDepthPeelingLayerVisualization
                            ? DepthPeelingDebugFBOName
                    : null;

    private void HandleRenderingSettingsChanged()
    {
        Engine.InvokeOnMainThread(() =>
        {
            ApplyAntiAliasingResolutionHint();
            CommandChain = GenerateCommandChain();
            foreach (var instance in Instances)
                instance.DestroyCache();
        }, "DefaultRenderPipeline2: Rendering settings changed", true);
    }

    private void HandleAntiAliasingSettingsChanged()
    {
        Engine.InvokeOnMainThread(() =>
        {
            ApplyAntiAliasingResolutionHint();

            foreach (var instance in Instances)
                InvalidateAntiAliasingResources(instance);

            foreach (var window in Engine.Windows)
            {
                window.InvalidateScenePanelResources();
                window.RequestRenderStateRecheck(resetCircuitBreaker: true);
            }
        }, "DefaultRenderPipeline2: AA settings changed", true);
    }

    private static void InvalidateAntiAliasingResources(XRRenderPipelineInstance instance)
    {
        foreach (string name in AntiAliasingFrameBufferDependencies)
            instance.Resources.RemoveFrameBuffer(name);

        foreach (string name in AntiAliasingTextureDependencies)
            instance.Resources.RemoveTexture(name);
    }

    private void ApplyAntiAliasingResolutionHint()
    {
        // Avoid fighting other upscalers when DLSS or XeSS is enabled.
        if (Engine.Rendering.Settings.EnableNvidiaDlss || Engine.Rendering.Settings.EnableIntelXess)
        {
            RequestedInternalResolution = null;
            return;
        }

        if (Engine.EffectiveSettings.AntiAliasingMode == EAntiAliasingMode.Tsr)
        {
            RequestedInternalResolution = Math.Clamp(Engine.Rendering.Settings.TsrRenderScale, 0.5f, 1.0f);
        }
        else
        {
            // Null means "use viewport default".
            RequestedInternalResolution = null;
        }
    }

    internal XRMaterial GetVoxelConeTracingVoxelizationMaterial()
        => _voxelConeTracingVoxelizationMaterial.Value;

    internal XRMaterial GetMotionVectorsMaterial()
        => _motionVectorsMaterial.Value;

    internal XRMaterial GetDepthNormalPrePassMaterial()
        => _depthNormalPrePassMaterial.Value;


    protected override void DescribeRenderPasses(RenderPassMetadataCollection metadata)
    {
        base.DescribeRenderPasses(metadata);

        static void Chain(RenderPassMetadataCollection collection, EDefaultRenderPass pass, params EDefaultRenderPass[] dependencies)
        {
            var builder = collection.ForPass((int)pass, pass.ToString(), ERenderGraphPassStage.Graphics);
            foreach (var dep in dependencies)
                builder.DependsOn((int)dep);
        }

        Chain(metadata, EDefaultRenderPass.PreRender);
        Chain(metadata, EDefaultRenderPass.Background, EDefaultRenderPass.PreRender, EDefaultRenderPass.DeferredDecals);
        Chain(metadata, EDefaultRenderPass.OpaqueDeferred, EDefaultRenderPass.PreRender);
        Chain(metadata, EDefaultRenderPass.DeferredDecals, EDefaultRenderPass.OpaqueDeferred);
        Chain(metadata, EDefaultRenderPass.OpaqueForward, EDefaultRenderPass.Background);
        Chain(metadata, EDefaultRenderPass.MaskedForward, EDefaultRenderPass.OpaqueForward);
        Chain(metadata, EDefaultRenderPass.WeightedBlendedOitForward, EDefaultRenderPass.MaskedForward);
        Chain(metadata, EDefaultRenderPass.PerPixelLinkedListForward, EDefaultRenderPass.WeightedBlendedOitForward);
        Chain(metadata, EDefaultRenderPass.DepthPeelingForward, EDefaultRenderPass.PerPixelLinkedListForward);
        Chain(metadata, EDefaultRenderPass.TransparentForward, EDefaultRenderPass.DepthPeelingForward);
        Chain(metadata, EDefaultRenderPass.OnTopForward, EDefaultRenderPass.TransparentForward);
        Chain(metadata, EDefaultRenderPass.PostRender, EDefaultRenderPass.OnTopForward);
    }


    #region Setting Uniforms

    private XRTexture2DArray? _probeIrradianceArray;
    private XRTexture2DArray? _probePrefilterArray;
    private XRDataBuffer? _probePositionBuffer;
    private XRDataBuffer? _probeTetraBuffer;
    private XRDataBuffer? _probeParamBuffer;
    private XRDataBuffer? _probeGridCellBuffer;
    private XRDataBuffer? _probeGridIndexBuffer;
    private Vector3 _probeGridOrigin;
    private float _probeGridCellSize;
    private IVector3 _probeGridDims;
    private bool _useProbeGridAcceleration = true;
    private int _lastProbeCount = 0;
    private readonly Dictionary<Guid, Vector3> _cachedProbePositions = new();
    private readonly Dictionary<Guid, (XRTexture2D Irradiance, XRTexture2D Prefilter)> _cachedProbeTextures = new();
    private readonly Dictionary<Guid, uint> _cachedProbeCaptureVersions = new();
    private volatile bool _pendingProbeRefresh;
    private Job? _probeTessellationJob;
    private volatile int _probeTessellationGeneration;
    private int _probeTetraProbeCount;

    public bool UseProbeGridAcceleration
    {
        get => _useProbeGridAcceleration;
        set => SetField(ref _useProbeGridAcceleration, value);
    }

    internal struct ProbePositionData
    {
        public Vector4 Position;
    }

    private struct ProbeParamData
    {
        public Vector4 InfluenceInner;       // xyz inner extents or inner radius
        public Vector4 InfluenceOuter;       // xyz outer extents or outer radius
        public Vector4 InfluenceOffsetShape; // xyz offset, w shape (0 sphere, 1 box)
        public Vector4 ProxyCenterEnable;    // xyz center offset, w enable (1/0)
        public Vector4 ProxyHalfExtents;     // xyz half extents, w normalization scale
        public Vector4 ProxyRotation;        // xyzw quaternion
    }

    private struct ProbeGridCell
    {
        public IVector4 OffsetCount;
        public IVector4 FallbackIndices;
    }

    private struct ProbeTetraData
    {
        public Vector4 Indices;
    }

    private void LightCombineFBO_SettingUniforms(XRRenderProgram program)
    {
        bool useAo = ShouldUseAmbientOcclusion();
        program.Uniform("UseAmbientOcclusion", useAo);

        float aoPower = 1.0f;
        bool multiBounce = false;
        bool specularOcclusion = false;

        AmbientOcclusionSettings? aoSettings = ResolveAmbientOcclusionSettings();
        if (aoSettings is not null)
        {
            aoPower = aoSettings.Power;
            if (AmbientOcclusionSettings.NormalizeType(aoSettings.Type) == AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion)
            {
                multiBounce = aoSettings.GroundTruth.MultiBounceEnabled;
                specularOcclusion = aoSettings.GroundTruth.SpecularOcclusionEnabled;
            }
        }

        program.Uniform("AmbientOcclusionPower", aoPower);
        program.Uniform("AmbientOcclusionMultiBounce", multiBounce);
        program.Uniform("SpecularOcclusionEnabled", specularOcclusion);

        BindPbrLightingResources(program);
    }

    public bool BindPbrLightingResources(XRRenderProgram program)
    {
        void SuppressOptionalProbeSamplers()
        {
            program.SuppressFallbackSamplerWarning("IrradianceArray");
            program.SuppressFallbackSamplerWarning("PrefilterArray");
        }

        XRTexture? brdfTexture = GetTexture<XRTexture>(BRDFTextureName);
        if (brdfTexture is not null)
            program.Sampler("BRDF", brdfTexture, 6);

        if (!UsesLightProbeGI)
        {
            SuppressOptionalProbeSamplers();
            program.Uniform("ForwardPbrResourcesEnabled", false);
            program.Uniform("ProbeCount", 0);
            program.Uniform("TetraCount", 0);
            program.Uniform("UseProbeGrid", false);
            return false;
        }

        var world = RenderingWorld;
        if (world is null)
        {
            SuppressOptionalProbeSamplers();
            program.Uniform("ForwardPbrResourcesEnabled", false);
            program.Uniform("ProbeCount", 0);
            program.Uniform("TetraCount", 0);
            program.Uniform("UseProbeGrid", false);
            return false;
        }

        IReadOnlyList<LightProbeComponent> probes = world.Lights.LightProbes;
        var readyProbes = GetReadyProbes(probes);
        if (readyProbes.Count == 0)
        {
            ClearProbeResources();
            SuppressOptionalProbeSamplers();
            program.Uniform("ForwardPbrResourcesEnabled", false);
            program.Uniform("ProbeCount", 0);
            program.Uniform("TetraCount", 0);
            program.Uniform("UseProbeGrid", false);
            return false;
        }

        if (_pendingProbeRefresh || ProbeConfigurationChanged(readyProbes))
            BuildProbeResources(readyProbes);

        bool enabled = brdfTexture is not null
            && _probeIrradianceArray is not null
            && _probePrefilterArray is not null
            && _probePositionBuffer is not null
            && _probeParamBuffer is not null;

        program.Uniform("ForwardPbrResourcesEnabled", enabled);
        if (!enabled)
        {
            SuppressOptionalProbeSamplers();
            program.Uniform("ProbeCount", 0);
            program.Uniform("TetraCount", 0);
            program.Uniform("UseProbeGrid", false);
            return false;
        }

        program.Sampler("IrradianceArray", _probeIrradianceArray!, 7);
        program.Sampler("PrefilterArray", _probePrefilterArray!, 8);

        int probeCount = (int)_probePositionBuffer!.ElementCount;
        program.Uniform("ProbeCount", probeCount);
        _probePositionBuffer.BindTo(program, 0);
        _probeParamBuffer!.BindTo(program, 2);
        bool useProbeGrid = _useProbeGridAcceleration && _probeGridCellBuffer is not null && _probeGridIndexBuffer is not null;
        program.Uniform("UseProbeGrid", useProbeGrid);

        if (useProbeGrid)
        {
            _probeGridCellBuffer!.BindTo(program, 3);
            _probeGridIndexBuffer!.BindTo(program, 4);
            program.Uniform("ProbeGridOrigin", _probeGridOrigin);
            program.Uniform("ProbeGridCellSize", _probeGridCellSize);
            program.Uniform("ProbeGridDims", _probeGridDims);
        }

        int tetraCount = _probeTetraBuffer != null && _probeTetraProbeCount == readyProbes.Count
            ? (int)_probeTetraBuffer.ElementCount
            : 0;
        program.Uniform("TetraCount", tetraCount);
        if (tetraCount > 0)
        {
            _probeTetraBuffer!.BindTo(program, 1);

            if (Engine.EditorPreferences.Debug.RenderLightProbeTetrahedra)
                RenderProbeTetrahedra(readyProbes, tetraCount);
        }

        return true;
    }

    private bool ShouldUseAmbientOcclusion()
    {
        AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings();
        return settings?.Enabled == true;
    }

    private AmbientOcclusionSettings? ResolveAmbientOcclusionSettings()
    {
        var camera = State.SceneCamera
            ?? State.RenderingCamera
            ?? CurrentRenderingPipeline?.LastSceneCamera
            ?? CurrentRenderingPipeline?.LastRenderingCamera;

        if (camera is null)
            return null;

        var stage = camera.GetPostProcessStageState<AmbientOcclusionSettings>();
        if (stage is null)
            return null;

        if (!stage.TryGetBacking(out AmbientOcclusionSettings? settings))
            return null;

        return settings;
    }

    private void RenderProbeTetrahedra(List<LightProbeComponent> readyProbes, int tetraCount)
    {
        for (uint i = 0; i < tetraCount; ++i)
        {
            var tetraData = _probeTetraBuffer!.GetDataRawAtIndex<ProbeTetraData>(i);
            var indices = tetraData.Indices;
            int index0 = (int)indices.X;
            int index1 = (int)indices.Y;
            int index2 = (int)indices.Z;
            int index3 = (int)indices.W;
            int probeCount = readyProbes.Count;

            if ((uint)index0 >= probeCount ||
                (uint)index1 >= probeCount ||
                (uint)index2 >= probeCount ||
                (uint)index3 >= probeCount)
            {
                Debug.LogWarning($"Skipping stale probe tetrahedron {i}: indices=({index0}, {index1}, {index2}, {index3}) probeCount={probeCount}.");
                continue;
            }

            Vector3 p0 = readyProbes[index0].Transform.RenderTranslation;
            Vector3 p1 = readyProbes[index1].Transform.RenderTranslation;
            Vector3 p2 = readyProbes[index2].Transform.RenderTranslation;
            Vector3 p3 = readyProbes[index3].Transform.RenderTranslation;
            Engine.Rendering.Debug.RenderLine(p0, p1, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderLine(p0, p2, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderLine(p0, p3, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderLine(p1, p2, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderLine(p1, p3, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderLine(p2, p3, ColorF4.Cyan);
        }
    }

    private void BuildProbeGrid(List<ProbePositionData> positions)
    {
        _probeGridCellBuffer?.Dispose();
        _probeGridCellBuffer = null;
        _probeGridIndexBuffer?.Dispose();
        _probeGridIndexBuffer = null;
        _probeGridOrigin = Vector3.Zero;
        _probeGridCellSize = 0f;
        _probeGridDims = IVector3.Zero;

        if (positions.Count == 0)
            return;

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p.Position.XYZ());
            max = Vector3.Max(max, p.Position.XYZ());
        }

        Vector3 extents = max - min;
        float maxExtent = Math.Max(extents.X, Math.Max(extents.Y, extents.Z));
        if (maxExtent <= 0.0001f)
            maxExtent = 1.0f;

        const int targetCellsPerAxis = 16;
        _probeGridCellSize = maxExtent / targetCellsPerAxis;
        _probeGridOrigin = min;
        Vector3 dimsF = extents / _probeGridCellSize + Vector3.One;
        IVector3 dimsI = new(
            Math.Max(1, (int)Math.Ceiling(dimsF.X)),
            Math.Max(1, (int)Math.Ceiling(dimsF.Y)),
            Math.Max(1, (int)Math.Ceiling(dimsF.Z)));
        dimsI = IVector3.Min(dimsI, new IVector3(64, 64, 64));
        _probeGridDims = dimsI;

        int cellCount = dimsI.X * dimsI.Y * dimsI.Z;
        var cellLists = new List<int>[cellCount];
        for (int i = 0; i < cellCount; ++i)
            cellLists[i] = new List<int>(4);

        for (int i = 0; i < positions.Count; ++i)
        {
            Vector4 pos4 = positions[i].Position;
            Vector3 rel = (new Vector3(pos4.X, pos4.Y, pos4.Z) - _probeGridOrigin) / _probeGridCellSize;
            IVector3 cell = new(
                Math.Clamp((int)MathF.Floor(rel.X), 0, dimsI.X - 1),
                Math.Clamp((int)MathF.Floor(rel.Y), 0, dimsI.Y - 1),
                Math.Clamp((int)MathF.Floor(rel.Z), 0, dimsI.Z - 1));
            int flat = cell.X + cell.Y * dimsI.X + cell.Z * dimsI.X * dimsI.Y;
            cellLists[flat].Add(i);
        }

        var offsets = new List<ProbeGridCell>(cellCount);
        var indices = new List<int>();
        for (int c = 0; c < cellCount; ++c)
        {
            var list = cellLists[c];
            int offset = indices.Count;
            indices.AddRange(list);

            int cellX = c % dimsI.X;
            int cellY = (c / dimsI.X) % dimsI.Y;
            int cellZ = c / (dimsI.X * dimsI.Y);
            Vector3 cellCenter = _probeGridOrigin + new Vector3(cellX + 0.5f, cellY + 0.5f, cellZ + 0.5f) * _probeGridCellSize;
            IVector4 fallbackIndices = ComputeProbeGridFallbackIndices(cellCenter, positions, list.Count > 0 ? list : null);

            offsets.Add(new ProbeGridCell
            {
                OffsetCount = new IVector4(offset, list.Count, 0, 0),
                FallbackIndices = fallbackIndices,
            });
        }

        _probeGridCellBuffer = new XRDataBuffer("LightProbeGridCells", EBufferTarget.ShaderStorageBuffer, (uint)offsets.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeGridCell>(), false, false)
        {
            BindingIndexOverride = 3,
        };
        _probeGridCellBuffer.SetDataRaw(offsets);
        _probeGridCellBuffer.PushData();

        _probeGridIndexBuffer = new XRDataBuffer("LightProbeGridIndices", EBufferTarget.ShaderStorageBuffer, (uint)indices.Count, EComponentType.Int, sizeof(int), false, false)
        {
            BindingIndexOverride = 4,
        };
        _probeGridIndexBuffer.SetDataRaw(indices);
        _probeGridIndexBuffer.PushData();
    }

    internal static IVector4 ComputeProbeGridFallbackIndices(Vector3 cellCenter, IReadOnlyList<ProbePositionData> positions, List<int>? preferredIndices)
    {
        Span<float> bestDistances = stackalloc float[4] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
        Span<int> bestIndices = stackalloc int[4] { -1, -1, -1, -1 };

        if (preferredIndices is not null && preferredIndices.Count > 0)
        {
            foreach (int probeIndex in preferredIndices)
                ConsiderProbe(probeIndex, cellCenter, positions, bestDistances, bestIndices);
        }
        else
        {
            for (int probeIndex = 0; probeIndex < positions.Count; ++probeIndex)
                ConsiderProbe(probeIndex, cellCenter, positions, bestDistances, bestIndices);
        }

        return new IVector4(bestIndices[0], bestIndices[1], bestIndices[2], bestIndices[3]);
    }

    private static void ConsiderProbe(int probeIndex, Vector3 cellCenter, IReadOnlyList<ProbePositionData> positions, Span<float> bestDistances, Span<int> bestIndices)
    {
        if ((uint)probeIndex >= positions.Count)
            return;

        for (int existing = 0; existing < 4; ++existing)
        {
            if (bestIndices[existing] == probeIndex)
                return;
        }

        Vector4 pos4 = positions[probeIndex].Position;
        float distance = Vector3.Distance(cellCenter, new Vector3(pos4.X, pos4.Y, pos4.Z));
        for (int slot = 0; slot < 4; ++slot)
        {
            if (distance >= bestDistances[slot])
                continue;

            for (int shift = 3; shift > slot; --shift)
            {
                bestDistances[shift] = bestDistances[shift - 1];
                bestIndices[shift] = bestIndices[shift - 1];
            }

            bestDistances[slot] = distance;
            bestIndices[slot] = probeIndex;
            break;
        }
    }

    private static List<LightProbeComponent> GetReadyProbes(IReadOnlyList<LightProbeComponent> probes)
    {
        var readyProbes = new List<LightProbeComponent>(probes.Count);
        foreach (var probe in probes)
        {
            if (probe.IrradianceTexture != null && probe.PrefilterTexture != null)
                readyProbes.Add(probe);
        }

        return readyProbes;
    }

    private bool ProbeConfigurationChanged(IReadOnlyList<LightProbeComponent> readyProbes)
    {
        if (_lastProbeCount != readyProbes.Count)
        {
            _pendingProbeRefresh = true;
            return true;
        }

        if (_cachedProbePositions.Count != readyProbes.Count || _cachedProbeTextures.Count != readyProbes.Count)
        {
            _pendingProbeRefresh = true;
            return true;
        }

        foreach (var probe in readyProbes)
        {
            var position = probe.Transform.RenderTranslation;
            if (!_cachedProbePositions.TryGetValue(probe.ID, out var cachedPos) || cachedPos != position)
            {
                _pendingProbeRefresh = true;
                return true;
            }

            if (!_cachedProbeTextures.TryGetValue(probe.ID, out var cachedTex)
                || cachedTex.Irradiance != probe.IrradianceTexture
                || cachedTex.Prefilter != probe.PrefilterTexture)
            {
                _pendingProbeRefresh = true;
                return true;
            }

            if (!_cachedProbeCaptureVersions.TryGetValue(probe.ID, out var cachedVersion)
                || cachedVersion != probe.CaptureVersion)
            {
                _pendingProbeRefresh = true;
                return true;
            }
        }

        return false;
    }

    private void ClearProbeResources()
    {
        _probeIrradianceArray?.Destroy();
        _probeIrradianceArray = null;
        _probePrefilterArray?.Destroy();
        _probePrefilterArray = null;
        _probePositionBuffer?.Dispose();
        _probePositionBuffer = null;
        _probeParamBuffer?.Dispose();
        _probeParamBuffer = null;
        _probeTetraBuffer?.Dispose();
        _probeTetraBuffer = null;
        _probeGridCellBuffer?.Dispose();
        _probeGridCellBuffer = null;
        _probeGridIndexBuffer?.Dispose();
        _probeGridIndexBuffer = null;
        _probeGridOrigin = Vector3.Zero;
        _probeGridCellSize = 0f;
        _probeGridDims = IVector3.Zero;
        _probeTessellationJob?.Cancel();
        _probeTessellationJob = null;
        unchecked { _probeTessellationGeneration++; }
        _probeTetraProbeCount = 0;
        _cachedProbePositions.Clear();
        _cachedProbeTextures.Clear();
        _cachedProbeCaptureVersions.Clear();
        _lastProbeCount = 0;
        _pendingProbeRefresh = false;
    }

    private void BuildProbeResources(IList<LightProbeComponent> readyProbes)
    {
        ClearProbeResources();

        if (readyProbes.Count == 0)
        {
            _pendingProbeRefresh = false;
            return;
        }

        var irrTextures = new List<XRTexture2D>(readyProbes.Count);
        var preTextures = new List<XRTexture2D>(readyProbes.Count);
        var positions = new List<ProbePositionData>(readyProbes.Count);
        var parameters = new List<ProbeParamData>(readyProbes.Count);

        foreach (var probe in readyProbes)
        {
            irrTextures.Add(probe.IrradianceTexture!);
            preTextures.Add(probe.PrefilterTexture!);

            var position = probe.Transform.RenderTranslation;
            positions.Add(new ProbePositionData { Position = new Vector4(position, 1.0f) });

            parameters.Add(new ProbeParamData
            {
                InfluenceInner = new Vector4(probe.InfluenceBoxInnerExtents, probe.InfluenceSphereInnerRadius),
                InfluenceOuter = new Vector4(probe.InfluenceBoxOuterExtents, probe.InfluenceSphereOuterRadius),
                InfluenceOffsetShape = new Vector4(probe.InfluenceOffset, probe.InfluenceShape == LightProbeComponent.EInfluenceShape.Box ? 1.0f : 0.0f),
                ProxyCenterEnable = new Vector4(probe.ProxyBoxCenterOffset, probe.ParallaxCorrectionEnabled ? 1.0f : 0.0f),
                ProxyHalfExtents = new Vector4(probe.ProxyBoxHalfExtents, probe.NormalizationScale),
                ProxyRotation = new Vector4(probe.ProxyBoxRotation.X, probe.ProxyBoxRotation.Y, probe.ProxyBoxRotation.Z, probe.ProxyBoxRotation.W),
            });
            _cachedProbePositions[probe.ID] = position;
            _cachedProbeTextures[probe.ID] = (probe.IrradianceTexture!, probe.PrefilterTexture!);
            _cachedProbeCaptureVersions[probe.ID] = probe.CaptureVersion;
        }

        if (irrTextures.Count == 0 || preTextures.Count == 0)
            return;

        _probeIrradianceArray = new XRTexture2DArray([.. irrTextures])
        {
            Name = "LightProbeIrradianceArray",
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            SizedInternalFormat = ESizedInternalFormat.Rgb8,  // Match irradiance texture format
        };

        _probePrefilterArray = new XRTexture2DArray([.. preTextures])
        {
            Name = "LightProbePrefilterArray",
            MinFilter = ETexMinFilter.LinearMipmapLinear,
            MagFilter = ETexMagFilter.Linear,
            SizedInternalFormat = ESizedInternalFormat.Rgb16f,  // Match prefilter texture format
        };

        _probePositionBuffer = new XRDataBuffer("LightProbePositions", EBufferTarget.ShaderStorageBuffer, (uint)positions.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbePositionData>(), false, false)
        {
            BindingIndexOverride = 0,
        };
        _probePositionBuffer.SetDataRaw<ProbePositionData>(positions);
        _probePositionBuffer.PushData();

        _probeParamBuffer = new XRDataBuffer("LightProbeParameters", EBufferTarget.ShaderStorageBuffer, (uint)parameters.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeParamData>(), false, false)
        {
            BindingIndexOverride = 2,
        };
        _probeParamBuffer.SetDataRaw<ProbeParamData>(parameters);
        _probeParamBuffer.PushData();

        if (_useProbeGridAcceleration)
            BuildProbeGrid(positions);

        _lastProbeCount = positions.Count;
        _pendingProbeRefresh = false;

        StartTetrahedralizationJob(readyProbes);
    }

    private void StartTetrahedralizationJob(IList<LightProbeComponent> probes)
    {
        _probeTessellationJob?.Cancel();
        int generation = _probeTessellationGeneration;
        int probeCount = probes.Count;
        _probeTessellationJob = Engine.Jobs.Schedule(() => RunTetrahedralization(probes, generation, probeCount));
    }

    private IEnumerable RunTetrahedralization(IList<LightProbeComponent> probes, int generation, int probeCount)
    {
        var probeIndices = new Dictionary<LightProbeComponent, int>(probes.Count);
        for (int i = 0; i < probes.Count; ++i)
            probeIndices[probes[i]] = i;

        // If we don't have enough probes for a tetrahedralization, create a minimal fallback so shaders still have data.
        if (probes.Count is > 0 and < 5)
        {
            UploadTetrahedralization(BuildFallbackTetraData(probeIndices), generation, probeCount);
            yield break;
        }

        if (!Lights3DCollection.TryCreateDelaunay(probes, out var triangulation))
        {
            Debug.LogWarning("Probe tetrahedralization failed; skipping tetra buffer upload.");
            UploadTetrahedralization([], generation, probeCount);
            yield break;
        }

        if (triangulation is null)
        {
            Debug.LogWarning("Probe tetrahedralization returned null data; skipping tetra buffer upload.");
            UploadTetrahedralization([], generation, probeCount);
            yield break;
        }

        var cells = triangulation.Cells?.ToList();
        if (cells is null || cells.Count == 0)
        {
            Debug.LogWarning("Probe tetrahedralization produced no cells; skipping tetra buffer upload.");
            UploadTetrahedralization([], generation, probeCount);
            yield break;
        }

        var tetraData = new List<ProbeTetraData>(cells.Count);
        foreach (var cell in cells)
        {
            var v = cell.Vertices;
            if (v.Length >= 4)
            {
                tetraData.Add(new ProbeTetraData
                {
                    Indices = new Vector4(
                        probeIndices[v[0]],
                        probeIndices[v[1]],
                        probeIndices[v[2]],
                        probeIndices[v[3]])
                });
            }
        }

        UploadTetrahedralization(tetraData, generation, probeCount);
        yield break;
    }

    private static List<ProbeTetraData> BuildFallbackTetraData(Dictionary<LightProbeComponent, int> indices)
    {
        int count = indices.Count;
        var list = new List<ProbeTetraData>(1);

        int a = indices.Values.ElementAt(0);
        int b = count >= 2 ? indices.Values.ElementAt(1) : a;
        int c = count >= 3 ? indices.Values.ElementAt(2) : b;
        int d = count >= 4 ? indices.Values.ElementAt(3) : c;

        // Build one degenerate tetra that repeats available probes; shaders can treat this as a single-sample approximation.
        list.Add(new ProbeTetraData
        {
            Indices = new Vector4(a, b, c, d)
        });

        return list;
    }

    private void UploadTetrahedralization(IReadOnlyList<ProbeTetraData> tetraData, int generation, int probeCount)
    {
        if (generation != _probeTessellationGeneration)
            return;

        _probeTetraBuffer?.Dispose();
        if (tetraData.Count == 0)
        {
            _probeTetraBuffer = null;
            _probeTetraProbeCount = 0;
            return;
        }

        var tetraList = tetraData as IList<ProbeTetraData> ?? [.. tetraData];

        _probeTetraBuffer = new XRDataBuffer("LightProbeTetra", EBufferTarget.ShaderStorageBuffer, (uint)tetraList.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeTetraData>(), false, false)
        {
            BindingIndexOverride = 1,
        };
        _probeTetraBuffer.SetDataRaw(tetraList);
        _probeTetraBuffer.PushData();
        _probeTetraProbeCount = probeCount;
    }


    private void RestirCompositeFBO_SettingUniforms(XRRenderProgram program)
    {
    var region = RenderingPipelineState?.CurrentRenderRegion;
        float width = region?.Width > 0 ? region.Value.Width : InternalWidth;
        float height = region?.Height > 0 ? region.Value.Height : InternalHeight;
        program.Uniform("ScreenWidth", width);
        program.Uniform("ScreenHeight", height);
    }

    private void SurfelGICompositeFBO_SettingUniforms(XRRenderProgram program)
    {
        var region = RenderingPipelineState?.CurrentRenderRegion;
        float width = region?.Width > 0 ? region.Value.Width : InternalWidth;
        float height = region?.Height > 0 ? region.Value.Height : InternalHeight;
        program.Uniform("ScreenWidth", width);
        program.Uniform("ScreenHeight", height);
    }

    private void LightVolumeCompositeFBO_SettingUniforms(XRRenderProgram program)
    {
        var region = RenderingPipelineState?.CurrentRenderRegion;
        float width = region?.Width > 0 ? region.Value.Width : InternalWidth;
        float height = region?.Height > 0 ? region.Value.Height : InternalHeight;
        program.Uniform("ScreenWidth", width);
        program.Uniform("ScreenHeight", height);
    }

    #endregion

    #region Highlighting

    /// <summary>
    /// Stencil reference value for hover highlighting (bit 0).
    /// </summary>
    public const int StencilRefHover = 1;

    /// <summary>
    /// Stencil reference value for selection highlighting (bit 1).
    /// </summary>
    public const int StencilRefSelection = 2;

    /// <summary>
    /// This pipeline is set up to use the stencil buffer to highlight objects.
    /// This will highlight the given material.
    /// </summary>
    /// <param name="material">The material to highlight.</param>
    /// <param name="enabled">Whether to enable or disable highlighting.</param>
    /// <param name="isSelection">If true, uses the selection stencil value; otherwise uses hover stencil value.</param>
    public static void SetHighlighted(XRMaterial? material, bool enabled, bool isSelection = false)
    {
        if (material is null)
            return;

        //Set stencil buffer to indicate objects that should be highlighted.
        //material?.SetFloat("Highlighted", enabled ? 1.0f : 0.0f);
        var refValue = enabled ? (isSelection ? StencilRefSelection : StencilRefHover) : 0;
        var stencil = material.RenderOptions.StencilTest;
        stencil.Enabled = ERenderParamUsage.Enabled;
        stencil.FrontFace = new StencilTestFace()
        {
            Function = EComparison.Always,
            Reference = refValue,
            ReadMask = 3,
            WriteMask = 3,
            BothFailOp = EStencilOp.Keep,
            StencilPassDepthFailOp = EStencilOp.Keep,
            BothPassOp = EStencilOp.Replace,
        };
        stencil.BackFace = new StencilTestFace()
        {
            Function = EComparison.Always,
            Reference = refValue,
            ReadMask = 3,
            WriteMask = 3,
            BothFailOp = EStencilOp.Keep,
            StencilPassDepthFailOp = EStencilOp.Keep,
            BothPassOp = EStencilOp.Replace,
        };
    }

    /// <summary>
    /// This pipeline is set up to use the stencil buffer to highlight objects.
    /// This will highlight the given model.
    /// </summary>
    /// <param name="model">The model component to highlight.</param>
    /// <param name="enabled">Whether to enable or disable highlighting.</param>
    /// <param name="isSelection">If true, uses the selection stencil value; otherwise uses hover stencil value.</param>
    public static void SetHighlighted(ModelComponent? model, bool enabled, bool isSelection = false)
        => model?.Meshes.ForEach(m => m.LODs.ForEach(lod => SetHighlighted(lod.Renderer.Material, enabled, isSelection)));

    /// <summary>
    /// This pipeline is set up to use the stencil buffer to highlight objects.
    /// This will highlight the model representing the given rigid body.
    /// The model component must be a sibling component of the rigid body, or this will do nothing.
    /// </summary>
    /// <param name="body">The rigid body whose model to highlight.</param>
    /// <param name="enabled">Whether to enable or disable highlighting.</param>
    /// <param name="isSelection">If true, uses the selection stencil value; otherwise uses hover stencil value.</param>
    public static void SetHighlighted(PhysxDynamicRigidBody? body, bool enabled, bool isSelection = false)
        => SetHighlighted(body?.OwningComponent?.GetSiblingComponent<ModelComponent>(), enabled, isSelection);

    #endregion
}
