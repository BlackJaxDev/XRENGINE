using XREngine.Extensions;
using System;
using System.Collections;
using System.ComponentModel;
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
    public enum DeferredDebugViewMode
    {
        Disabled = 0,
        RawAlbedo = 1,
        DirectLighting = 2,
        Rmse = 3,
        Normal = 4,
        Depth = 5,
    }

    public const string SceneShaderPath = "Scene3D";

    private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();
    private readonly FarToNearRenderCommandSorter _farToNearSorter = new();

    //TODO: these options below should not be controlled by this render pipeline object, 
    // but rather in branches in the command chain.

    private readonly Lazy<XRMaterial> _voxelConeTracingVoxelizationMaterial;
    private readonly Lazy<XRMaterial> _motionVectorsMaterial;
    private readonly Lazy<XRMaterial> _depthNormalPrePassMaterial;

    private DeferredDebugViewMode _deferredDebugView = DeferredDebugViewMode.Disabled;
    [Category("Debug")]
    [DisplayName("Deferred Debug View")]
    [Description("Overrides DeferredLightCombine output for diagnostics. Disabled = normal shaded output; other modes show raw deferred inputs.")]
    public DeferredDebugViewMode DeferredDebugView
    {
        get => _deferredDebugView;
        set => SetField(ref _deferredDebugView, value);
    }

    private const float TemporalFeedbackMin = 0.16f;
    private const float TemporalFeedbackMax = 0.96f;
    private const float TemporalVarianceGamma = 1.0f;
    private const float TemporalCatmullRadius = 1.0f;
    private const float TemporalDepthRejectThreshold = 0.0075f;
    private static readonly Vector2 TemporalReactiveTransparencyRange = new(0.4f, 0.85f);
    private const float TemporalReactiveVelocityScale = 0.55f;
    private const float TemporalReactiveLumaThreshold = 0.35f;
    private const float TemporalDepthDiscontinuityScale = 140.0f;
    private const float TemporalConfidencePower = 0.55f;

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

    // Keep post-process and temporal AA intermediates in FP16 even when the final
    // presentation target is SDR so nested captures cannot churn TSR resources between
    // RGBA8 and RGBA16F across frames.
    private static EPixelInternalFormat ResolvePostProcessIntermediateInternalFormat()
        => EPixelInternalFormat.Rgba16f;

    private static EPixelType ResolvePostProcessIntermediatePixelType()
        => EPixelType.HalfFloat;

    private static ESizedInternalFormat ResolvePostProcessIntermediateSizedInternalFormat()
        => ESizedInternalFormat.Rgba16f;

    private static bool NeedsRecreateOutputTextureInternalSize(XRTexture texture)
        => NeedsRecreateTextureInternalSize(texture) || !MatchesOutputTextureFormat(texture);

    private static bool NeedsRecreateOutputTextureFullSize(XRTexture texture)
        => NeedsRecreateTextureFullSize(texture) || !MatchesOutputTextureFormat(texture);

    private static bool NeedsRecreatePostProcessTextureInternalSize(XRTexture texture)
        => NeedsRecreateTextureInternalSize(texture) || !MatchesPostProcessIntermediateTextureFormat(texture);

    private static bool NeedsRecreatePostProcessTextureFullSize(XRTexture texture)
        => NeedsRecreateTextureFullSize(texture) || !MatchesPostProcessIntermediateTextureFormat(texture);

    private static bool MatchesTextureFormat(
        XRTexture texture,
        ESizedInternalFormat sizedFormat,
        EPixelInternalFormat internalFormat,
        EPixelType pixelType)
    {
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

    private static bool MatchesOutputTextureFormat(XRTexture texture)
    {
        ESizedInternalFormat sizedFormat = ResolveOutputSizedInternalFormat();
        EPixelInternalFormat internalFormat = ResolveOutputInternalFormat();
        EPixelType pixelType = ResolveOutputPixelType();

        return MatchesTextureFormat(texture, sizedFormat, internalFormat, pixelType);
    }

    private static bool MatchesPostProcessIntermediateTextureFormat(XRTexture texture)
    {
        ESizedInternalFormat sizedFormat = ResolvePostProcessIntermediateSizedInternalFormat();
        EPixelInternalFormat internalFormat = ResolvePostProcessIntermediateInternalFormat();
        EPixelType pixelType = ResolvePostProcessIntermediatePixelType();

        return MatchesTextureFormat(texture, sizedFormat, internalFormat, pixelType);
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

    private static bool NeedsRecreateFboDueToPostProcessIntermediateFormat(XRFrameBuffer fbo)
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

            if (target is XRTexture tex && !MatchesPostProcessIntermediateTextureFormat(tex))
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
        if (!fbo.IsLastCheckComplete || fbo.EffectiveSampleCount != MsaaSampleCount)
            return true;

        return fbo.Name switch
        {
            MsaaGBufferFBOName => !HasMsaaGBufferTargets(fbo),
            MsaaLightingFBOName => !HasMsaaLightingTargets(fbo),
            ForwardPassMsaaFBOName => !HasForwardPassMsaaTargets(fbo),
            _ => NeedsRecreateMsaaFboDueToColorRenderBufferFormat(fbo),
        };
    }

    private bool NeedsRecreateMsaaFboDueToColorRenderBufferFormat(XRFrameBuffer fbo)
    {
        if (fbo.Targets is null)
            return false;

        ERenderBufferStorage expectedColorFormat = fbo.Name == ForwardPassMsaaFBOName
            ? GetForwardMsaaColorFormat()
            : ResolveOutputRenderBufferStorage();

        foreach (var (target, attachment, _, _) in fbo.Targets)
        {
            if (attachment == EFrameBufferAttachment.ColorAttachment0
                && target is XRRenderBuffer renderBuffer
                && renderBuffer.Type != expectedColorFormat)
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

        var targets = fbo.Targets;
        if (targets is null || targets.Length != 1)
            return true;

        var (target, attachment, mipLevel, layerIndex) = targets[0];
        if (!ReferenceEquals(target, GetTexture<XRTexture>(DiffuseTextureName))
            || attachment != EFrameBufferAttachment.ColorAttachment0
            || mipLevel != 0
            || layerIndex != -1)
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

    private bool HasTextureAttachment(
        (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex) target,
        string textureName,
        EFrameBufferAttachment attachment)
        => target.Attachment == attachment
            && target.MipLevel == 0
            && target.LayerIndex == -1
            && ReferenceEquals(target.Target, GetTexture<XRTexture>(textureName));

    private bool HasSingleColorTarget(XRFrameBuffer fbo, string textureName)
    {
        if (fbo.Targets is not { Length: 1 })
            return false;

        return HasTextureAttachment(fbo.Targets[0], textureName, EFrameBufferAttachment.ColorAttachment0);
    }

    private bool HasMsaaGBufferTargets(XRFrameBuffer fbo)
    {
        if (fbo.Targets is not { Length: 5 } targets)
            return false;

        return HasTextureAttachment(targets[0], MsaaAlbedoOpacityTextureName, EFrameBufferAttachment.ColorAttachment0)
            && HasTextureAttachment(targets[1], MsaaNormalTextureName, EFrameBufferAttachment.ColorAttachment1)
            && HasTextureAttachment(targets[2], MsaaRMSETextureName, EFrameBufferAttachment.ColorAttachment2)
            && HasTextureAttachment(targets[3], MsaaTransformIdTextureName, EFrameBufferAttachment.ColorAttachment3)
            && HasTextureAttachment(targets[4], MsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment);
    }

    private bool HasMsaaLightingTargets(XRFrameBuffer fbo)
    {
        if (fbo.Targets is not { Length: 2 } targets)
            return false;

        return HasTextureAttachment(targets[0], MsaaLightingTextureName, EFrameBufferAttachment.ColorAttachment0)
            && HasTextureAttachment(targets[1], MsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment);
    }

    private bool HasForwardPassMsaaTargets(XRFrameBuffer fbo)
    {
        if (fbo.Targets is not { Length: 2 } targets)
            return false;

        var (colorTarget, colorAttachment, mipLevel, layerIndex) = targets[0];
        if (colorAttachment != EFrameBufferAttachment.ColorAttachment0
            || mipLevel != 0
            || layerIndex != -1
            || colorTarget is not XRRenderBuffer renderBuffer
            || renderBuffer.Type != GetForwardMsaaColorFormat())
            return false;

        return HasTextureAttachment(targets[1], ForwardPassMsaaDepthStencilTextureName, EFrameBufferAttachment.DepthStencilAttachment);
    }

    private bool NeedsRecreatePostProcessOutputFbo(XRFrameBuffer fbo)
    {
        if (NeedsRecreateFboDueToPostProcessIntermediateFormat(fbo) || !fbo.IsLastCheckComplete)
            return true;

        return !HasSingleColorTarget(fbo, PostProcessOutputTextureName);
    }

    private bool NeedsRecreateFxaaFbo(XRFrameBuffer fbo)
    {
        if (NeedsRecreateFboDueToPostProcessIntermediateFormat(fbo) || !fbo.IsLastCheckComplete)
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
        if (NeedsRecreateFboDueToPostProcessIntermediateFormat(fbo) || !fbo.IsLastCheckComplete)
            return true;

        return !HasSingleColorTarget(fbo, TsrHistoryColorTextureName);
    }

    private bool NeedsRecreateTsrUpscaleFbo(XRFrameBuffer fbo)
    {
        if (NeedsRecreateFboDueToPostProcessIntermediateFormat(fbo) || !fbo.IsLastCheckComplete)
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
        && (Engine.Rendering.State.CurrentRenderingPipeline?.Pipeline as DefaultRenderPipeline2)?.EnableDeferredMsaa == true;

    private const string TonemappingStageKey = "tonemapping";
    private const string ColorGradingStageKey = "colorGrading";
    private const string VignetteStageKey = "vignette";
    private const string BloomStageKey = "bloom";
    private const string AmbientOcclusionStageKey = "ambientOcclusion";
    private const int AmbientOcclusionDisabledMode = -1;
    private const string TemporalAntiAliasingStageKey = "temporalAntiAliasing";
    private const string MotionBlurStageKey = "motionBlur";
    private const string DepthOfFieldStageKey = "depthOfField";
    private const string LensDistortionStageKey = "lensDistortion";
    private const string ChromaticAberrationStageKey = "chromaticAberration";
    private const string FogStageKey = "fog";
    private const string VolumetricFogStageKey = "volumetricFog";

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

    private static readonly string[] ResizeRecoveryTextureDependencies =
    [
        AmbientOcclusionIntensityTextureName,
        DepthViewTextureName,
        StencilViewTextureName,
        DiffuseTextureName,
        HDRSceneTextureName,
        BloomBlurTextureName,
        AutoExposureTextureName,
        TransparentSceneCopyTextureName,
        TransparentAccumTextureName,
        TransparentRevealageTextureName,
    ];

    private static readonly string[] ResizeRecoveryFrameBufferDependencies =
    [
        // AmbientOcclusionFBO is managed by AO passes (not CacheOrCreateFBO),
        // so it must not be destroyed here — the AO pass owns its lifecycle.
        SceneCopyFBOName,
        TransparentSceneCopyFBOName,
        DeferredTransparencyBlurFBOName,
        TransparentAccumulationFBOName,
        TransparentResolveFBOName,
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
        }, "DefaultRenderPipeline2: Rendering settings changed", true);
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
        }, "DefaultRenderPipeline2: AA settings changed", true);
    }

    private static void InvalidateAntiAliasingResources(XRRenderPipelineInstance instance)
    {
        VPRC_TemporalAccumulationPass.ResetHistory(instance);

        foreach (string name in AntiAliasingFrameBufferDependencies)
            instance.Resources.RemoveFrameBuffer(name);

        foreach (string name in AntiAliasingTextureDependencies)
            instance.Resources.RemoveTexture(name);
    }

    internal void HandleViewportResized(XRRenderPipelineInstance instance, int width, int height)
    {
        if (IsDestroyed || width <= 0 || height <= 0)
            return;

        // Resize callbacks can land after early cache-or-create commands have already run
        // for the current frame. Explicitly evict the post-process/present source chain so
        // the next pass/frame rebuilds from fresh descriptors instead of presenting torn-down
        // attachments from the pre-resize generation.
        const string reason = "ViewportResized";

        VPRC_TemporalAccumulationPass.ResetHistory(instance);

        foreach (string name in AntiAliasingFrameBufferDependencies)
            instance.RemoveFrameBufferResource(name, reason);

        foreach (string name in ResizeRecoveryFrameBufferDependencies)
            instance.RemoveFrameBufferResource(name, reason);

        foreach (string name in AntiAliasingTextureDependencies)
            instance.RemoveTextureResource(name, reason);

        foreach (string name in ResizeRecoveryTextureDependencies)
            instance.RemoveTextureResource(name, reason);

        instance.RenderState.WindowViewport?.Window?.RequestRenderStateRecheck(resetCircuitBreaker: true);
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
    private const ulong ProbeContentRefreshDebounceFrames = 12;
    private int _lastProbeCount = 0;
    private readonly Dictionary<Guid, Vector3> _cachedProbePositions = new();
    private readonly Dictionary<Guid, (XRTexture2D Irradiance, XRTexture2D Prefilter)> _cachedProbeTextures = new();
    private readonly Dictionary<Guid, uint> _observedProbeCaptureVersions = new();
    private ProbePositionData[] _cachedProbePositionData = [];
    private ProbeParamData[] _cachedProbeParamData = [];
    private volatile bool _pendingProbeRefresh;
    private bool _pendingProbeRefreshContentOnly;
    private ulong _probeRefreshEarliestFrameId;
    private readonly List<LightProbeComponent> _cachedReadyProbes = new();
    private ulong _probeBindingStateFrameId = ulong.MaxValue;
    private ulong _probeTetrahedraDebugRenderFrameId = ulong.MaxValue;
    private bool _probeBindingResourcesEnabled;
    private bool _probeBindingUseGrid;
    private int _probeBindingProbeCount;
    private int _probeBindingTetraCount;
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
        program.Uniform("DeferredDebugMode", (int)DeferredDebugView);

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

        UpdatePbrLightingResourcesForFrame(brdfTexture);

        program.Uniform("ForwardPbrResourcesEnabled", _probeBindingResourcesEnabled);
        if (!_probeBindingResourcesEnabled)
        {
            SuppressOptionalProbeSamplers();
            program.Uniform("ProbeCount", 0);
            program.Uniform("TetraCount", 0);
            program.Uniform("UseProbeGrid", false);
            return false;
        }

        program.Sampler("IrradianceArray", _probeIrradianceArray!, 7);
        program.Sampler("PrefilterArray", _probePrefilterArray!, 8);

        program.Uniform("ProbeCount", _probeBindingProbeCount);
        _probePositionBuffer!.BindTo(program, 0);
        _probeParamBuffer!.BindTo(program, 2);
        program.Uniform("UseProbeGrid", _probeBindingUseGrid);

        if (_probeBindingUseGrid)
        {
            _probeGridCellBuffer!.BindTo(program, 3);
            _probeGridIndexBuffer!.BindTo(program, 4);
            program.Uniform("ProbeGridOrigin", _probeGridOrigin);
            program.Uniform("ProbeGridCellSize", _probeGridCellSize);
            program.Uniform("ProbeGridDims", _probeGridDims);
        }

        program.Uniform("TetraCount", _probeBindingTetraCount);
        if (_probeBindingTetraCount > 0)
        {
            _probeTetraBuffer!.BindTo(program, 1);

            ulong frameId = Engine.Rendering.State.RenderFrameId;
            if (Engine.EditorPreferences.Debug.RenderLightProbeTetrahedra
                && _probeTetrahedraDebugRenderFrameId != frameId)
            {
                RenderProbeTetrahedra(_cachedReadyProbes, _probeBindingTetraCount);
                _probeTetrahedraDebugRenderFrameId = frameId;
            }
        }

        return true;
    }

    private void UpdatePbrLightingResourcesForFrame(XRTexture? brdfTexture)
    {
        ulong frameId = Engine.Rendering.State.RenderFrameId;
        if (_probeBindingStateFrameId == frameId)
            return;

        _probeBindingStateFrameId = frameId;
        _probeTetrahedraDebugRenderFrameId = ulong.MaxValue;
        _probeBindingResourcesEnabled = false;
        _probeBindingUseGrid = false;
        _probeBindingProbeCount = 0;
        _probeBindingTetraCount = 0;

        var world = RenderingWorld;
        if (world is null)
            return;

        IReadOnlyList<LightProbeComponent> probes = world.Lights.LightProbes;
        var readyProbes = GetReadyProbes(probes, _cachedReadyProbes);
        if (readyProbes.Count == 0)
        {
            ClearProbeResources();
            return;
        }

        switch (ProbeConfigurationChanged(readyProbes))
        {
            case EProbeRefreshKind.Immediate:
                BuildProbeResources(readyProbes);
                break;
            case EProbeRefreshKind.DeferredContentOnly:
                ScheduleDeferredProbeRefresh();
                break;
        }

        if (_pendingProbeRefresh && frameId >= _probeRefreshEarliestFrameId)
        {
            if (_pendingProbeRefreshContentOnly && _probeIrradianceArray is not null)
                RefreshProbeTextureContent(readyProbes);
            else
                BuildProbeResources(readyProbes);
        }

        _probeBindingResourcesEnabled = brdfTexture is not null
            && _probeIrradianceArray is not null
            && _probePrefilterArray is not null
            && _probePositionBuffer is not null
            && _probeParamBuffer is not null;

        if (!_probeBindingResourcesEnabled)
            return;

        _probeBindingProbeCount = (int)_probePositionBuffer!.ElementCount;
        _probeBindingUseGrid = _useProbeGridAcceleration && _probeGridCellBuffer is not null && _probeGridIndexBuffer is not null;
        _probeBindingTetraCount = _probeTetraBuffer != null && _probeTetraProbeCount == readyProbes.Count
            ? (int)_probeTetraBuffer.ElementCount
            : 0;
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

    private void BuildProbeGrid(IReadOnlyList<ProbePositionData> positions, IReadOnlyList<ProbeParamData> parameters, IReadOnlyList<ProbeTetraData>? tetraData = null)
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
        for (int i = 0; i < positions.Count; ++i)
        {
            GetProbeInfluenceBounds(positions[i], parameters[i], out Vector3 probeMin, out Vector3 probeMax);
            min = Vector3.Min(min, probeMin);
            max = Vector3.Max(max, probeMax);
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

        if (tetraData is { Count: > 0 })
        {
            float cellPadding = _probeGridCellSize * 0.5f;
            Vector3 tetraPadding = new(cellPadding);

            for (int tetraIndex = 0; tetraIndex < tetraData.Count; ++tetraIndex)
            {
                if (!TryGetTetraBounds(tetraData[tetraIndex], positions, out Vector3 tetraMin, out Vector3 tetraMax))
                    continue;

                GetProbeGridCellRange(tetraMin - tetraPadding, tetraMax + tetraPadding, dimsI, out IVector3 minCell, out IVector3 maxCell);
                for (int z = minCell.Z; z <= maxCell.Z; ++z)
                {
                    for (int y = minCell.Y; y <= maxCell.Y; ++y)
                    {
                        for (int x = minCell.X; x <= maxCell.X; ++x)
                        {
                            int flat = x + y * dimsI.X + z * dimsI.X * dimsI.Y;
                            cellLists[flat].Add(tetraIndex);
                        }
                    }
                }
            }
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
            List<int>? preferredIndices = CollectPreferredProbeIndices(list, tetraData, positions.Count);
            IVector4 fallbackIndices = ComputeProbeGridFallbackIndices(cellCenter, positions, preferredIndices);

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

    private static void GetProbeInfluenceBounds(ProbePositionData position, ProbeParamData parameters, out Vector3 min, out Vector3 max)
    {
        Vector4 pos4 = position.Position;
        Vector4 offset4 = parameters.InfluenceOffsetShape;
        Vector3 center = new(pos4.X + offset4.X, pos4.Y + offset4.Y, pos4.Z + offset4.Z);
        Vector4 outer4 = parameters.InfluenceOuter;
        Vector3 outerExtents = offset4.W >= 0.5f
            ? new Vector3(
                MathF.Max(outer4.X, 0.0001f),
                MathF.Max(outer4.Y, 0.0001f),
                MathF.Max(outer4.Z, 0.0001f))
            : new Vector3(MathF.Max(outer4.W, 0.0001f));
        min = center - outerExtents;
        max = center + outerExtents;
    }

    private void GetProbeGridCellRange(ProbePositionData position, ProbeParamData parameters, IVector3 dims, out IVector3 minCell, out IVector3 maxCell)
    {
        GetProbeInfluenceBounds(position, parameters, out Vector3 minWorld, out Vector3 maxWorld);
        GetProbeGridCellRange(minWorld, maxWorld, dims, out minCell, out maxCell);
    }

    private void GetProbeGridCellRange(Vector3 minWorld, Vector3 maxWorld, IVector3 dims, out IVector3 minCell, out IVector3 maxCell)
    {
        Vector3 minRel = (minWorld - _probeGridOrigin) / _probeGridCellSize;
        Vector3 maxRel = (maxWorld - _probeGridOrigin) / _probeGridCellSize;
        minCell = new IVector3(
            Math.Clamp((int)MathF.Floor(minRel.X), 0, dims.X - 1),
            Math.Clamp((int)MathF.Floor(minRel.Y), 0, dims.Y - 1),
            Math.Clamp((int)MathF.Floor(minRel.Z), 0, dims.Z - 1));
        maxCell = new IVector3(
            Math.Clamp((int)MathF.Floor(maxRel.X), 0, dims.X - 1),
            Math.Clamp((int)MathF.Floor(maxRel.Y), 0, dims.Y - 1),
            Math.Clamp((int)MathF.Floor(maxRel.Z), 0, dims.Z - 1));
    }

    private static bool TryGetTetraBounds(ProbeTetraData tetra, IReadOnlyList<ProbePositionData> positions, out Vector3 min, out Vector3 max)
    {
        Vector4 indices = tetra.Indices;
        int index0 = (int)indices.X;
        int index1 = (int)indices.Y;
        int index2 = (int)indices.Z;
        int index3 = (int)indices.W;
        if ((uint)index0 >= positions.Count ||
            (uint)index1 >= positions.Count ||
            (uint)index2 >= positions.Count ||
            (uint)index3 >= positions.Count)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
            return false;
        }

        Vector4 position0 = positions[index0].Position;
        Vector4 position1 = positions[index1].Position;
        Vector4 position2 = positions[index2].Position;
        Vector4 position3 = positions[index3].Position;

        Vector3 p0 = new(position0.X, position0.Y, position0.Z);
        Vector3 p1 = new(position1.X, position1.Y, position1.Z);
        Vector3 p2 = new(position2.X, position2.Y, position2.Z);
        Vector3 p3 = new(position3.X, position3.Y, position3.Z);

        min = Vector3.Min(Vector3.Min(p0, p1), Vector3.Min(p2, p3));
        max = Vector3.Max(Vector3.Max(p0, p1), Vector3.Max(p2, p3));
        return true;
    }

    private static List<int>? CollectPreferredProbeIndices(List<int> tetraIndices, IReadOnlyList<ProbeTetraData>? tetraData, int probeCount)
    {
        if (tetraData is null || tetraIndices.Count == 0 || probeCount <= 0)
            return null;

        var preferred = new List<int>(Math.Min(probeCount, tetraIndices.Count * 4));
        var seen = new HashSet<int>();
        foreach (int tetraIndex in tetraIndices)
        {
            if ((uint)tetraIndex >= tetraData.Count)
                continue;

            Vector4 probeIndices = tetraData[tetraIndex].Indices;
            AddPreferredProbeIndex((int)probeIndices.X, probeCount, seen, preferred);
            AddPreferredProbeIndex((int)probeIndices.Y, probeCount, seen, preferred);
            AddPreferredProbeIndex((int)probeIndices.Z, probeCount, seen, preferred);
            AddPreferredProbeIndex((int)probeIndices.W, probeCount, seen, preferred);
        }

        return preferred.Count > 0 ? preferred : null;
    }

    private static void AddPreferredProbeIndex(int probeIndex, int probeCount, HashSet<int> seen, List<int> preferred)
    {
        if ((uint)probeIndex >= probeCount || !seen.Add(probeIndex))
            return;

        preferred.Add(probeIndex);
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

    private static List<LightProbeComponent> GetReadyProbes(IReadOnlyList<LightProbeComponent> probes, List<LightProbeComponent> target)
    {
        target.Clear();
        foreach (var probe in probes)
        {
            if (probe.IrradianceTexture != null && probe.PrefilterTexture != null)
                target.Add(probe);
        }

        return target;
    }

    private enum EProbeRefreshKind : byte
    {
        None,
        Immediate,
        DeferredContentOnly,
    }

    private void ScheduleDeferredProbeRefresh()
    {
        _pendingProbeRefresh = true;
        _pendingProbeRefreshContentOnly = true;
        _probeRefreshEarliestFrameId = Engine.Rendering.State.RenderFrameId + ProbeContentRefreshDebounceFrames;
    }

    private EProbeRefreshKind ProbeConfigurationChanged(IReadOnlyList<LightProbeComponent> readyProbes)
    {
        if (_lastProbeCount != readyProbes.Count)
        {
            return EProbeRefreshKind.Immediate;
        }

        if (_cachedProbePositions.Count != readyProbes.Count || _cachedProbeTextures.Count != readyProbes.Count)
        {
            return EProbeRefreshKind.Immediate;
        }

        bool contentOnlyChanged = false;

        foreach (var probe in readyProbes)
        {
            var position = probe.Transform.RenderTranslation;
            if (!_cachedProbePositions.TryGetValue(probe.ID, out var cachedPos) || cachedPos != position)
            {
                return EProbeRefreshKind.Immediate;
            }

            if (!_cachedProbeTextures.TryGetValue(probe.ID, out var cachedTex)
                || cachedTex.Irradiance != probe.IrradianceTexture
                || cachedTex.Prefilter != probe.PrefilterTexture)
            {
                return EProbeRefreshKind.Immediate;
            }

            if (!_observedProbeCaptureVersions.TryGetValue(probe.ID, out var observedVersion)
                || observedVersion != probe.CaptureVersion)
            {
                _observedProbeCaptureVersions[probe.ID] = probe.CaptureVersion;
                contentOnlyChanged = true;
            }
        }

        return contentOnlyChanged ? EProbeRefreshKind.DeferredContentOnly : EProbeRefreshKind.None;
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
        _observedProbeCaptureVersions.Clear();
        _cachedProbePositionData = [];
        _cachedProbeParamData = [];
        _lastProbeCount = 0;
        _pendingProbeRefresh = false;
        _pendingProbeRefreshContentOnly = false;
        _probeRefreshEarliestFrameId = 0;
        _probeBindingStateFrameId = ulong.MaxValue;
        _probeTetrahedraDebugRenderFrameId = ulong.MaxValue;
        _probeBindingResourcesEnabled = false;
        _probeBindingUseGrid = false;
        _probeBindingProbeCount = 0;
        _probeBindingTetraCount = 0;
    }

    /// <summary>
    /// Fast path for when only probe texture content has changed (e.g. a probe re-captured).
    /// Re-copies all source texture layers into the existing texture arrays on the GPU
    /// without destroying/recreating any GPU resources or rebuilding the grid.
    /// </summary>
    private void RefreshProbeTextureContent(IList<LightProbeComponent> readyProbes)
    {
        _probeIrradianceArray?.PushData();
        _probePrefilterArray?.PushData();

        foreach (var probe in readyProbes)
            _observedProbeCaptureVersions[probe.ID] = probe.CaptureVersion;

        _pendingProbeRefresh = false;
        _pendingProbeRefreshContentOnly = false;
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
            _observedProbeCaptureVersions[probe.ID] = probe.CaptureVersion;
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

        _cachedProbePositionData = [.. positions];
        _cachedProbeParamData = [.. parameters];

        if (_useProbeGridAcceleration)
            BuildProbeGrid(_cachedProbePositionData, _cachedProbeParamData, null);

        _lastProbeCount = positions.Count;
        _pendingProbeRefresh = false;
        _pendingProbeRefreshContentOnly = false;

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
            if (_useProbeGridAcceleration && _cachedProbePositionData.Length == probeCount && _cachedProbeParamData.Length == probeCount)
                BuildProbeGrid(_cachedProbePositionData, _cachedProbeParamData, null);
            return;
        }

        List<ProbeTetraData> tetraList = tetraData as List<ProbeTetraData> ?? [.. tetraData];

        _probeTetraBuffer = new XRDataBuffer("LightProbeTetra", EBufferTarget.ShaderStorageBuffer, (uint)tetraList.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeTetraData>(), false, false)
        {
            BindingIndexOverride = 1,
        };
        _probeTetraBuffer.SetDataRaw(tetraList);
        _probeTetraBuffer.PushData();
        _probeTetraProbeCount = probeCount;

        if (_useProbeGridAcceleration && _cachedProbePositionData.Length == probeCount && _cachedProbeParamData.Length == probeCount)
            BuildProbeGrid(_cachedProbePositionData, _cachedProbeParamData, tetraList);
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
