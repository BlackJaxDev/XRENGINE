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

public partial class DefaultRenderPipeline : RenderPipeline
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
    /// Latches the effective value once per pipeline render so nested scene/light-probe
    /// captures cannot bleed HDR state into resize-time resource recreation or final output.
    /// Falls back to global engine setting when no camera is available.
    /// </summary>
    internal static bool ResolveOutputHDR()
    {
        XRRenderPipelineInstance? pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
        if (pipeline is not null)
        {
            bool? latched = pipeline.EffectiveOutputHDRThisFrame;
            if (latched.HasValue)
                return latched.Value;

            XRCamera? camera = pipeline.RenderState.SceneCamera
                ?? pipeline.RenderState.RenderingCamera
                ?? pipeline.LastSceneCamera
                ?? pipeline.LastRenderingCamera;
            return camera?.OutputHDROverride ?? Engine.Rendering.Settings.OutputHDR;
        }

        var fallbackCamera = Engine.Rendering.State.RenderingPipelineState?.SceneCamera
            ?? Engine.Rendering.State.RenderingCamera;
        return fallbackCamera?.OutputHDROverride ?? Engine.Rendering.Settings.OutputHDR;
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
    /// Prefers the latched per-frame value when available so nested quad/light-probe
    /// renders cannot observe a different AA mode partway through the frame.
    /// </summary>
    private static EAntiAliasingMode ResolveAntiAliasingMode()
        => RenderPipeline.ResolveEffectiveAntiAliasingModeForFrame();

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
    /// Prefers the latched per-frame value when available so nested quad/light-probe
    /// renders cannot observe a different MSAA sample count partway through the frame.
    /// </summary>
    internal static uint ResolveEffectiveMsaaSampleCount()
        => RenderPipeline.ResolveEffectiveMsaaSampleCountForFrame();

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

    private bool NeedsRecreateLightCombineFbo(XRFrameBuffer fbo)
    {
        if (!fbo.IsLastCheckComplete)
            return true;

        if (!HasSingleColorTarget(fbo, DiffuseTextureName))
            return true;

        if (fbo is not XRQuadFrameBuffer quadFbo || quadFbo.Material is not XRMaterial material)
            return true;

        if (quadFbo.DeriveRenderTargetsFromMaterial)
            return true;

        var textures = material.Textures;
        if (textures.Count != 7)
            return true;

        if (!ReferenceEquals(textures[0], GetTexture<XRTexture>(AlbedoOpacityTextureName))
            || !ReferenceEquals(textures[1], GetTexture<XRTexture>(NormalTextureName))
            || !ReferenceEquals(textures[2], GetTexture<XRTexture>(RMSETextureName))
            || !ReferenceEquals(textures[3], GetTexture<XRTexture>(AmbientOcclusionIntensityTextureName))
            || !ReferenceEquals(textures[4], GetTexture<XRTexture>(DepthViewTextureName))
            || !ReferenceEquals(textures[5], GetTexture<XRTexture>(DiffuseTextureName))
            || !ReferenceEquals(textures[6], GetTexture<XRTexture>(BRDFTextureName)))
            return true;

        var fragmentShaders = material.FragmentShaders;
        if (fragmentShaders.Count != 1)
            return true;

        XRShader expectedShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, DeferredLightCombineShaderName()),
            EShaderType.Fragment);
        return !ReferenceEquals(fragmentShaders[0], expectedShader);
    }

    private bool HasSingleColorTarget(XRFrameBuffer fbo, string textureName)
    {
        if (fbo.Targets is not { Length: 1 })
            return false;

        var (target, attachment, mipLevel, layerIndex) = fbo.Targets[0];
        return attachment == EFrameBufferAttachment.ColorAttachment0
            && mipLevel == 0
            && layerIndex == -1
            && ReferenceEquals(target, GetTexture<XRTexture>(textureName));
    }

    private bool NeedsRecreatePostProcessOutputFbo(XRFrameBuffer fbo)
    {
        if (NeedsRecreateFboDueToOutputFormat(fbo) || !fbo.IsLastCheckComplete)
            return true;

        return !HasSingleColorTarget(fbo, PostProcessOutputTextureName);
    }

    private bool NeedsRecreateFxaaFbo(XRFrameBuffer fbo)
    {
        if (NeedsRecreateFboDueToOutputFormat(fbo) || !fbo.IsLastCheckComplete)
            return true;

        if (!HasSingleColorTarget(fbo, FxaaOutputTextureName))
            return true;

        if (fbo is not XRQuadFrameBuffer quadFbo || quadFbo.Material is not XRMaterial material)
            return true;

        if (quadFbo.DeriveRenderTargetsFromMaterial)
            return true;

        var textures = material.Textures;
        if (textures.Count != 1)
            return true;

        if (!ReferenceEquals(textures[0], GetTexture<XRTexture>(PostProcessOutputTextureName)))
            return true;

        var fragmentShaders = material.FragmentShaders;
        if (fragmentShaders.Count != 1)
            return true;

        XRShader expectedShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, "FXAA.fs"),
            EShaderType.Fragment);
        return !ReferenceEquals(fragmentShaders[0], expectedShader);
    }

    private bool NeedsRecreateTsrHistoryColorFbo(XRFrameBuffer fbo)
    {
        if (NeedsRecreateFboDueToOutputFormat(fbo) || !fbo.IsLastCheckComplete)
            return true;

        return !HasSingleColorTarget(fbo, TsrHistoryColorTextureName);
    }

    private bool NeedsRecreateTsrUpscaleFbo(XRFrameBuffer fbo)
    {
        if (NeedsRecreateFboDueToOutputFormat(fbo) || !fbo.IsLastCheckComplete)
            return true;

        if (!HasSingleColorTarget(fbo, FxaaOutputTextureName))
            return true;

        if (fbo is not XRQuadFrameBuffer quadFbo || quadFbo.Material is not XRMaterial material)
            return true;

        if (quadFbo.DeriveRenderTargetsFromMaterial)
            return true;

        var textures = material.Textures;
        if (textures.Count != 5)
            return true;

        if (!ReferenceEquals(textures[0], GetTexture<XRTexture>(PostProcessOutputTextureName))
            || !ReferenceEquals(textures[1], GetTexture<XRTexture>(VelocityTextureName))
            || !ReferenceEquals(textures[2], GetTexture<XRTexture>(DepthViewTextureName))
            || !ReferenceEquals(textures[3], GetTexture<XRTexture>(HistoryDepthViewTextureName))
            || !ReferenceEquals(textures[4], GetTexture<XRTexture>(TsrHistoryColorTextureName)))
            return true;

        var fragmentShaders = material.FragmentShaders;
        if (fragmentShaders.Count != 1)
            return true;

        XRShader expectedShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, "TemporalSuperResolution.fs"),
            EShaderType.Fragment);
        return !ReferenceEquals(fragmentShaders[0], expectedShader);
    }

    private bool NeedsRecreatePostProcessFbo(XRFrameBuffer fbo)
    {
        if (!fbo.IsLastCheckComplete)
            return true;

        if (fbo is not XRQuadFrameBuffer quadFbo || quadFbo.Material is not XRMaterial material)
            return true;

        if (quadFbo.DeriveRenderTargetsFromMaterial)
            return true;

        var textures = material.Textures;
        if (textures.Count != 5)
            return true;

        if (!ReferenceEquals(textures[0], GetTexture<XRTexture>(HDRSceneTextureName))
            || !ReferenceEquals(textures[1], GetTexture<XRTexture>(BloomBlurTextureName))
            || !ReferenceEquals(textures[2], GetTexture<XRTexture>(DepthViewTextureName))
            || !ReferenceEquals(textures[3], GetTexture<XRTexture>(StencilViewTextureName))
            || !ReferenceEquals(textures[4], GetTexture<XRTexture>(AutoExposureTextureName)))
            return true;

        var fragmentShaders = material.FragmentShaders;
        if (fragmentShaders.Count != 1)
            return true;

        XRShader expectedShader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, PostProcessShaderName()),
            EShaderType.Fragment);
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
    public const string AmbientOcclusionIntensityTextureName = EngineShaderBindingNames.Samplers.AmbientOcclusionTexture;
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
    public const string BRDFTextureName = EngineShaderBindingNames.Samplers.BRDF;
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
        && (Engine.Rendering.State.CurrentRenderingPipeline?.Pipeline as DefaultRenderPipeline)?.EnableDeferredMsaa == true;

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

    public DefaultRenderPipeline() : this(false)
    {
    }

    public DefaultRenderPipeline(bool stereo = false) : base(true)
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

    protected override void OnDestroying()
    {
        Engine.Rendering.SettingsChanged -= HandleRenderingSettingsChanged;
        Engine.Rendering.AntiAliasingSettingsChanged -= HandleAntiAliasingSettingsChanged;
        ClearProbeResources();
        base.OnDestroying();
    }

    private void HandleRenderingSettingsChanged()
    {
        if (IsDestroyed)
            return;

        Engine.InvokeOnMainThread(() =>
        {
            if (IsDestroyed)
                return;

            ApplyAntiAliasingResolutionHint();
            CommandChain = GenerateCommandChain();
            foreach (var instance in Instances)
                instance.DestroyCache();
        }, "DefaultRenderPipeline: Rendering settings changed", true);
    }

    private void HandleAntiAliasingSettingsChanged()
    {
        if (IsDestroyed)
            return;

        Engine.InvokeOnMainThread(() =>
        {
            if (IsDestroyed)
                return;

            ApplyAntiAliasingResolutionHint();

            foreach (var instance in Instances)
                InvalidateAntiAliasingResources(instance);

            foreach (var window in Engine.Windows)
            {
                window.InvalidateScenePanelResources();
                window.RequestRenderStateRecheck(resetCircuitBreaker: true);
            }
        }, "DefaultRenderPipeline: AA settings changed", true);
    }

    private static void InvalidateAntiAliasingResources(XRRenderPipelineInstance instance)
    {
        const string reason = "AntiAliasingSettingsChanged";

        foreach (string name in AntiAliasingFrameBufferDependencies)
            instance.RemoveFrameBufferResource(name, reason);

        foreach (string name in AntiAliasingTextureDependencies)
            instance.RemoveTextureResource(name, reason);
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

    #region Command Chain Generation

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer c = new(this);
        var ifElse = c.Add<VPRC_IfElse>();
        ifElse.ConditionEvaluator = () => State.WindowViewport is not null;
        ifElse.TrueCommands = CreateViewportTargetCommands();
        ifElse.FalseCommands = CreateFBOTargetCommands();
        return c;
    }

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

    public ViewportRenderCommandContainer CreateFBOTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);
        bool enableComputePasses = EnableComputeDependentPasses;

        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (c.AddUsing<VPRC_PushOutputFBORenderArea>())
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                c.Add<VPRC_StencilMask>().Set(~0u);
                c.Add<VPRC_ClearByBoundFBO>();
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, GPURenderDispatch);
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, GPURenderDispatch);
                if (enableComputePasses)
                    c.Add<VPRC_ForwardPlusLightCullingPass>();
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, GPURenderDispatch);
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PerPixelLinkedListForward, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DepthPeelingForward, GPURenderDispatch);
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GPURenderDispatch);
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, GPURenderDispatch);
            }
        }
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, GPURenderDispatch);
        c.Add<VPRC_RenderScreenSpaceUI>();
        return c;
    }


    private ViewportRenderCommandContainer CreateViewportTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);
        bool enableComputePasses = EnableComputeDependentPasses;
        bool bypassVendorUpscale = string.Equals(
            Environment.GetEnvironmentVariable("XRE_BYPASS_VENDOR_UPSCALE"),
            "1",
            StringComparison.Ordinal);

        c.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Begin;

        CacheTextures(c);

        if (enableComputePasses)
        {
            c.Add<VPRC_VoxelConeTracingPass>().SetOptions(VoxelConeTracingVolumeTextureName,
                [
                    (int)EDefaultRenderPass.OpaqueDeferred,
                    (int)EDefaultRenderPass.OpaqueForward,
                    (int)EDefaultRenderPass.MaskedForward
                ],
                GPURenderDispatch,
                true);
        }
            
        //Create FBOs only after all their texture dependencies have been cached.

        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        {
            if (enableComputePasses)
            {
                // Render to the ambient occlusion FBO using a switch to select the active AO implementation.
                var aoSwitch = c.Add<VPRC_Switch>();
                aoSwitch.SwitchEvaluator = EvaluateAmbientOcclusionMode;
                aoSwitch.Cases = new()
                {
                    [(int)AmbientOcclusionSettings.EType.ScreenSpace] = CreateSSAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.HorizonBased] = CreateHBAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion] = CreateVXAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.MultiViewCustom] = CreateMVAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype] = CreateMSVOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.SpatialHashExperimental] = CreateSpatialHashAOPassCommands(),
                };
                aoSwitch.DefaultCase = CreateAmbientOcclusionDisabledPassCommands();
            }
            else
            {
                var aoSwitch = c.Add<VPRC_Switch>();
                aoSwitch.SwitchEvaluator = EvaluateAmbientOcclusionMode;
                aoSwitch.Cases = new()
                {
                    [(int)AmbientOcclusionSettings.EType.ScreenSpace] = CreateSSAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.HorizonBased] = CreateHBAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion] = CreateVXAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.MultiViewCustom] = CreateSSAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype] = CreateSSAOPassCommands(),
                    [(int)AmbientOcclusionSettings.EType.SpatialHashExperimental] = CreateSSAOPassCommands(),
                };
                aoSwitch.DefaultCase = CreateAmbientOcclusionDisabledPassCommands();
            }

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                DeferredGBufferFBOName,
                CreateDeferredGBufferFBO,
                GetDesiredFBOSizeInternal,
                NeedsRecreateDeferredGBufferFbo);

            // MSAA deferred GBuffer and Lighting FBOs must be cached before any command tries to bind them.
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                MsaaGBufferFBOName,
                CreateMsaaGBufferFBO,
                GetDesiredFBOSizeInternal,
                NeedsRecreateMsaaFbo);
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                MsaaLightingFBOName,
                CreateMsaaLightingFBO,
                GetDesiredFBOSizeInternal,
                NeedsRecreateMsaaFbo);

            // Deferred GBuffer geometry rendering.
            // When MSAA deferred is active, renders into the MSAA GBuffer FBO for per-sample surface data.
            // Otherwise renders into the dedicated non-MSAA GBuffer FBO.
            // Always clear color+depth so the GBuffer starts with known values.
            using (c.AddUsing<VPRC_BindFBOByName>(x =>
            {
                x.Write = true;
                x.ClearColor = true;
                x.ClearDepth = true;
                x.ClearStencil = true;
                x.DynamicName = () => RuntimeEnableMsaaDeferred ? MsaaGBufferFBOName : DeferredGBufferFBOName;
            }))
            {
                c.Add<VPRC_StencilMask>().Set(~0u);
                    c.Add<VPRC_DepthTest>().Enable = true;
                    c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, GPURenderDispatch);
                    c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DeferredDecals, GPURenderDispatch);
            }

            // When MSAA deferred is active, also resolve geometry into the non-MSAA GBuffer FBO
            // so that the AO pass has correct GBuffer data (SSAO doesn't support MSAA textures).
            {
                var msaaGBufferBranch = c.Add<VPRC_IfElse>();
                msaaGBufferBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
                {
                    var msaaGeomCmds = new ViewportRenderCommandContainer(this);
                    // Resolve MSAA GBuffer → non-MSAA GBuffer for AO compatibility.
                    msaaGeomCmds.Add<VPRC_ResolveMsaaGBuffer>().SetOptions(
                        MsaaGBufferFBOName,
                        DeferredGBufferFBOName,
                        colorAttachmentCount: 4,
                        resolveDepthStencil: true);
                    msaaGBufferBranch.TrueCommands = msaaGeomCmds;
                }
            }

            // Forward depth+normal pre-pass
            var prePassChoice = c.Add<VPRC_IfElse>();
            prePassChoice.ConditionEvaluator = () => Engine.EditorPreferences.Debug.ForwardDepthPrePassEnabled;
            {
                // When sharing GBuffer targets, skip the dedicated forward-only FBO
                // and render only into the merged GBuffer attachments.
                var shareChoice = new ViewportRenderCommandContainer(this);
                var shareIfElse = shareChoice.Add<VPRC_IfElse>();
                shareIfElse.ConditionEvaluator = () => Engine.EditorPreferences.Debug.ForwardPrePassSharesGBufferTargets;
                shareIfElse.TrueCommands = CreateForwardPrePassSharedCommands();
                shareIfElse.FalseCommands = CreateForwardPrePassSeparateCommands();
                prePassChoice.TrueCommands = shareChoice;
            }

            c.Add<VPRC_DepthTest>().Enable = false;

            var aoResolveSwitch = c.Add<VPRC_Switch>();
            aoResolveSwitch.SwitchEvaluator = EvaluateAmbientOcclusionMode;
            aoResolveSwitch.Cases = new()
            {
                [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusResolveCommands(),
                [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOResolveCommands(),
            };
            aoResolveSwitch.DefaultCase = CreateAmbientOcclusionResolveCommands();

            //LightCombine FBO
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                LightCombineFBOName,
                CreateLightCombineFBO,
                GetDesiredFBOSizeInternal,
                NeedsRecreateLightCombineFbo)
                .UseLifetime(RenderResourceLifetime.Transient);

            // MSAA deferred: mark complex pixels in the MSAA depth-stencil before lighting
            {
                var msaaMarkBranch = c.Add<VPRC_IfElse>();
                msaaMarkBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
                {
                    var markCmds = new ViewportRenderCommandContainer(this);
                    // Clear color (zero for additive lighting) and stencil (fresh for marking),
                    // but NOT depth — the MSAA depth-stencil is shared from the GBuffer pass.
                    using (markCmds.AddUsing<VPRC_BindFBOByName>(x =>
                        x.SetOptions(MsaaLightingFBOName, write: true, clearColor: true, clearDepth: false, clearStencil: true)))
                    {
                        markCmds.Add<VPRC_StencilMask>().Set(~0u);
                        markCmds.Add<VPRC_MarkComplexMsaaPixels>().SetOptions(
                            MsaaNormalTextureName,
                            MsaaDepthViewTextureName);
                    }
                    msaaMarkBranch.TrueCommands = markCmds;
                }
            }

            // Render the GBuffer to the lighting FBO.
            // When MSAA deferred is active, light volumes render into the MSAA Lighting FBO
            // using two-pass (simple + complex with per-sample shading).
            // Otherwise, light volumes render into the standard LightCombine FBO.
            {
                var msaaLightingBranch = c.Add<VPRC_IfElse>();
                msaaLightingBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
                {
                    // MSAA path: render lights into MSAA Lighting FBO, then resolve to DiffuseTexture
                    var msaaLightCmds = new ViewportRenderCommandContainer(this);
                    // Do NOT clear — color was zeroed and stencil was marked by the marking phase above;
                    // depth is shared from the GBuffer and must be preserved for light volume testing.
                    using (msaaLightCmds.AddUsing<VPRC_BindFBOByName>(x =>
                        x.SetOptions(MsaaLightingFBOName, write: true, clearColor: false, clearDepth: false, clearStencil: false)))
                    {
                        msaaLightCmds.Add<VPRC_StencilMask>().Set(~0u);
                        var msaaLightPass = msaaLightCmds.Add<VPRC_LightCombinePass>();
                        msaaLightPass.SetOptions(
                            AlbedoOpacityTextureName,
                            NormalTextureName,
                            RMSETextureName,
                            DepthViewTextureName);
                        msaaLightPass.MsaaDeferred = true;
                    }
                    // Resolve MSAA lighting → non-MSAA DiffuseTexture
                    msaaLightCmds.Add<VPRC_BlitFrameBuffer>().SetOptions(
                        MsaaLightingFBOName,
                        LightCombineFBOName,
                        EReadBufferMode.ColorAttachment0,
                        blitColor: true,
                        blitDepth: false,
                        blitStencil: false,
                        linearFilter: false);
                    msaaLightingBranch.TrueCommands = msaaLightCmds;
                }
                {
                    // Non-MSAA path: render lights directly into LightCombine FBO
                    var stdLightCmds = new ViewportRenderCommandContainer(this);
                    using (stdLightCmds.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(LightCombineFBOName)))
                    {
                        stdLightCmds.Add<VPRC_StencilMask>().Set(~0u);
                        stdLightCmds.Add<VPRC_LightCombinePass>().SetOptions(
                            AlbedoOpacityTextureName,
                            NormalTextureName,
                            RMSETextureName,
                            DepthViewTextureName);
                    }
                    msaaLightingBranch.FalseCommands = stdLightCmds;
                }
            }

            // Always create MSAA FBOs so per-camera AA overrides can use them at runtime.
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                ForwardPassMsaaFBOName,
                CreateForwardPassMsaaFBO,
                GetDesiredFBOSizeInternal,
                NeedsRecreateMsaaFbo);
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                MsaaLightCombineFBOName,
                CreateMsaaLightCombineFBO,
                GetDesiredFBOSizeInternal,
                NeedsRecreateMsaaLightCombineFbo);
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                DepthPreloadFBOName,
                CreateDepthPreloadFBO,
                GetDesiredFBOSizeInternal);

            // MSAA deferred FBO caching is done earlier (before GBuffer geometry render).

            //ForwardPass FBO
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                ForwardPassFBOName,
                CreateForwardPassFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                SceneCopyFBOName,
                CreateSceneCopyFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TransparentSceneCopyFBOName,
                CreateTransparentSceneCopyFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                DeferredTransparencyBlurFBOName,
                CreateDeferredTransparencyBlurFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TransparentAccumulationFBOName,
                CreateTransparentAccumulationFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TransparentResolveFBOName,
                CreateTransparentResolveFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                RestirCompositeFBOName,
                CreateRestirCompositeFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                LightVolumeCompositeFBOName,
                CreateLightVolumeCompositeFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                RadianceCascadeCompositeFBOName,
                CreateRadianceCascadeCompositeFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                SurfelGICompositeFBOName,
                CreateSurfelGICompositeFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                HistoryCaptureFBOName,
                CreateHistoryCaptureFBO,
                GetDesiredFBOSizeInternal);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TemporalInputFBOName,
                CreateTemporalInputFBO,
                GetDesiredFBOSizeInternal);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TemporalAccumulationFBOName,
                CreateTemporalAccumulationFBO,
                GetDesiredFBOSizeInternal);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                HistoryExposureFBOName,
                CreateHistoryExposureFBO,
                GetDesiredFBOSizeInternal);

            //Render forward pass - GBuffer results + forward lit meshes + debug data
            // FBO target and clear flags are resolved at render time so per-camera AA overrides work.
            // Color is always cleared: the LightCombine quad overwrites every pixel, but stale
            // HDRSceneTexture content from the previous frame can bleed through if the quad
            // doesn't cover fully (e.g., edge of viewport) or if the MSAA resolve blit fails.
            // Depth is only cleared for MSAA (renderbuffers start undefined); the non-MSAA path
            // preserves GBuffer depth so forward materials depth-test against deferred geometry.
            // Stencil is always cleared so post-process outline is driven only by current-frame writes.
            using (c.AddUsing<VPRC_BindFBOByName>(x =>
            {
                x.Write = true;
                x.ClearStencil = true;
                x.DynamicName = () => RuntimeEnableMsaa ? ForwardPassMsaaFBOName : ForwardPassFBOName;
                x.ClearColor = true;
                x.DynamicClearDepth = () => RuntimeEnableMsaa;
            }))
            {
                // Depth preload is only needed for MSAA.
                var msaaPreload = c.Add<VPRC_IfElse>();
                msaaPreload.ConditionEvaluator = () => RuntimeEnableMsaa;
                {
                    var preloadCmds = new ViewportRenderCommandContainer(this);

                    // When deferred MSAA is active, blit per-sample depth from the MSAA GBuffer
                    // instead of the non-MSAA shader-based preload. This preserves per-sample
                    // depth at silhouette edges so the skybox can render at actual sky samples
                    // and forward meshes get correct per-sample depth testing.
                    var deferredChoice = preloadCmds.Add<VPRC_IfElse>();
                    deferredChoice.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
                    {
                        var blitCmds = new ViewportRenderCommandContainer(this);
                        blitCmds.Add<VPRC_BlitFrameBuffer>().SetOptions(
                            MsaaGBufferFBOName,
                            ForwardPassMsaaFBOName,
                            EReadBufferMode.ColorAttachment0,
                            blitColor: false,
                            blitDepth: true,
                            blitStencil: false,
                            linearFilter: false);
                        deferredChoice.TrueCommands = blitCmds;
                    }
                    {
                        // Forward-only MSAA: shader-based preload from non-MSAA depth
                        var shaderCmds = new ViewportRenderCommandContainer(this);
                        shaderCmds.Add<VPRC_RenderQuadToFBO>().SetTargets(DepthPreloadFBOName, ForwardPassMsaaFBOName);
                        deferredChoice.FalseCommands = shaderCmds;
                    }

                    msaaPreload.TrueCommands = preloadCmds;
                }

                //Render the deferred pass lighting result, no depth testing
                c.Add<VPRC_DepthTest>().Enable = false;

                // When deferred MSAA is active, use the per-sample LightCombine variant
                // so direct light is read from the MSAA lighting texture per-sample via
                // sampler2DMS + gl_SampleID. This avoids the dark silhouette edges that
                // occur when the premature resolve averages sky-samples (zero) with
                // geometry lighting before the skybox has a chance to fill them.
                var lightCompositeBranch = c.Add<VPRC_IfElse>();
                lightCompositeBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
                {
                    var msaaCmds = new ViewportRenderCommandContainer(this);
                    msaaCmds.Add<VPRC_SampleShading>().Enable = true;
                    msaaCmds.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = MsaaLightCombineFBOName;
                    msaaCmds.Add<VPRC_SampleShading>().Enable = false;
                    lightCompositeBranch.TrueCommands = msaaCmds;
                }
                {
                    var stdCmds = new ViewportRenderCommandContainer(this);
                    stdCmds.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = LightCombineFBOName;
                    lightCompositeBranch.FalseCommands = stdCmds;
                }

                //Backgrounds (skybox) should honor the depth buffer but avoid modifying it
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, GPURenderDispatch);

                //Enable depth testing and writing for forward passes
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = true;
                if (enableComputePasses)
                    c.Add<VPRC_ForwardPlusLightCullingPass>();
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, GPURenderDispatch);

                if (enableComputePasses)
                {
                    c.Add<VPRC_ReSTIRPass>();
                    c.Add<VPRC_LightVolumesPass>();
                    c.Add<VPRC_RadianceCascadesPass>();
                    c.Add<VPRC_SurfelGIPass>();
                }

                c.Add<VPRC_RenderDebugShapes>();
                c.Add<VPRC_RenderDebugPhysics>();
            }

            // MSAA resolve blit: only execute when MSAA is active for the current camera.
            // Only color is resolved; OpenGL 4.6 §18.3.1 forbids blitting depth/stencil
            // from a multisampled read framebuffer to a single-sample draw framebuffer
            // (generates GL_INVALID_OPERATION and aborts the entire blit, including color).
            // The non-MSAA DepthStencilTexture retains the GBuffer depth, which is sufficient
            // for subsequent transparent passes and post-processing.
            {
                var msaaResolve = c.Add<VPRC_IfElse>();
                msaaResolve.ConditionEvaluator = () => RuntimeEnableMsaa;
                {
                    var resolveCmds = new ViewportRenderCommandContainer(this);
                    resolveCmds.Add<VPRC_ResolveMsaaGBuffer>().SetOptions(
                        ForwardPassMsaaFBOName,
                        ForwardPassFBOName,
                        colorAttachmentCount: 1,
                        resolveDepthStencil: true,
                        depthViewTextureName: ForwardPassMsaaDepthViewTextureName);
                    msaaResolve.TrueCommands = resolveCmds;
                }
            }

            c.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
            c.Add<VPRC_RenderQuadFBO>().FrameBufferName = DeferredTransparencyBlurFBOName;
            c.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
            c.Add<VPRC_ClearTextureByName>().SetOptions(TransparentAccumTextureName, ColorF4.Transparent);
            c.Add<VPRC_ClearTextureByName>().SetOptions(TransparentRevealageTextureName, ColorF4.White);
            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(TransparentAccumulationFBOName, true, false, false, false)))
            {
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, GPURenderDispatch);
            }
            c.Add<VPRC_RenderQuadFBO>().FrameBufferName = TransparentResolveFBOName;

            AppendExactTransparencyCommands(c);

            // TransparentForward and OnTopForward are rendered AFTER the temporal
            // accumulation resolve (see below) so that sub-pixel jitter does not
            // shift alpha-test / blend boundaries, which causes smearing/ghosting
            // when TAA/TSR tries to blend jittered transparent edges with history.

            c.Add<VPRC_DepthTest>().Enable = false;

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                VelocityFBOName,
                CreateVelocityFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);

            // Ensure the velocity target is initialized to zero instead of inheriting whatever clear
            // color previous passes left on the renderer. Non-zero clears here imprint the scene's
            // clear color into the velocity buffer, which then looks like a color pass when previewed
            // and corrupts motion blur accumulation.
            //Debug.Out($"[Velocity] Preparing velocity pass. InternalSize={InternalWidth}x{InternalHeight} Stereo={Stereo} Msaa={EnableMsaa}");
            c.Add<VPRC_SetClears>().Set(ColorF4.Black, null, null);
            // Keep the existing depth buffer so skyboxes/UI behind geometry do not write into velocity.
            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(VelocityFBOName, true, true, false, false)))
            {
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                // GPU path currently ignores override materials; force CPU so motion vectors render with the correct material.
                // Skip background/on-top passes so skyboxes/UI do not pollute the velocity buffer.
                c.Add<VPRC_RenderMotionVectorsPass>().SetOptions(false,
                    new[]
                    {
                        (int)EDefaultRenderPass.OpaqueDeferred,
                        (int)EDefaultRenderPass.DeferredDecals,
                        (int)EDefaultRenderPass.OpaqueForward,
                        (int)EDefaultRenderPass.MaskedForward,
                        (int)EDefaultRenderPass.WeightedBlendedOitForward,
                        (int)EDefaultRenderPass.PerPixelLinkedListForward,
                        (int)EDefaultRenderPass.DepthPeelingForward,
                        // TransparentForward is omitted: it renders after temporal
                        // accumulation to avoid TAA smearing artifacts.
                    });
                c.Add<VPRC_DepthWrite>().Allow = true;
            }
            // Restore clears for subsequent passes to the pipeline defaults.
            c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);

            c.Add<VPRC_DepthTest>().Enable = false;

            c.Add<VPRC_BloomPass>().SetTargetFBONames(
                ForwardPassFBOName,
                BloomBlurTextureName,
                Stereo);

            var motionBlurChoice = c.Add<VPRC_IfElse>();
            motionBlurChoice.ConditionEvaluator = ShouldUseMotionBlur;
            motionBlurChoice.TrueCommands = CreateMotionBlurPassCommands();

            var dofChoice = c.Add<VPRC_IfElse>();
            dofChoice.ConditionEvaluator = ShouldUseDepthOfField;
            dofChoice.TrueCommands = CreateDepthOfFieldPassCommands();

            var temporalAccumulate = c.Add<VPRC_TemporalAccumulationPass>();
            temporalAccumulate.Phase = VPRC_TemporalAccumulationPass.EPhase.Accumulate;
            temporalAccumulate.ConfigureAccumulationTargets(
                ForwardPassFBOName,
                TemporalInputFBOName,
                TemporalAccumulationFBOName,
                HistoryCaptureFBOName,
                HistoryExposureFBOName);

            // Pop jitter so transparent / masked forward passes render with a
            // clean (unjittered) projection. This prevents sub-pixel jitter from
            // shifting alpha-test / blend boundaries that cause TAA smearing.
            c.Add<VPRC_TemporalAccumulationPass>().Phase =
                VPRC_TemporalAccumulationPass.EPhase.PopJitter;

            // Render transparent and on-top forward passes AFTER temporal resolve.
            // They composite on top of the resolved opaque image without temporal
            // accumulation, avoiding ghosting/smearing on transparent edges.
            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardPassFBOName, true, false, false, false)))
            {
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GPURenderDispatch);
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, GPURenderDispatch);
            }

            c.Add<VPRC_DepthTest>().Enable = false;

            //PostProcess FBO
            //This FBO is created here because it relies on BloomBlurTextureName, which is created in the BloomPass.
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                PostProcessFBOName,
                CreatePostProcessFBO,
                GetDesiredFBOSizeInternal,
                NeedsRecreatePostProcessFbo)
                .UseLifetime(RenderResourceLifetime.Transient);

            if (EnableTransformIdVisualization)
            {
                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    TransformIdDebugQuadFBOName,
                    CreateTransformIdDebugQuadFBO,
                    GetDesiredFBOSizeInternal);
            }

            if (EnableTransparencyAccumulationVisualization)
            {
                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    TransparentAccumulationDebugFBOName,
                    CreateTransparentAccumulationDebugFBO,
                    GetDesiredFBOSizeInternal);
            }

            if (EnableTransparencyRevealageVisualization)
            {
                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    TransparentRevealageDebugFBOName,
                    CreateTransparentRevealageDebugFBO,
                    GetDesiredFBOSizeInternal);
            }

            if (EnableTransparencyOverdrawVisualization)
            {
                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    TransparentOverdrawDebugFBOName,
                    CreateTransparentOverdrawDebugFBO,
                    GetDesiredFBOSizeInternal);
            }

            if (EnableDepthPeelingLayerVisualization)
            {
                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    DepthPeelingDebugFBOName,
                    CreateDepthPeelingDebugFBO,
                    GetDesiredFBOSizeInternal);
            }

            // Always create FXAA resources so per-camera AA overrides can use them at runtime.
            c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                FxaaOutputTextureName,
                CreateFxaaOutputTexture,
                NeedsRecreateOutputTextureFullSize,
                ResizeTextureFullSize);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                PostProcessOutputFBOName,
                CreatePostProcessOutputFBO,
                GetDesiredFBOSizeInternal,
                NeedsRecreatePostProcessOutputFbo);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                FxaaFBOName,
                CreateFxaaFBO,
                GetDesiredFBOSizeFull,
                NeedsRecreateFxaaFbo);

            c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                TsrHistoryColorTextureName,
                CreateTsrHistoryColorTexture,
                NeedsRecreateOutputTextureFullSize,
                ResizeTextureFullSize);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TsrHistoryColorFBOName,
                CreateTsrHistoryColorFBO,
                GetDesiredFBOSizeFull,
                NeedsRecreateTsrHistoryColorFbo);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TsrUpscaleFBOName,
                CreateTsrUpscaleFBO,
                GetDesiredFBOSizeFull,
                NeedsRecreateTsrUpscaleFbo);

            //c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            //    UserInterfaceFBOName,
            //    CreateUserInterfaceFBO,
            //    GetDesiredFBOSizeInternal);

        }

        // GPU auto-exposure: dispatch the compute shader BEFORE the post-process quad
        // so the 1x1 exposure texture and the _gpuAutoExposureReadyThisFrame flag are
        // current when PostProcess.fs samples them.  The HDR scene buffer is already
        // fully rendered at this point (opaque + transparent + bloom are complete).
        {
            string exposureSource = HDRSceneTextureName;
            c.Add<VPRC_ExposureUpdate>().SetOptions(exposureSource, true);
        }

            // Post-AA chain: FXAA and SMAA run against the post-process output, while TSR
            // resolves from internal resolution and writes a full-resolution result.
        {
            var upscaleChoice = c.Add<VPRC_IfElse>();
                upscaleChoice.ConditionEvaluator = () => RuntimeEnableFxaa || RuntimeEnableSmaa || RuntimeNeedsTsrUpscale;
            {
                var upscaleCmds = new ViewportRenderCommandContainer(this);

                // First pass: PostProcess quad renders to PostProcessOutputTexture at internal resolution
                using (upscaleCmds.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
                {
                    upscaleCmds.Add<VPRC_RenderQuadToFBO>().SetTargets(PostProcessFBOName, PostProcessOutputFBOName);
                }

                    // Second pass: apply the selected anti-aliasing path.
                using (upscaleCmds.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
                {
                        var tsrOrPostAa = upscaleCmds.Add<VPRC_IfElse>();
                        tsrOrPostAa.ConditionEvaluator = () => RuntimeNeedsTsrUpscale;
                    {
                            var tsrUpscale = new ViewportRenderCommandContainer(this);
                            tsrUpscale.Add<VPRC_RenderQuadToFBO>().SetTargets(TsrUpscaleFBOName, TsrUpscaleFBOName);
                            tsrUpscale.Add<VPRC_BlitFrameBuffer>().SetOptions(
                                TsrUpscaleFBOName,
                                TsrHistoryColorFBOName,
                                EReadBufferMode.ColorAttachment0,
                                blitColor: true,
                                blitDepth: false,
                                blitStencil: false,
                                linearFilter: false);
                            tsrOrPostAa.TrueCommands = tsrUpscale;
                    }
                    {
                            var fxaaOrSmaa = new ViewportRenderCommandContainer(this);
                            var postAaChoice = fxaaOrSmaa.Add<VPRC_IfElse>();
                            postAaChoice.ConditionEvaluator = () => RuntimeEnableFxaa;
                            {
                                var fxaaUpscale = new ViewportRenderCommandContainer(this);
                                fxaaUpscale.Add<VPRC_RenderQuadToFBO>().SetTargets(FxaaFBOName, FxaaFBOName);
                                postAaChoice.TrueCommands = fxaaUpscale;
                            }
                            {
                                var smaaUpscale = new ViewportRenderCommandContainer(this);
                                var smaa = smaaUpscale.Add<VPRC_SMAA>();
                                smaa.SourceTextureName = PostProcessOutputTextureName;
                                smaa.OutputTextureName = SmaaOutputTextureName;
                                smaa.OutputFBOName = SmaaFBOName;
                                postAaChoice.FalseCommands = smaaUpscale;
                            }
                            tsrOrPostAa.FalseCommands = fxaaOrSmaa;
                    }
                }

                upscaleChoice.TrueCommands = upscaleCmds;
            }
        }

        // Temporal commit is CPU-side state bookkeeping only (no GPU ops).
        c.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Commit;

        // Final output to screen uses the full viewport region (with panel offset if applicable).
        // All subsequent commands target the swapchain, keeping them in one contiguous
        // Vulkan render pass so a LoadOp.Clear restart cannot wipe the composited scene.
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                //c.Add<VPRC_ClearByBoundFBO>();
                if (EnableTransformIdVisualization)
                {
                    // Debug visualization is produced by a quad shader; present it directly.
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(TransformIdDebugQuadFBOName, null);
                }
                else if (ActiveTransparencyDebugFboName is not null)
                {
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(ActiveTransparencyDebugFboName, null);
                }
                else
                {
                    string? overrideSource = Environment.GetEnvironmentVariable("XRE_OUTPUT_SOURCE_FBO");
                    if (!string.IsNullOrWhiteSpace(overrideSource))
                    {
                        // Env var override takes absolute precedence (debug tooling).
                        if (bypassVendorUpscale)
                        {
                            c.Add<VPRC_RenderQuadToFBO>().SetTargets(overrideSource, null);
                        }
                        else
                        {
                            var vendorBlit = c.Add<VPRC_VendorUpscale>();
                            vendorBlit.FrameBufferName = overrideSource;
                            vendorBlit.DepthTextureName = DepthViewTextureName;
                            vendorBlit.MotionTextureName = VelocityTextureName;
                        }
                    }
                    else
                    {
                        // Dynamic AA/upscale selection: choose the correct source at render time.
                        // FXAA, SMAA, and TSR each publish a distinct post-AA output FBO; when none
                        // are active, the post-process output goes directly to screen.
                        var upscaleOutputChoice = c.Add<VPRC_IfElse>();
                        upscaleOutputChoice.ConditionEvaluator = () => RuntimeEnableFxaa || RuntimeEnableSmaa || RuntimeNeedsTsrUpscale;
                        {
                            var upscaleOutput = new ViewportRenderCommandContainer(this);
                            var tsrOrPostAaFinal = upscaleOutput.Add<VPRC_IfElse>();
                            tsrOrPostAaFinal.ConditionEvaluator = () => RuntimeNeedsTsrUpscale;
                            tsrOrPostAaFinal.TrueCommands = CreateFinalBlitCommands(TsrUpscaleFBOName, bypassVendorUpscale);
                            {
                                var postAaOutput = new ViewportRenderCommandContainer(this);
                                var fxaaOrSmaaFinal = postAaOutput.Add<VPRC_IfElse>();
                                fxaaOrSmaaFinal.ConditionEvaluator = () => RuntimeEnableFxaa;
                                fxaaOrSmaaFinal.TrueCommands = CreateFinalBlitCommands(FxaaFBOName, bypassVendorUpscale);
                                fxaaOrSmaaFinal.FalseCommands = CreateFinalBlitCommands(SmaaFBOName, bypassVendorUpscale);
                                tsrOrPostAaFinal.FalseCommands = postAaOutput;
                            }
                            upscaleOutputChoice.TrueCommands = upscaleOutput;
                        }
                        upscaleOutputChoice.FalseCommands = CreateFinalBlitCommands(PostProcessFBOName, bypassVendorUpscale);
                    }
                }
            }
        }

        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        c.Add<VPRC_RenderScreenSpaceUI>();
        return c;
    }

    /// <summary>
    /// Builds the final blit command container that presents the given source FBO to the output.
    /// Used by the FXAA/non-FXAA runtime switch so the output source is resolved per-camera.
    /// </summary>
    private ViewportRenderCommandContainer CreateFinalBlitCommands(string sourceFboName, bool bypassVendorUpscale)
    {
        var cmds = new ViewportRenderCommandContainer(this);
        if (bypassVendorUpscale)
        {
            cmds.Add<VPRC_RenderToWindow>().SourceFBOName = sourceFboName;
        }
        else
        {
            var vendorBlit = cmds.Add<VPRC_VendorUpscale>();
            vendorBlit.FrameBufferName = sourceFboName;
            vendorBlit.DepthTextureName = DepthViewTextureName;
            vendorBlit.MotionTextureName = VelocityTextureName;
        }
        return cmds;
    }

    private string TransparentResolveShaderName()
        => Stereo ? "TransparentResolveStereo.fs" : "TransparentResolve.fs";

    private string TransparentAccumulationDebugShaderName()
        => Stereo ? "TransparentAccumulationDebugStereo.fs" : "TransparentAccumulationDebug.fs";

    private string TransparentRevealageDebugShaderName()
        => Stereo ? "TransparentRevealageDebugStereo.fs" : "TransparentRevealageDebug.fs";

    private string TransparentOverdrawDebugShaderName()
        => Stereo ? "TransparentOverdrawDebugStereo.fs" : "TransparentOverdrawDebug.fs";

    private ViewportRenderCommandContainer CreateVendorUpscaleCommands(string sourceFboName)
    {
        var c = new ViewportRenderCommandContainer(this);
        var vendorBlit = c.Add<VPRC_VendorUpscale>();
        vendorBlit.FrameBufferName = sourceFboName;
        vendorBlit.DepthTextureName = DepthViewTextureName;
        vendorBlit.MotionTextureName = VelocityTextureName;
        return c;
    }

    private void CacheTextures(ViewportRenderCommandContainer c)
    {
        //BRDF, for PBR lighting
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            BRDFTextureName,
            CreateBRDFTexture,
            null,
            null);

        //Depth + Stencil GBuffer texture
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthStencilTextureName,
            CreateDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardPrePassDepthStencilTextureName,
            CreateForwardPrePassDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardPassMsaaDepthStencilTextureName,
            CreateForwardPassMsaaDepthStencilTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardPassMsaaDepthViewTextureName,
            CreateForwardPassMsaaDepthViewTexture,
            t => NeedsRecreateTextureView(t, ForwardPassMsaaDepthStencilTextureName),
            t => RetargetTextureView(t, ForwardPassMsaaDepthStencilTextureName));

        //Depth view texture
        //This is a view of the depth/stencil texture that only shows the depth values.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthViewTextureName,
            CreateDepthViewTexture,
            t => NeedsRecreateTextureView(t, DepthStencilTextureName),
            t => RetargetTextureView(t, DepthStencilTextureName));

        //Stencil view texture
        //This is a view of the depth/stencil texture that only shows the stencil values.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            StencilViewTextureName,
            CreateStencilViewTexture,
            t => NeedsRecreateTextureView(t, DepthStencilTextureName),
            t => RetargetTextureView(t, DepthStencilTextureName));

        //History depth + view textures
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HistoryDepthStencilTextureName,
            CreateHistoryDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HistoryDepthViewTextureName,
            CreateHistoryDepthViewTexture,
            t => NeedsRecreateTextureView(t, HistoryDepthStencilTextureName),
            t => RetargetTextureView(t, HistoryDepthStencilTextureName));

        //Albedo/Opacity GBuffer texture
        //RGB = Albedo, A = Opacity
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AlbedoOpacityTextureName,
            CreateAlbedoOpacityTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //Normal GBuffer texture
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            NormalTextureName,
            CreateNormalTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardPrePassNormalTextureName,
            CreateForwardPrePassNormalTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //RMSI GBuffer texture
        //R = Roughness, G = Metallic, B = Specular, A = IOR
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            RMSETextureName,
            CreateRMSETexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //Transform ID GBuffer texture
        //R32UI = per-draw/per-transform identifier
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TransformIdTextureName,
            CreateTransformIdTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        // TransformId visualization is rendered directly via a debug quad.

        // MSAA deferred GBuffer textures (always cached so per-camera AA overrides work at runtime)
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaAlbedoOpacityTextureName,
            CreateMsaaAlbedoOpacityTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaNormalTextureName,
            CreateMsaaNormalTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaRMSETextureName,
            CreateMsaaRMSETexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaDepthStencilTextureName,
            CreateMsaaDepthStencilTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaDepthViewTextureName,
            CreateMsaaDepthViewTexture,
            t => NeedsRecreateTextureView(t, MsaaDepthStencilTextureName),
            t => RetargetTextureView(t, MsaaDepthStencilTextureName));
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaTransformIdTextureName,
            CreateMsaaTransformIdTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaLightingTextureName,
            CreateMsaaLightingTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);

        //SSAO FBO texture, this is created later by the SSAO command
        //c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
        //    AmbientOcclusionIntensityTextureName,
        //    CreateSSAOTexture,
        //    NeedsRecreateTextureInternalSize,
        //    ResizeTextureInternalSize);

        //Lighting texture
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DiffuseTextureName,
            CreateLightingTexture,
            t =>
                NeedsRecreateTextureInternalSize(t) ||
                t is not IFrameBufferAttachement ||
                (Stereo ? t is not XRTexture2DArray : t is not XRTexture2D),
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VelocityTextureName,
            CreateVelocityTexture,
            t =>
                NeedsRecreateTextureInternalSize(t) ||
                t is not IFrameBufferAttachement ||
                (Stereo ? t is not XRTexture2DArray : t is not XRTexture2D),
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HistoryColorTextureName,
            CreateHistoryColorTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TemporalColorInputTextureName,
            CreateTemporalColorInputTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TemporalExposureVarianceTextureName,
            CreateTemporalExposureVarianceTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HistoryExposureVarianceTextureName,
            CreateHistoryExposureVarianceTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MotionBlurTextureName,
            CreateMotionBlurTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthOfFieldTextureName,
            CreateDepthOfFieldTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        // PostProcessOutput is the intermediate target used by the post-process quad before
        // any optional AA/upscale pass. It must exist regardless of the selected AA mode
        // because the matching FBO is created unconditionally later in the command chain.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            PostProcessOutputTextureName,
            CreatePostProcessOutputTexture,
            NeedsRecreateOutputTextureInternalSize,
            ResizeTextureInternalSize);

        if (EnableFxaa)
        {
            // FXAA output is full resolution (FXAA performs the upscale)
            c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                FxaaOutputTextureName,
                CreateFxaaOutputTexture,
                NeedsRecreateOutputTextureFullSize,
                ResizeTextureFullSize);
        }

        //HDR Scene texture
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HDRSceneTextureName,
            CreateHDRSceneTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TransparentSceneCopyTextureName,
            CreateTransparentSceneCopyTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TransparentAccumTextureName,
            CreateTransparentAccumTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TransparentRevealageTextureName,
            CreateTransparentRevealageTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        CacheExactTransparencyTextures(c);

        // 1x1 exposure value texture (GPU auto exposure)
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AutoExposureTextureName,
            CreateAutoExposureTexture,
            null,
            null);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            RestirGITextureName,
            CreateRestirGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            LightVolumeGITextureName,
            CreateLightVolumeGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            RadianceCascadeGITextureName,
            CreateRadianceCascadeGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            SurfelGITextureName,
            CreateSurfelGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VoxelConeTracingVolumeTextureName,
            CreateVoxelConeTracingVolumeTexture,
            NeedsRecreateVoxelVolumeTexture,
            ResizeVoxelVolumeTexture);

        //HDR Scene texture 2
        //c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
        //    HDRSceneTexture2Name,
        //    CreateHDRSceneTexture,
        //    NeedsRecreateTextureInternalSize,
        //    ResizeTextureInternalSize);

        //HUD texture
        //c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
        //    UserInterfaceTextureName,
        //    CreateHUDTexture,
        //    NeedsRecreateTextureFullSize,
        //    ResizeTextureFullSize);
    }

    private ViewportRenderCommandContainer CreateSSAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureSSAOPass(container.Add<VPRC_SSAOPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateAmbientOcclusionDisabledPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureAmbientOcclusionDisabledPass(container.Add<VPRC_AODisabledPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateAmbientOcclusionResolveCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName);
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionBlurFBOName, GBufferFBOName);
        return container;
    }

    private ViewportRenderCommandContainer CreateHBAOPlusResolveCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName);
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionBlurFBOName, HBAOPlusBlurIntermediateFBOName);
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(HBAOPlusBlurIntermediateFBOName, GBufferFBOName);
        return container;
    }

    private ViewportRenderCommandContainer CreateHBAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureHBAOPass(container.Add<VPRC_AODisabledPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateHBAOPlusPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureHBAOPlusPass(container.Add<VPRC_HBAOPlusPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateGTAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureGTAOPass(container.Add<VPRC_GTAOPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateGTAOResolveCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName);
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionBlurFBOName, GTAOBlurIntermediateFBOName);
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(GTAOBlurIntermediateFBOName, GBufferFBOName);
        return container;
    }

    private ViewportRenderCommandContainer CreateVXAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureVXAOPass(container.Add<VPRC_AODisabledPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateMVAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureMVAOPass(container.Add<VPRC_MVAOPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateMSVOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureMSVOPass(container.Add<VPRC_MSVO>());
        return container;
    }

    private ViewportRenderCommandContainer CreateSpatialHashAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureSpatialHashAOPass(container.Add<VPRC_SpatialHashAOPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateMotionBlurPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this);

        container.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            MotionBlurCopyFBOName,
            CreateMotionBlurCopyFBO,
            GetDesiredFBOSizeInternal);

        container.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            MotionBlurFBOName,
            CreateMotionBlurFBO,
            GetDesiredFBOSizeInternal);

        container.Add<VPRC_BlitFrameBuffer>().SetOptions(
            ForwardPassFBOName,
            MotionBlurCopyFBOName,
            EReadBufferMode.ColorAttachment0,
            blitColor: true,
            blitDepth: false,
            blitStencil: false,
            linearFilter: false);

        // Render the motion blur result back into the forward pass FBO
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(MotionBlurFBOName, ForwardPassFBOName);

        return container;
    }

    /// <summary>
    /// Creates the forward pre-pass commands that render into both a dedicated
    /// forward-only FBO and into the shared GBuffer attachments (separate + merge).
    /// </summary>
    private ViewportRenderCommandContainer CreateForwardPrePassSeparateCommands()
    {
        var c = new ViewportRenderCommandContainer(this);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            ForwardDepthPrePassFBOName,
            CreateForwardDepthPrePassFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            ForwardDepthPrePassMergeFBOName,
            CreateForwardDepthPrePassMergeFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardDepthPrePassFBOName)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = true;
            c.Add<VPRC_ForwardDepthNormalPrePass>().SetOptions(
                [(int)EDefaultRenderPass.OpaqueForward, (int)EDefaultRenderPass.MaskedForward],
                GPURenderDispatch);
        }

        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardDepthPrePassMergeFBOName, true, false, false, false)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = true;
            c.Add<VPRC_ForwardDepthNormalPrePass>().SetOptions(
                [(int)EDefaultRenderPass.OpaqueForward, (int)EDefaultRenderPass.MaskedForward],
                GPURenderDispatch);
        }

        return c;
    }

    /// <summary>
    /// Creates the forward pre-pass commands that render directly into the GBuffer
    /// normal and depth attachments, skipping the dedicated forward-only FBO.
    /// </summary>
    private ViewportRenderCommandContainer CreateForwardPrePassSharedCommands()
    {
        var c = new ViewportRenderCommandContainer(this);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            ForwardDepthPrePassMergeFBOName,
            CreateForwardDepthPrePassMergeFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardDepthPrePassMergeFBOName, true, false, false, false)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = true;
            c.Add<VPRC_ForwardDepthNormalPrePass>().SetOptions(
                [(int)EDefaultRenderPass.OpaqueForward, (int)EDefaultRenderPass.MaskedForward],
                GPURenderDispatch);
        }

        return c;
    }

    private ViewportRenderCommandContainer CreateDepthOfFieldPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this);

        container.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            DepthOfFieldCopyFBOName,
            CreateDepthOfFieldCopyFBO,
            GetDesiredFBOSizeInternal);

        container.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            DepthOfFieldFBOName,
            CreateDepthOfFieldFBO,
            GetDesiredFBOSizeInternal);

        container.Add<VPRC_BlitFrameBuffer>().SetOptions(
            ForwardPassFBOName,
            DepthOfFieldCopyFBOName,
            EReadBufferMode.ColorAttachment0,
            blitColor: true,
            blitDepth: false,
            blitStencil: false,
            linearFilter: false);

        // Render the DoF result back into the forward pass FBO
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(DepthOfFieldFBOName, ForwardPassFBOName);

        return container;
    }

    private void ConfigureSSAOPass(VPRC_SSAOPass pass)
    {
        pass.SetOptions(
            VPRC_SSAOPass.DefaultSamples,
            VPRC_SSAOPass.DefaultNoiseWidth,
            VPRC_SSAOPass.DefaultNoiseHeight,
            VPRC_SSAOPass.DefaultMinSampleDist,
            VPRC_SSAOPass.DefaultMaxSampleDist,
            Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName);

        pass.SetOutputNames(
            AmbientOcclusionNoiseTextureName,
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureMVAOPass(VPRC_MVAOPass pass)
    {
        pass.SetOptions(
            VPRC_MVAOPass.DefaultSamples,
            VPRC_MVAOPass.DefaultNoiseWidth,
            VPRC_MVAOPass.DefaultNoiseHeight,
            VPRC_MVAOPass.DefaultMinSampleDist,
            VPRC_MVAOPass.DefaultMaxSampleDist,
            Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName);

        pass.SetOutputNames(
            AmbientOcclusionNoiseTextureName,
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureMSVOPass(VPRC_MSVO pass)
    {
        pass.SetOptions(Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName,
            TransformIdTextureName);

        pass.SetOutputNames(
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureAmbientOcclusionDisabledPass(VPRC_AODisabledPass pass)
    {
        pass.SetOptions(Stereo);
        pass.SetStubInfo(null, null);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName,
            TransformIdTextureName);

        pass.SetOutputNames(
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureHBAOPass(VPRC_AODisabledPass pass)
    {
        ConfigureAmbientOcclusionDisabledPass(pass);
        pass.SetStubInfo(
            "HorizonBased",
            "HorizonBased AO is intentionally deferred in favor of HBAO+. Rendering neutral AO instead of implying that classic HBAO is implemented.");
    }

    private void ConfigureHBAOPlusPass(VPRC_HBAOPlusPass pass)
    {
        pass.SetOptions(Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName,
            TransformIdTextureName);

        pass.SetOutputNames(
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            HBAOPlusBlurIntermediateFBOName,
            GBufferFBOName,
            HBAOPlusRawTextureName,
            HBAOPlusBlurIntermediateTextureName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureGTAOPass(VPRC_GTAOPass pass)
    {
        pass.SetOptions(Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName,
            TransformIdTextureName);

        pass.SetOutputNames(
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GTAOBlurIntermediateFBOName,
            GBufferFBOName,
            GTAORawTextureName,
            GTAOBlurIntermediateTextureName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureVXAOPass(VPRC_AODisabledPass pass)
    {
        ConfigureAmbientOcclusionDisabledPass(pass);
        pass.SetStubInfo(
            "VoxelAmbientOcclusion",
            "VXAO is not implemented yet. This mode is reserved for a future voxelization plus cone-tracing path that will integrate with the existing voxel cone tracing infrastructure.");
    }

    private void ConfigureSpatialHashAOPass(VPRC_SpatialHashAOPass pass)
    {
        pass.SetOptions(
            VPRC_SpatialHashAOPass.DefaultSamples,
            VPRC_SpatialHashAOPass.DefaultNoiseWidth,
            VPRC_SpatialHashAOPass.DefaultNoiseHeight,
            VPRC_SpatialHashAOPass.DefaultMinSampleDist,
            VPRC_SpatialHashAOPass.DefaultMaxSampleDist,
            Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName);

        pass.SetOutputNames(
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private int EvaluateAmbientOcclusionMode()
    {
        AmbientOcclusionSettings? aoSettings = ResolveAmbientOcclusionSettings();
        if (aoSettings is null || !aoSettings.Enabled)
            return AmbientOcclusionDisabledMode;

        return MapAmbientOcclusionMode(aoSettings.Type);
    }

    private static int MapAmbientOcclusionMode(AmbientOcclusionSettings.EType type)
        => AmbientOcclusionSettings.NormalizeType(type) switch
        {
            AmbientOcclusionSettings.EType.ScreenSpace => (int)AmbientOcclusionSettings.EType.ScreenSpace,
            AmbientOcclusionSettings.EType.HorizonBased => (int)AmbientOcclusionSettings.EType.HorizonBased,
            AmbientOcclusionSettings.EType.HorizonBasedPlus => (int)AmbientOcclusionSettings.EType.HorizonBasedPlus,
            AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion => (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion,
            AmbientOcclusionSettings.EType.VoxelAmbientOcclusion => (int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion,
            AmbientOcclusionSettings.EType.MultiViewCustom => (int)AmbientOcclusionSettings.EType.MultiViewCustom,
            AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype => (int)AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype,
            AmbientOcclusionSettings.EType.SpatialHashExperimental => (int)AmbientOcclusionSettings.EType.SpatialHashExperimental,
            _ => (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion,
        };

    #endregion

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
            Debug.RenderingEvery("ProbeGI.Disabled", TimeSpan.FromSeconds(5),
                "[ProbeGI] GI mode disabled (UsesLightProbeGI=false)");
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
            Debug.RenderingEvery("ProbeGI.NoWorld", TimeSpan.FromSeconds(5),
                "[ProbeGI] RenderingWorld is null");
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
            Debug.RenderingEvery("ProbeGI.NoReady", TimeSpan.FromSeconds(5),
                "[ProbeGI] No ready probes. Total={0}, Ready=0 (need IrradianceTexture+PrefilterTexture)", probes.Count);
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
            Debug.RenderingEvery("ProbeGI.NotEnabled", TimeSpan.FromSeconds(5),
                "[ProbeGI] Resources not ready after build. BRDF={0}, IrrArr={1}, PreArr={2}, PosBuffer={3}, ParamBuffer={4}",
                brdfTexture is not null, _probeIrradianceArray is not null,
                _probePrefilterArray is not null, _probePositionBuffer is not null,
                _probeParamBuffer is not null);
            SuppressOptionalProbeSamplers();
            program.Uniform("ProbeCount", 0);
            program.Uniform("TetraCount", 0);
            program.Uniform("UseProbeGrid", false);
            return false;
        }

        Debug.RenderingEvery("ProbeGI.Bound", TimeSpan.FromSeconds(10),
            "[ProbeGI] Probes bound successfully. Ready={0}, ProbeCount={1}",
            readyProbes.Count, (int)_probePositionBuffer!.ElementCount);

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
        {
            Debug.RenderingEvery("AO.V1.Resolve.NoCamera", TimeSpan.FromSeconds(2),
                "[AO][Diag][V1] ResolveAOSettings: camera=null (Scene={0},Rendering={1},LastScene={2},LastRendering={3})",
                State.SceneCamera is null ? "null" : "set",
                State.RenderingCamera is null ? "null" : "set",
                CurrentRenderingPipeline?.LastSceneCamera is null ? "null" : "set",
                CurrentRenderingPipeline?.LastRenderingCamera is null ? "null" : "set");
            return null;
        }

        var stage = camera.GetPostProcessStageState<AmbientOcclusionSettings>();
        if (stage is null)
        {
            var pps = camera.GetActivePostProcessState();
            Debug.RenderingEvery("AO.V1.Resolve.NoStage", TimeSpan.FromSeconds(2),
                "[AO][Diag][V1] ResolveAOSettings: camera found but stage=null (pipelineState={0}, stageCount={1}, schemaEmpty={2})",
                pps is null ? "null" : "exists",
                pps?.Stages.Count ?? -1,
                pps?.Schema.IsEmpty ?? true);
            return null;
        }

        if (!stage.TryGetBacking(out AmbientOcclusionSettings? settings))
        {
            Debug.RenderingEvery("AO.V1.Resolve.NoBacking", TimeSpan.FromSeconds(2),
                "[AO][Diag][V1] ResolveAOSettings: stage found but TryGetBacking failed (backingInstance={0}, backingType={1})",
                stage.BackingInstance?.GetType().Name ?? "null",
                stage.Descriptor?.BackingType?.Name ?? "null");
            return null;
        }

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
