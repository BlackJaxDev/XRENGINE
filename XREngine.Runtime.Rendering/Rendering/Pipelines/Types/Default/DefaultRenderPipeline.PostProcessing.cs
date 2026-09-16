using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;
using static XREngine.RuntimeEngine.Rendering.State;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    private static readonly Vector3 DefaultHoverOutlineColor = new(1.0f, 1.0f, 0.0f);
    private static readonly Vector3 DefaultSelectionOutlineColor = new(0.0f, 1.0f, 0.0f);
    private static readonly VignetteSettings DefaultVignetteSettings = new();
    private static readonly ColorGradingSettings DefaultColorGradingSettings = new();
    private static readonly ChromaticAberrationSettings DefaultChromaticAberrationSettings = new();
    private static readonly FogSettings DefaultFogSettings = new();
    private static readonly VolumetricFogSettings DefaultVolumetricFogSettings = new();
    private static readonly LensDistortionSettings DefaultLensDistortionSettings = new();
    private static readonly BloomSettings DefaultBloomSettings = new();
    private static readonly TonemappingSettings DefaultTonemappingSettings = new();

    private static readonly string[] DefaultPipelinePreferredPreviewTextureNames =
    [
        FinalPostProcessOutputTextureName,
        PostProcessOutputTextureName,
        HDRSceneTextureName,
        DiffuseTextureName
    ];

    private static readonly string[] DefaultPipelinePreferredPreviewFrameBufferNames =
    [
        FinalPostProcessOutputFBOName,
        PostProcessOutputFBOName,
        ForwardPassFBOName,
        LightCombineFBOName
    ];

    public override IReadOnlyList<string> PreferredPreviewTextureNames => DefaultPipelinePreferredPreviewTextureNames;
    public override IReadOnlyList<string> PreferredPreviewFrameBufferNames => DefaultPipelinePreferredPreviewFrameBufferNames;

    private enum EPostProcessBindingPublication
    {
        Composite,
        Final,
    }

    /// <summary>
    /// Publishes numeric post-process state with a generation derived from the
    /// exact settings owners and camera values consumed by the pass. Texture
    /// inputs remain material-owned and use the material resource generation.
    /// </summary>
    private sealed class PostProcessBindingPublisher(
        DefaultRenderPipeline owner,
        EPostProcessBindingPublication publication) : IRenderBindingPublisher
    {
        private const ulong HashOffset = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;
        private readonly object _generationSync = new();
        private ulong _lastContentSignature;
        private long _generation = 1;

        public ERenderBindingFrequency Frequency
            => ERenderBindingFrequency.View;

        public ulong Generation
        {
            get
            {
                ulong contentSignature = owner.ComputePostProcessBindingSignature(
                    publication);
                lock (_generationSync)
                {
                    if (contentSignature == _lastContentSignature)
                        return unchecked((ulong)_generation);

                    _lastContentSignature = contentSignature;
                    if (Interlocked.Increment(ref _generation) == 0)
                        Interlocked.CompareExchange(ref _generation, 1, 0);
                    return unchecked((ulong)_generation);
                }
            }
        }

        public void PublishUniforms(
            XRRenderProgram vertexProgram,
            XRRenderProgram materialProgram)
        {
            if (publication == EPostProcessBindingPublication.Composite)
                owner.PostProcessFBO_SettingUniforms(materialProgram);
            else
                owner.FinalPostProcessFBO_SettingUniforms(materialProgram);
        }

        internal static void Add(ref ulong hash, ulong value)
        {
            hash ^= value;
            hash *= HashPrime;
        }

        internal static ulong BeginHash()
            => HashOffset;
    }

    private const string TemporalFeedbackMinParameterName = PostProcessParameterNames.TemporalFeedbackMin;
    private const string TemporalFeedbackMaxParameterName = PostProcessParameterNames.TemporalFeedbackMax;
    private const string TemporalVarianceGammaParameterName = PostProcessParameterNames.TemporalVarianceGamma;
    private const string TemporalCatmullRadiusParameterName = PostProcessParameterNames.TemporalCatmullRadius;
    private const string TemporalDepthRejectThresholdParameterName = PostProcessParameterNames.TemporalDepthRejectThreshold;
    private const string TemporalReactiveTransparencyRangeParameterName = PostProcessParameterNames.TemporalReactiveTransparencyRange;
    private const string TemporalReactiveVelocityScaleParameterName = PostProcessParameterNames.TemporalReactiveVelocityScale;
    private const string TemporalReactiveLumaThresholdParameterName = PostProcessParameterNames.TemporalReactiveLumaThreshold;
    private const string TemporalDepthDiscontinuityScaleParameterName = PostProcessParameterNames.TemporalDepthDiscontinuityScale;
    private const string TemporalConfidencePowerParameterName = PostProcessParameterNames.TemporalConfidencePower;
    private const string TemporalDebugViewModeParameterName = PostProcessParameterNames.TemporalDebugViewMode;

    private readonly struct TemporalResolveSettings
    {
        public float FeedbackMin { get; init; }
        public float FeedbackMax { get; init; }
        public float VarianceGamma { get; init; }
        public float CatmullRadius { get; init; }
        public float DepthRejectThreshold { get; init; }
        public Vector2 ReactiveTransparencyRange { get; init; }
        public float ReactiveVelocityScale { get; init; }
        public float ReactiveLumaThreshold { get; init; }
        public float DepthDiscontinuityScale { get; init; }
        public float ConfidencePower { get; init; }
        public TemporalDebugViewMode DebugMode { get; init; }
    }

    protected override void DescribePostProcessSchema(RenderPipelinePostProcessSchemaBuilder builder)
    {
        CommonPostProcessStages.AddStandardPipelineSchema(builder);
    }
    private static MotionBlurSettings? GetMotionBlurSettings()
    {
        var stage = ResolveCurrentSettingsCamera()?.GetPostProcessStageState<MotionBlurSettings>();
        return stage?.TryGetBacking(out MotionBlurSettings? settings) == true ? settings : null;
    }

    private static bool DisableHistoryBasedVrEffects()
        => !VPRC_TemporalAccumulationPass.TryUseHistoryBasedVrEffects(out _, out _);

    private static bool ShouldUseMotionBlur()
        => !DefaultRenderPipeline.UseOpenXrVulkanDesktopStartupSafePath
        && !IsLightProbePass
        && !RuntimeEngine.Rendering.State.IsSceneCapturePass
        && GetMotionBlurSettings() is { Enabled: true };

    private static DepthOfFieldSettings? GetDepthOfFieldSettings()
    {
        var stage = ResolveCurrentSettingsCamera()?.GetPostProcessStageState<DepthOfFieldSettings>();
        return stage?.TryGetBacking(out DepthOfFieldSettings? settings) == true ? settings : null;
    }

    private static BloomSettings? GetBloomSettings()
    {
        var stage = ResolveCurrentSettingsCamera()?.GetPostProcessStageState<BloomSettings>();
        return stage?.TryGetBacking(out BloomSettings? settings) == true ? settings : null;
    }

    private static bool ShouldUseDepthOfField()
        => !DefaultRenderPipeline.UseOpenXrVulkanDesktopStartupSafePath
        && !IsLightProbePass
        && !RuntimeEngine.Rendering.State.IsSceneCapturePass
        && GetDepthOfFieldSettings() is { Enabled: true };

    private static bool ShouldUseBloom()
    {
        bool safePath = DefaultRenderPipeline.UseOpenXrVulkanDesktopStartupSafePath;
        bool lightProbe = IsLightProbePass;
        bool sceneCapture = RuntimeEngine.Rendering.State.IsSceneCapturePass;
        bool settingsDisabled = GetBloomSettings() is { Enabled: false };
        bool result = !safePath && !lightProbe && !sceneCapture && !settingsDisabled;

        if (RenderDiagnosticsFlags.DiagPostProcess)
        {
            Debug.RenderingEvery(
                $"Bloom.ShouldUse.{result}.{safePath}.{sceneCapture}",
                TimeSpan.FromSeconds(1),
                "[BloomDiag] ShouldUseBloom={0} safePath={1} lightProbe={2} sceneCapture={3} settingsDisabled={4} vp='{5}' external={6}",
                result,
                safePath,
                lightProbe,
                sceneCapture,
                settingsDisabled,
                RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport?.Index.ToString() ?? "<null>",
                RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport?.RendersToExternalSwapchainTarget.ToString() ?? "<null>");
        }

        return result;
    }

    private ulong ComputePostProcessBindingSignature(
        EPostProcessBindingPublication publication)
    {
        ulong hash = PostProcessBindingPublisher.BeginHash();
        XRCamera? camera = ResolveCurrentSettingsCamera();
        PipelinePostProcessState? state = camera?.GetActivePostProcessState();

        AddPostProcessSettings(
            ref hash,
            state,
            DefaultLensDistortionSettings);
        AddHash(ref hash, InternalWidth);
        AddHash(ref hash, InternalHeight);
        AddCameraLensInputs(ref hash, camera);

        if (publication == EPostProcessBindingPublication.Composite)
        {
            AddPostProcessSettings(ref hash, state, DefaultVignetteSettings);
            AddPostProcessSettings(
                ref hash,
                state,
                DefaultColorGradingSettings);
            AddPostProcessSettings(
                ref hash,
                state,
                DefaultChromaticAberrationSettings);
            AddPostProcessSettings(ref hash, state, DefaultFogSettings);
            AddPostProcessSettings(
                ref hash,
                state,
                AtmosphericScatteringSettings.Default);
            AddPostProcessSettings(
                ref hash,
                state,
                DefaultVolumetricFogSettings);
            AddPostProcessSettings(ref hash, state, DefaultBloomSettings);
            AddPostProcessSettings(
                ref hash,
                state,
                DefaultTonemappingSettings);
            AddHash(ref hash, ResolveOutputHDR());
            AddHash(
                ref hash,
                RuntimeEngine.Rendering.Settings.DefaultLuminance);

            var preferences = RuntimeEngine.EditorPreferences;
            Vector3 hoverOutlineColor = preferences is null
                ? DefaultHoverOutlineColor
                : new Vector3(
                    (float)preferences.HoverOutlineColor.R,
                    (float)preferences.HoverOutlineColor.G,
                    (float)preferences.HoverOutlineColor.B);
            Vector3 selectionOutlineColor = preferences is null
                ? DefaultSelectionOutlineColor
                : new Vector3(
                    (float)preferences.SelectionOutlineColor.R,
                    (float)preferences.SelectionOutlineColor.G,
                    (float)preferences.SelectionOutlineColor.B);
            AddHash(ref hash, hoverOutlineColor);
            AddHash(ref hash, selectionOutlineColor);
            AddHash(
                ref hash,
                RuntimeRenderingHostServices.DebugDrawing
                    .HoverOutlineEnabled);
            AddHash(
                ref hash,
                RuntimeRenderingHostServices.DebugDrawing
                    .SelectionOutlineEnabled);
        }

        return hash == 0UL ? 1UL : hash;
    }

    private static void AddPostProcessSettings<TSettings>(
        ref ulong hash,
        PipelinePostProcessState? state,
        TSettings fallback)
        where TSettings : PostProcessSettings
    {
        TSettings settings = GetSettings<TSettings>(state) ?? fallback;
        PostProcessBindingPublisher.Add(
            ref hash,
            unchecked((uint)RuntimeHelpers.GetHashCode(settings)));
        PostProcessBindingPublisher.Add(
            ref hash,
            settings.BindingGeneration);
    }

    private static void AddCameraLensInputs(
        ref ulong hash,
        XRCamera? camera)
    {
        object? cameraParameters = camera?.Parameters;
        PostProcessBindingPublisher.Add(
            ref hash,
            cameraParameters is null
                ? 0UL
                : unchecked((uint)RuntimeHelpers.GetHashCode(cameraParameters)));

        switch (cameraParameters)
        {
            case XRPerspectiveCameraParameters perspective:
                AddHash(ref hash, perspective.VerticalFieldOfView);
                AddHash(ref hash, perspective.InheritAspectRatio);
                AddHash(ref hash, perspective.AspectRatio);
                break;
            case XRPhysicalCameraParameters physical:
                AddHash(ref hash, physical.VerticalFieldOfViewDegrees);
                AddHash(ref hash, physical.InheritPrincipalPoint);
                AddHash(ref hash, physical.PrincipalPointPx);
                break;
        }
    }

    private static void AddHash(ref ulong hash, bool value)
        => PostProcessBindingPublisher.Add(ref hash, value ? 1UL : 0UL);

    private static void AddHash(ref ulong hash, int value)
        => PostProcessBindingPublisher.Add(ref hash, unchecked((uint)value));

    private static void AddHash(ref ulong hash, uint value)
        => PostProcessBindingPublisher.Add(ref hash, value);

    private static void AddHash(ref ulong hash, float value)
        => PostProcessBindingPublisher.Add(
            ref hash,
            BitConverter.SingleToUInt32Bits(value));

    private static void AddHash(ref ulong hash, Vector2 value)
    {
        AddHash(ref hash, value.X);
        AddHash(ref hash, value.Y);
    }

    private static void AddHash(ref ulong hash, Vector3 value)
    {
        AddHash(ref hash, value.X);
        AddHash(ref hash, value.Y);
        AddHash(ref hash, value.Z);
    }

    private static TSettings? GetSettings<TSettings>(PipelinePostProcessState? state) where TSettings : class
        => state?.GetStage<TSettings>()?.TryGetBacking(out TSettings? settings) == true ? settings : null;

    private static void ApplyBloomBrightPassUniforms(PipelinePostProcessState? state, XRRenderProgram program)
    {
        var settings = GetSettings<BloomSettings>(state);
        if (settings is not null)
        {
            settings.SetBrightPassUniforms(program);
            return;
        }

        program.Uniform("BloomIntensity", 0.530f);
        program.Uniform("BloomThreshold", 0.138f);
        program.Uniform("SoftKnee", 0.5f);
        program.Uniform("Luminance", RuntimeEngine.Rendering.Settings.DefaultLuminance);
    }

    private static void ApplyPostProcessUniforms(PipelinePostProcessState? state, XRRenderProgram program, bool applyLensDistortion)
    {
        var vignette = GetSettings<VignetteSettings>(state);
        (vignette ?? DefaultVignetteSettings).SetUniforms(program);

        var color = GetSettings<ColorGradingSettings>(state);
        (color ?? DefaultColorGradingSettings).SetUniforms(program);

        var chroma = GetSettings<ChromaticAberrationSettings>(state);
        (chroma ?? DefaultChromaticAberrationSettings).SetUniforms(program);

        var fog = GetSettings<FogSettings>(state);
        (fog ?? DefaultFogSettings).SetUniforms(program);

        var atmosphere = GetSettings<AtmosphericScatteringSettings>(state);
        (atmosphere ?? AtmosphericScatteringSettings.Default).SetUniforms(program);

        var volumetricFog = GetSettings<VolumetricFogSettings>(state);
        (volumetricFog ?? DefaultVolumetricFogSettings).SetUniforms(program);

        ApplyLensDistortionUniforms(state, program, applyLensDistortion);

        var bloom = GetSettings<BloomSettings>(state);
        (bloom ?? DefaultBloomSettings).SetCombineUniforms(program);

        var tonemapping = GetSettings<TonemappingSettings>(state);
        (tonemapping ?? DefaultTonemappingSettings).SetUniforms(program);
    }

    private static void ApplyLensDistortionUniforms(PipelinePostProcessState? state, XRRenderProgram program, bool enabled)
    {
        var lens = GetSettings<LensDistortionSettings>(state);
        float widthPx = Math.Max(1, InternalWidth);
        float heightPx = Math.Max(1, InternalHeight);
        float fallbackAspectRatio = (float)widthPx / heightPx;
        float? cameraFov = null;
        float aspectRatio = fallbackAspectRatio;
        Vector2 distortionCenterUv = LensDistortionSettings.DefaultDistortionCenterUv;

        var cameraParams = ResolveCurrentSettingsCamera()?.Parameters;
        switch (cameraParams)
        {
            case XRPerspectiveCameraParameters perspParams:
                cameraFov = perspParams.VerticalFieldOfView;
                aspectRatio = perspParams.InheritAspectRatio ? fallbackAspectRatio : perspParams.AspectRatio;
                break;
            case XRPhysicalCameraParameters physicalParams:
                cameraFov = physicalParams.VerticalFieldOfViewDegrees;
                aspectRatio = fallbackAspectRatio;

                if (!physicalParams.InheritPrincipalPoint)
                {
                    distortionCenterUv = new Vector2(
                        physicalParams.PrincipalPointPx.X / widthPx,
                        physicalParams.PrincipalPointPx.Y / heightPx);
                }
                break;
        }

        if (enabled)
        {
            (lens ?? DefaultLensDistortionSettings).SetUniforms(program, cameraFov, aspectRatio, distortionCenterUv);
            return;
        }

        program.Uniform("LensDistortionMode", (int)ELensDistortionMode.None);
        program.Uniform("LensDistortionCenter", distortionCenterUv);
        program.Uniform("LensDistortionIntensity", 0.0f);
        program.Uniform("PaniniDistance", 0.0f);
        program.Uniform("PaniniCrop", 1.0f);
        program.Uniform("PaniniViewExtents", Vector2.One);
        program.Uniform("BrownConradyRadial", Vector3.Zero);
        program.Uniform("BrownConradyTangential", Vector2.Zero);
    }

    private void PostProcessFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        materialProgram.Uniform("OutputHDR", ResolveOutputHDR());

        var prefs = RuntimeEngine.EditorPreferences;
        var hoverOutlineColor = prefs is null
            ? DefaultHoverOutlineColor
            : new Vector3((float)prefs.HoverOutlineColor.R, (float)prefs.HoverOutlineColor.G, (float)prefs.HoverOutlineColor.B);
        var selectionOutlineColor = prefs is null
            ? DefaultSelectionOutlineColor
            : new Vector3((float)prefs.SelectionOutlineColor.R, (float)prefs.SelectionOutlineColor.G, (float)prefs.SelectionOutlineColor.B);
        bool enableEditorOutline = RuntimeRenderingHostServices.DebugDrawing.HoverOutlineEnabled ||
            RuntimeRenderingHostServices.DebugDrawing.SelectionOutlineEnabled;
        materialProgram.Uniform("HoverOutlineColor", hoverOutlineColor);
        materialProgram.Uniform("SelectionOutlineColor", selectionOutlineColor);
        materialProgram.Uniform("EnableEditorOutline", enableEditorOutline);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        ApplyPostProcessUniforms(state, materialProgram, applyLensDistortion: false);

        if (RenderDiagnosticsFlags.DiagPostProcess)
            LogPostProcessUniformDiagnostics(state, ResolveOutputHDR(), hoverOutlineColor, selectionOutlineColor, enableEditorOutline);
    }

    private void FinalPostProcessFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        ApplyLensDistortionUniforms(state, materialProgram, enabled: true);
    }

    private void LogPostProcessUniformDiagnostics(
        PipelinePostProcessState? state,
        bool outputHdr,
        Vector3 hoverOutlineColor,
        Vector3 selectionOutlineColor,
        bool enableEditorOutline)
    {
        var color = GetSettings<ColorGradingSettings>(state) ?? new ColorGradingSettings();
        var bloom = GetSettings<BloomSettings>(state) ?? new BloomSettings();
        var tonemap = GetSettings<TonemappingSettings>(state) ?? new TonemappingSettings();
        var fog = GetSettings<FogSettings>(state) ?? new FogSettings();

        string TexLabel(string textureName)
        {
            XRTexture? texture = GetTexture<XRTexture>(textureName);
            if (texture is null)
                return $"{textureName}=<null>";

            string name = string.IsNullOrWhiteSpace(texture.Name) ? texture.GetType().Name : texture.Name!;
            return $"{textureName}={name}#{texture.GetHashCode():X8}";
        }

        Debug.RenderingEvery(
            $"PostProcess.Uniforms.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[PostProcessDiag] OutputHDR={0} Tint=({1:0.###},{2:0.###},{3:0.###}) Exposure={4:0.###} UseGpuAutoExposure={5} Contrast={6:0.###} Gamma={7:0.###} Hue={8:0.###} Saturation={9:0.###} Brightness={10:0.###} Tonemap={11} Mobius={12:0.###} BloomEnabled={13} BloomStrength={14:0.###} BloomMips={15}-{16} DebugBloomOnly={17} FogIntensity={18:0.###} Outline={19} Hover=({20:0.###},{21:0.###},{22:0.###}) Selection=({23:0.###},{24:0.###},{25:0.###}) Textures=[{26}; {27}; {28}; {29}; {30}; {31}; {32}]",
            outputHdr,
            color.Tint.R,
            color.Tint.G,
            color.Tint.B,
            color.Exposure,
            color.UseGpuAutoExposureThisFrame,
            color.Contrast,
            color.Gamma,
            color.Hue,
            color.Saturation,
            color.Brightness,
            tonemap.Tonemapping,
            tonemap.MobiusTransition,
            bloom.Enabled,
            bloom.Strength,
            bloom.StartMip,
            bloom.EndMip,
            bloom.DebugBloomOnly,
            fog.DepthFogIntensity,
            enableEditorOutline,
            hoverOutlineColor.X,
            hoverOutlineColor.Y,
            hoverOutlineColor.Z,
            selectionOutlineColor.X,
            selectionOutlineColor.Y,
            selectionOutlineColor.Z,
            TexLabel(HDRSceneTextureName),
            TexLabel(BloomBlurTextureName),
            TexLabel(DepthViewTextureName),
            TexLabel(StencilViewTextureName),
            TexLabel(AutoExposureTextureName),
            TexLabel(AtmosphereColorTextureName),
            TexLabel(VolumetricFogColorTextureName));
    }

    private void FxaaFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        XRTexture? source = GetTexture<XRTexture>(FinalPostProcessOutputTextureName);
        if (source is not null)
            materialProgram.Sampler(PostProcessOutputTextureName, source, 0);

        float width = Math.Max(1u, FullWidth);
        float height = Math.Max(1u, FullHeight);
        var texelStep = new Vector2(1.0f / width, 1.0f / height);
        materialProgram.Uniform("FxaaTexelStep", texelStep);
    }

    private static bool _loggedVolumetricFogScatterLightsOnce;
    private static bool _loggedVolumetricFogScatterCascadeOnce;
    private static ulong _lastVolumetricFogScatterLightsKey = ulong.MaxValue;

    private void VolumetricFog_SetFragmentCameraUniforms(XRRenderProgram materialProgram)
    {
        var renderState = RenderingPipelineState;
        renderState?.SceneCamera?.SetUniforms(materialProgram, true);
        renderState?.StereoRightEyeCamera?.SetUniforms(materialProgram, false);

        if (!VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData))
            return;

        materialProgram.Uniform(EEngineUniform.ProjMatrix.ToStringFast(), temporalData.CurrProjection);
        materialProgram.Uniform(EEngineUniform.InverseProjMatrix.ToStringFast(), temporalData.CurrInverseProjection);
        materialProgram.Uniform(EEngineUniform.ViewProjectionMatrix.ToStringFast(), temporalData.CurrViewProjection);
    }

    private void Atmosphere_SetFragmentCameraUniforms(XRRenderProgram materialProgram)
    {
        var renderState = RenderingPipelineState;
        renderState?.SceneCamera?.SetUniforms(materialProgram, true);
        renderState?.StereoRightEyeCamera?.SetUniforms(materialProgram, false);

        if (!VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData))
            return;

        materialProgram.Uniform(EEngineUniform.ProjMatrix.ToStringFast(), temporalData.CurrProjection);
        materialProgram.Uniform(EEngineUniform.InverseProjMatrix.ToStringFast(), temporalData.CurrInverseProjection);
        materialProgram.Uniform(EEngineUniform.ViewProjectionMatrix.ToStringFast(), temporalData.CurrViewProjection);
    }

    private void AtmosphereHalfScatterFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        Atmosphere_SetFragmentCameraUniforms(materialProgram);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        var atmosphere = GetSettings<AtmosphericScatteringSettings>(state);
        (atmosphere ?? AtmosphericScatteringSettings.Default).SetUniforms(materialProgram);
    }

    private void AtmosphereUpscaleFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        Atmosphere_SetFragmentCameraUniforms(materialProgram);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        var atmosphere = GetSettings<AtmosphericScatteringSettings>(state);
        (atmosphere ?? AtmosphericScatteringSettings.Default).SetUniforms(materialProgram);
    }

    private void AtmosphereReprojectFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        Atmosphere_SetFragmentCameraUniforms(materialProgram);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        var atmosphere = GetSettings<AtmosphericScatteringSettings>(state);
        float maxDistance = atmosphere is not null && atmosphere.Enabled && atmosphere.AerialPerspective
            ? atmosphere.MaxDistance
            : 0.0f;
        int debugMode = atmosphere is null ? 0 : (int)atmosphere.DebugMode;

        bool historyReady = false;
        Matrix4x4 previousViewProjection = Matrix4x4.Identity;
        uint historyWidth = 1u;
        uint historyHeight = 1u;
        if (VPRC_AtmosphereHistoryPass.TryGetTemporalUniformData(out var temporalData))
        {
            bool temporalEnabled = atmosphere is null || atmosphere.TemporalEnabled;
            historyReady = temporalData.HistoryReady && maxDistance > 0.0f && debugMode == 0 && temporalEnabled;
            previousViewProjection = temporalData.PreviousViewProjection;
            historyWidth = Math.Max(1u, temporalData.Width);
            historyHeight = Math.Max(1u, temporalData.Height);
        }

        materialProgram.Uniform("AtmosphereHistoryReady", historyReady);
        materialProgram.Uniform("AtmospherePreviousViewProjection", previousViewProjection);
        materialProgram.Uniform("AtmosphereHistoryTexelSize", new Vector2(1.0f / historyWidth, 1.0f / historyHeight));
        materialProgram.Uniform("AtmosphereMaxDistance", maxDistance);
        materialProgram.Uniform("AtmosphereTemporalAlpha", 0.92f);
        materialProgram.Uniform("AtmosphereDepthRejectThreshold", 10.0f);
        materialProgram.Uniform("AtmosphereDebugMode", debugMode);
    }

    /// <summary>
    /// Pushes volumetric-fog + primary directional shadow uniforms to the
    /// half-resolution scatter quad pass. The <see cref="EUniformRequirements.Lights"/>
    /// engine-uniform plumbing also handles ShadowMap / ShadowMapArray sampler binds,
    /// but we re-issue the forward-lighting upload explicitly here as well so that
    /// DirectionalLights / DirLightCount / ShadowMapEnabled are guaranteed to be
    /// present on this program even if the missing-uniform cache was warmed by an
    /// earlier pass that bound the same flag bit for a different program.
    /// </summary>
    private void VolumetricFogHalfScatterFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        VolumetricFog_SetFragmentCameraUniforms(materialProgram);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        var volumetricFog = GetSettings<VolumetricFogSettings>(state);
        (volumetricFog ?? new VolumetricFogSettings()).SetUniforms(materialProgram);
        materialProgram.Uniform("GlobalAmbient", new Vector3(0.1f, 0.1f, 0.1f));

        var lights = RuntimeEngine.Rendering.State.RenderingWorld?.Lights;
        if (lights is not null)
        {
            lights.SetForwardLightingUniforms(materialProgram);

            // Mirror the deferred directional-light upload path used by the scene light-combine
            // pass so the volumetric fog shader can read the primary directional light through
            // `LightData.*` instead of the array-of-struct `DirectionalLights[0].*` path.
            if (lights.DynamicDirectionalLights.Count > 0)
                lights.DynamicDirectionalLights[0].SetUniforms(materialProgram);

            // Diagnostic: log on transition of (dirLightCount, castsShadows, hasShadowTex) so we
            // can tell whether the scatter pass observes a shadow-enabled directional light. Driven
            // by the user-reported mode-8 output (red = ShadowMapEnabled false at scatter time).
            int dirCount = lights.DynamicDirectionalLights.Count;
            bool casts = false;
            bool hasShadowTex = false;
            int texCount = 0;
            if (dirCount > 0)
            {
                var first = lights.DynamicDirectionalLights[0];
                casts = first.CastsShadows;
                texCount = first.ShadowMap?.Material?.Textures.Count ?? 0;
                hasShadowTex = texCount > 0;
            }
            ulong key = ((ulong)(uint)dirCount)
                      | ((casts ? 1UL : 0UL) << 32)
                      | ((hasShadowTex ? 1UL : 0UL) << 33)
                      | (((ulong)(uint)texCount) << 40);
            if (key != _lastVolumetricFogScatterLightsKey)
            {
                _lastVolumetricFogScatterLightsKey = key;
                Debug.Lighting($"[VolumetricFog.Scatter] Lights upload: DirLights={dirCount} CastsShadows={casts} HasShadowTex={hasShadowTex} TexCount={texCount}");
            }

            // One-shot diagnostic: dump the published cascade state of the primary directional
            // light so we can see whether the cascade data the scatter pass is uploading is
            // valid. If ActiveCascadeCount is 0 or the first cascade's WorldToLightSpaceMatrix
            // is Identity, mode-11 "always white" is explained by the matrix itself being
            // degenerate rather than by a uniform-upload drop.
            if (!_loggedVolumetricFogScatterCascadeOnce && dirCount > 0)
            {
                _loggedVolumetricFogScatterCascadeOnce = true;
                var first = lights.DynamicDirectionalLights[0];
                int activeCascades = first.ActiveCascadeCount;
                Debug.Lighting(
                    $"[VolumetricFog.Scatter] DirLight[0] EnableCascadedShadows={first.EnableCascadedShadows} " +
                    $"ActiveCascadeCount={activeCascades} CascadedShadowMapTexture={(first.CascadedShadowMapTexture != null ? "present" : "null")} " +
                    $"WorldForward=({first.Transform.WorldForward.X:F3},{first.Transform.WorldForward.Y:F3},{first.Transform.WorldForward.Z:F3})");
            }
        }
        else if (!_loggedVolumetricFogScatterLightsOnce)
        {
            _loggedVolumetricFogScatterLightsOnce = true;
            Debug.Lighting("[VolumetricFog.Scatter] Lights upload skipped: RenderingWorld is null at scatter pass.");
        }
    }

    private void VolumetricFogUpscaleFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        VolumetricFog_SetFragmentCameraUniforms(materialProgram);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        var volumetricFog = GetSettings<VolumetricFogSettings>(state);
        (volumetricFog ?? new VolumetricFogSettings()).SetUniforms(materialProgram);
    }

    private void VolumetricFogReprojectFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        VolumetricFog_SetFragmentCameraUniforms(materialProgram);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        var volumetricFog = GetSettings<VolumetricFogSettings>(state);
        float maxDistance = volumetricFog is not null && volumetricFog.Enabled && volumetricFog.Intensity > 0.0f
            ? volumetricFog.MaxDistance
            : 0.0f;
        int debugMode = volumetricFog is null ? 0 : (int)volumetricFog.DebugMode;

        bool historyReady = false;
        Matrix4x4 previousViewProjection = Matrix4x4.Identity;
        uint historyWidth = 1u;
        uint historyHeight = 1u;
        if (VPRC_VolumetricFogHistoryPass.TryGetTemporalUniformData(out var temporalData))
        {
            historyReady = temporalData.HistoryReady && maxDistance > 0.0f && debugMode == 0;
            previousViewProjection = temporalData.PreviousViewProjection;
            historyWidth = Math.Max(1u, temporalData.Width);
            historyHeight = Math.Max(1u, temporalData.Height);
        }

        materialProgram.Uniform("VolumetricFogHistoryReady", historyReady);
        materialProgram.Uniform("VolumetricFogPreviousViewProjection", previousViewProjection);
        materialProgram.Uniform("VolumetricFogHistoryTexelSize", new Vector2(1.0f / historyWidth, 1.0f / historyHeight));
        materialProgram.Uniform("VolumetricFogMaxDistance", maxDistance);
        materialProgram.Uniform("VolumetricFogTemporalAlpha", 0.9f);
        materialProgram.Uniform("VolumetricFogDepthRejectThreshold", 1.0f);
        materialProgram.Uniform("VolumetricFogDebugMode", debugMode);
    }

    private static TemporalResolveSettings ResolveTemporalSettings(PipelinePostProcessState? state)
    {
        var stage = state?.GetStage(TemporalAntiAliasingStageKey);
        return new TemporalResolveSettings
        {
            FeedbackMin = stage?.GetValue(TemporalFeedbackMinParameterName, TemporalFeedbackMin) ?? TemporalFeedbackMin,
            FeedbackMax = stage?.GetValue(TemporalFeedbackMaxParameterName, TemporalFeedbackMax) ?? TemporalFeedbackMax,
            VarianceGamma = stage?.GetValue(TemporalVarianceGammaParameterName, TemporalVarianceGamma) ?? TemporalVarianceGamma,
            CatmullRadius = stage?.GetValue(TemporalCatmullRadiusParameterName, TemporalCatmullRadius) ?? TemporalCatmullRadius,
            DepthRejectThreshold = stage?.GetValue(TemporalDepthRejectThresholdParameterName, TemporalDepthRejectThreshold) ?? TemporalDepthRejectThreshold,
            ReactiveTransparencyRange = stage?.GetValue(TemporalReactiveTransparencyRangeParameterName, TemporalReactiveTransparencyRange) ?? TemporalReactiveTransparencyRange,
            ReactiveVelocityScale = stage?.GetValue(TemporalReactiveVelocityScaleParameterName, TemporalReactiveVelocityScale) ?? TemporalReactiveVelocityScale,
            ReactiveLumaThreshold = stage?.GetValue(TemporalReactiveLumaThresholdParameterName, TemporalReactiveLumaThreshold) ?? TemporalReactiveLumaThreshold,
            DepthDiscontinuityScale = stage?.GetValue(TemporalDepthDiscontinuityScaleParameterName, TemporalDepthDiscontinuityScale) ?? TemporalDepthDiscontinuityScale,
            ConfidencePower = stage?.GetValue(TemporalConfidencePowerParameterName, TemporalConfidencePower) ?? TemporalConfidencePower,
            DebugMode = (TemporalDebugViewMode)(stage?.GetValue(TemporalDebugViewModeParameterName, (int)TemporalDebugViewMode.Disabled) ?? (int)TemporalDebugViewMode.Disabled),
        };
    }

    private void TsrUpscaleFBO_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? source = GetTexture<XRTexture>(FinalPostProcessOutputTextureName);
        if (source is not null)
            program.Sampler(PostProcessOutputTextureName, source, 0);

        TsrResolveFBO_SettingUniforms(program);
    }

    /// <summary>
    /// Applies the TSR parameters without replacing the source sampler selected by
    /// the mono-reference material. The production SPS resolve deliberately binds
    /// the complete array; each mono oracle must retain its one-layer texture view.
    /// </summary>
    private void TsrMonoReferenceFBO_SettingUniforms(XRRenderProgram program)
        => TsrResolveFBO_SettingUniforms(program);

    private void TsrResolveFBO_SettingUniforms(XRRenderProgram program)
    {
        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        TemporalResolveSettings temporalSettings = ResolveTemporalSettings(state);
        bool historyReady = false;
        Vector2 currentJitterUv = Vector2.Zero;
        Vector2 previousJitterUv = Vector2.Zero;
        bool temporalHistoryAllowed = !DisableHistoryBasedVrEffects();
        if (temporalHistoryAllowed && VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData))
        {
            // TSR owns a full-resolution color history; the exposure-variance history is only produced by the TAA resolve.
            historyReady = temporalData.HistoryReady;
            currentJitterUv = new Vector2(temporalData.CurrentJitter.X / Math.Max(1u, InternalWidth), temporalData.CurrentJitter.Y / Math.Max(1u, InternalHeight));
            previousJitterUv = new Vector2(temporalData.PreviousJitter.X / Math.Max(1u, InternalWidth), temporalData.PreviousJitter.Y / Math.Max(1u, InternalHeight));
        }
        else if (temporalHistoryAllowed)
        {
            VPRC_TemporalAccumulationPass.ReportMissingTemporalSnapshot(
                CurrentRenderingPipeline,
                "Default.TSR",
                currentMatrixLayerMask: 0u,
                expectedLayerMask: Stereo ? 0b11u : 0b01u);
        }

        float sourceWidth = Math.Max(1u, InternalWidth);
        float sourceHeight = Math.Max(1u, InternalHeight);
        float historyWidth = Math.Max(1u, FullWidth);
        float historyHeight = Math.Max(1u, FullHeight);

/*
        Debug.RenderingEvery(
            $"TsrUpscaleFBO.Uniforms.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[RenderDiag] TsrUniforms HistoryReady={0} Internal={1}x{2} Full={3}x{4} CurrentJitter={5} PreviousJitter={6}",
            historyReady,
            InternalWidth,
            InternalHeight,
            FullWidth,
            FullHeight,
            currentJitterUv,
            previousJitterUv);
*/

        program.Uniform("HistoryReady", historyReady);
        program.Uniform("SourceTexelSize", new Vector2(1.0f / sourceWidth, 1.0f / sourceHeight));
        program.Uniform("HistoryTexelSize", new Vector2(1.0f / historyWidth, 1.0f / historyHeight));
        program.Uniform("CurrentJitterUv", currentJitterUv);
        program.Uniform("PreviousJitterUv", previousJitterUv);
        program.Uniform("FeedbackMin", temporalSettings.FeedbackMin);
        program.Uniform("FeedbackMax", temporalSettings.FeedbackMax);
        program.Uniform("VarianceGamma", temporalSettings.VarianceGamma);
        program.Uniform("CatmullRadius", temporalSettings.CatmullRadius);
        program.Uniform("DepthRejectThreshold", temporalSettings.DepthRejectThreshold);
        program.Uniform("ReactiveTransparencyRange", temporalSettings.ReactiveTransparencyRange);
        program.Uniform("ReactiveVelocityScale", temporalSettings.ReactiveVelocityScale);
        program.Uniform("ReactiveLumaThreshold", temporalSettings.ReactiveLumaThreshold);
        program.Uniform("DepthDiscontinuityScale", temporalSettings.DepthDiscontinuityScale);
        program.Uniform("ConfidencePower", temporalSettings.ConfidencePower);
        program.Uniform("DebugMode", (int)temporalSettings.DebugMode);
    }

    private void BrightPassFBO_SettingUniforms(XRRenderProgram program)
    {
        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        ApplyBloomBrightPassUniforms(state, program);
    }

    private void DepthOfFieldFBO_SettingUniforms(XRRenderProgram program)
    {
        float width = Math.Max(1u, InternalWidth);
        float height = Math.Max(1u, InternalHeight);
        var texelSize = new Vector2(1.0f / width, 1.0f / height);

        var settings = GetDepthOfFieldSettings();
        if (settings is null || !settings.Enabled)
        {
            program.Uniform("TexelSize", texelSize);
            program.Uniform("DoFMode", 0);
            program.Uniform("FocusDepth", 1.0f);
            program.Uniform("FocusRangeDepth", 1.0f);
            program.Uniform("Aperture", 0.0f);
            program.Uniform("MaxCoC", 0.0f);
            program.Uniform("BokehRadius", 0.0f);
            program.Uniform("NearBlur", false);
            return;
        }

        settings.SetUniforms(program, texelSize);
    }

    private void MotionBlurFBO_SettingUniforms(XRRenderProgram program)
    {
        float width = Math.Max(1u, InternalWidth);
        float height = Math.Max(1u, InternalHeight);
        var texelSize = new Vector2(1.0f / width, 1.0f / height);

        var settings = GetMotionBlurSettings();
        if (settings is null || !settings.Enabled)
        {
            program.Uniform("TexelSize", texelSize);
            program.Uniform("ShutterScale", 0.0f);
            program.Uniform("VelocityThreshold", 1.0f);
            program.Uniform("DepthRejectThreshold", 0.0f);
            program.Uniform("MaxBlurPixels", 0.0f);
            program.Uniform("SampleFalloff", 1.0f);
            program.Uniform("MaxSamples", 1);
            return;
        }

        settings.SetUniforms(program, texelSize);
    }

    private void TemporalAccumulationFBO_SettingUniforms(XRRenderProgram program)
    {
        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        TemporalResolveSettings temporalSettings = ResolveTemporalSettings(state);
        bool temporalHistoryAllowed = !DisableHistoryBasedVrEffects();
        if (temporalHistoryAllowed && VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData))
        {
            float width = Math.Max(1u, temporalData.Width);
            float height = Math.Max(1u, temporalData.Height);
            bool historyReady = temporalData.HistoryReady && temporalData.HistoryExposureReady;
            program.Uniform("HistoryReady", historyReady);
            program.Uniform("TexelSize", new Vector2(1.0f / width, 1.0f / height));
            program.Uniform("CurrentJitterUv", new Vector2(temporalData.CurrentJitter.X / width, temporalData.CurrentJitter.Y / height));
            program.Uniform("PreviousJitterUv", new Vector2(temporalData.PreviousJitter.X / width, temporalData.PreviousJitter.Y / height));
        }
        else
        {
            program.Uniform("HistoryReady", false);
            program.Uniform("TexelSize", Vector2.Zero);
            program.Uniform("CurrentJitterUv", Vector2.Zero);
            program.Uniform("PreviousJitterUv", Vector2.Zero);
            if (temporalHistoryAllowed)
            {
                VPRC_TemporalAccumulationPass.ReportMissingTemporalSnapshot(
                    CurrentRenderingPipeline,
                    "Default.TemporalAccumulation",
                    currentMatrixLayerMask: 0u,
                    expectedLayerMask: Stereo ? 0b11u : 0b01u);
            }
        }

        program.Uniform("FeedbackMin", temporalSettings.FeedbackMin);
        program.Uniform("FeedbackMax", temporalSettings.FeedbackMax);
        program.Uniform("VarianceGamma", temporalSettings.VarianceGamma);
        program.Uniform("CatmullRadius", temporalSettings.CatmullRadius);
        program.Uniform("DepthRejectThreshold", temporalSettings.DepthRejectThreshold);
        program.Uniform("ReactiveTransparencyRange", temporalSettings.ReactiveTransparencyRange);
        program.Uniform("ReactiveVelocityScale", temporalSettings.ReactiveVelocityScale);
        program.Uniform("ReactiveLumaThreshold", temporalSettings.ReactiveLumaThreshold);
        program.Uniform("DepthDiscontinuityScale", temporalSettings.DepthDiscontinuityScale);
        program.Uniform("ConfidencePower", temporalSettings.ConfidencePower);
        program.Uniform("DebugMode", (int)temporalSettings.DebugMode);
    }
}
