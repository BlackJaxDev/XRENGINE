using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;
using XREngine.Rendering.Pipelines.Commands;
using static XREngine.RuntimeEngine.Rendering.State;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private AdvancedRenderPipelinePostProcessUI? _postProcessUIProvider;

    public override IRenderPipelinePostProcessUIProvider? PostProcessUIProvider
        => _postProcessUIProvider ??= new AdvancedRenderPipelinePostProcessUI(this);

    private static readonly string[] AdvancedPipelinePreferredPreviewTextureNames =
    [
        FinalPostProcessOutputTextureName,
        PostProcessOutputTextureName,
        HDRSceneTextureName,
        DiffuseTextureName,
        AlbedoOpacityTextureName
    ];

    private static readonly string[] AdvancedPipelinePreferredPreviewFrameBufferNames =
    [
        FinalPostProcessOutputFBOName,
        PostProcessOutputFBOName,
        ForwardPassFBOName,
        LightCombineFBOName,
        LightingAccumFBOName,
        DeferredGBufferFBOName
    ];

    public override IReadOnlyList<string> PreferredPreviewTextureNames => AdvancedPipelinePreferredPreviewTextureNames;
    public override IReadOnlyList<string> PreferredPreviewFrameBufferNames => AdvancedPipelinePreferredPreviewFrameBufferNames;

    private sealed class AdvancedRenderPipelinePostProcessUI(AdvancedRenderPipeline pipeline) : IRenderPipelinePostProcessUIProvider
    {
        public void DrawPipelineHeader(PipelineEditorContext context)
        {
            ImGui.Separator();
            ImGui.Text("Native Shading Debug View");
            EAdvancedShadingDebugView currentView = pipeline.ShadingDebugView;
            ImGui.SetNextItemWidth(-1.0f);
            if (ImGui.BeginCombo("##ShadingDebugViewCombo", currentView.ToString()))
            {
                foreach (EAdvancedShadingDebugView view in Enum.GetValues<EAdvancedShadingDebugView>())
                {
                    bool isSelected = view == currentView;
                    if (ImGui.Selectable(view.ToString(), isSelected))
                    {
                        pipeline.ShadingDebugView = view;
                    }
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        public void DrawPipelineFooter(PipelineEditorContext context)
        {
        }
    }

    private static readonly Vector3 DefaultHoverOutlineColor = new(1.0f, 1.0f, 0.0f);
    private static readonly Vector3 DefaultSelectionOutlineColor = new(0.0f, 1.0f, 0.0f);

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

    private static XRCamera? ResolveCurrentSettingsCamera(XRRenderPipelineInstance? pipeline = null)
        => RenderPipelineCameraResolver.ResolveCurrentSettingsCamera(pipeline);

    private static MotionBlurSettings? GetMotionBlurSettings()
    {
        var stage = ResolveCurrentSettingsCamera()?.GetPostProcessStageState<MotionBlurSettings>();
        return stage?.TryGetBacking(out MotionBlurSettings? settings) == true ? settings : null;
    }

    private static bool DisableHistoryBasedVrEffects()
        => !VPRC_TemporalAccumulationPass.TryUseHistoryBasedVrEffects(out _, out _);

    private static bool ShouldUseMotionBlur()
        => !AdvancedRenderPipeline.UseOpenXrVulkanDesktopStartupSafePath
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
        => !AdvancedRenderPipeline.UseOpenXrVulkanDesktopStartupSafePath
        && !IsLightProbePass
        && !RuntimeEngine.Rendering.State.IsSceneCapturePass
        && GetDepthOfFieldSettings() is { Enabled: true };

    private static bool ShouldUseBloom()
        => !IsLightProbePass
        && !RuntimeEngine.Rendering.State.IsSceneCapturePass
        && GetBloomSettings() is not { Enabled: false };

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
        (vignette ?? new VignetteSettings()).SetUniforms(program);

        var color = GetSettings<ColorGradingSettings>(state);
        (color ?? new ColorGradingSettings()).SetUniforms(program);

        var chroma = GetSettings<ChromaticAberrationSettings>(state);
        (chroma ?? new ChromaticAberrationSettings()).SetUniforms(program);

        var fog = GetSettings<FogSettings>(state);
        (fog ?? new FogSettings()).SetUniforms(program);

        var atmosphere = GetSettings<AtmosphericScatteringSettings>(state);
        (atmosphere ?? AtmosphericScatteringSettings.Default).SetUniforms(program);

        var volumetricFog = GetSettings<VolumetricFogSettings>(state);
        (volumetricFog ?? new VolumetricFogSettings()).SetUniforms(program);

        ApplyLensDistortionUniforms(state, program, applyLensDistortion);

        var bloom = GetSettings<BloomSettings>(state);
        (bloom ?? new BloomSettings()).SetCombineUniforms(program);

        var tonemapping = GetSettings<TonemappingSettings>(state);
        (tonemapping ?? new TonemappingSettings()).SetUniforms(program);
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
            (lens ?? new LensDistortionSettings()).SetUniforms(program, cameraFov, aspectRatio, distortionCenterUv);
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

    private void ApplyPostProcessProgramBindings(XRMaterial material, XRRenderProgram materialProgram)
    {
        BindCurrentPostProcessTextures(material, materialProgram);
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
    }

    /// <summary>
    /// Publishes the current generation's post-process sampler slots before Vulkan
    /// captures the draw. Native visibility can replace the HDR and view resources
    /// after the quad material was constructed, so stale material references would
    /// otherwise produce fallback descriptors.
    /// </summary>
    private void BindCurrentPostProcessTextures(XRMaterial material, XRRenderProgram materialProgram)
    {
        int expectedTextureCount = Stereo ? 6 : 8;
        if (material.Textures.Count != expectedTextureCount)
        {
            throw new InvalidOperationException(
                $"Post-process material requires exactly {expectedTextureCount} sampler slots, but has {material.Textures.Count}.");
        }

        BindCurrentPostProcessTexture(material, materialProgram, HDRSceneTextureName, 0);
        BindCurrentPostProcessTexture(material, materialProgram, BloomBlurTextureName, 1);
        BindCurrentPostProcessTexture(material, materialProgram, DepthViewTextureName, 2);
        BindCurrentPostProcessTexture(material, materialProgram, StencilViewTextureName, 3);
        BindCurrentPostProcessTexture(material, materialProgram, AutoExposureTextureName, 4);
        if (!Stereo)
        {
            BindCurrentPostProcessTexture(material, materialProgram, AtmosphereColorTextureName, 5);
            BindCurrentPostProcessTexture(material, materialProgram, VolumetricFogColorTextureName, 6);
        }
        BindCurrentPostProcessTexture(
            material,
            materialProgram,
            AdvancedVisibilityResourceNames.Metadata,
            Stereo ? 5 : 7,
            "AdvancedVisibilityMetadata");
    }

    private void BindCurrentPostProcessTexture(
        XRMaterial material,
        XRRenderProgram materialProgram,
        string resourceName,
        int slot,
        string? samplerName = null)
    {
        XRTexture texture = RequirePostProcessTexture(resourceName);
        if (!ReferenceEquals(material.Textures[slot], texture))
            material.Textures[slot] = texture;
        materialProgram.Sampler(samplerName ?? resourceName, texture, slot);
    }

    private void ApplyFinalPostProcessProgramBindings(XRRenderProgram materialProgram)
    {
        XRTexture? source = GetTexture<XRTexture>(PostProcessOutputTextureName);
        if (source is not null)
            materialProgram.Sampler(PostProcessOutputTextureName, source, 0);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        ApplyLensDistortionUniforms(state, materialProgram, enabled: true);
    }

    private void ApplyFxaaProgramBindings(XRRenderProgram materialProgram)
    {
        XRTexture? source = GetTexture<XRTexture>(FinalPostProcessOutputTextureName);
        if (source is not null)
            materialProgram.Sampler(PostProcessOutputTextureName, source, 0);

        float width = Math.Max(1u, FullWidth);
        float height = Math.Max(1u, FullHeight);
        var texelStep = new Vector2(1.0f / width, 1.0f / height);
        materialProgram.Uniform("FxaaTexelStep", texelStep);
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

    private void ApplyTsrUpscaleProgramBindings(XRRenderProgram program)
    {
        BindAdvancedTemporalReactiveMask(program);
        XRTexture? source = GetTexture<XRTexture>(FinalPostProcessOutputTextureName);
        if (source is not null)
            program.Sampler(PostProcessOutputTextureName, source, 0);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        TemporalResolveSettings temporalSettings = ResolveTemporalSettings(state);
        bool historyReady = false;
        Vector2 currentJitterUv = Vector2.Zero;
        Vector2 previousJitterUv = Vector2.Zero;
        bool temporalHistoryAllowed = !DisableHistoryBasedVrEffects();
        if (temporalHistoryAllowed && CurrentRenderingPipeline is { } pipeline &&
            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(pipeline, out var temporalData))
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
                "Advanced.TSR",
                currentMatrixLayerMask: 0u,
                expectedLayerMask: Stereo ? 0b11u : 0b01u);
        }

        float sourceWidth = Math.Max(1u, InternalWidth);
        float sourceHeight = Math.Max(1u, InternalHeight);
        float historyWidth = Math.Max(1u, FullWidth);
        float historyHeight = Math.Max(1u, FullHeight);

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

    private void ApplyBrightPassProgramBindings(XRRenderProgram program)
    {
        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        ApplyBloomBrightPassUniforms(state, program);
    }

    private void ApplyDepthOfFieldProgramBindings(XRRenderProgram program)
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

        var camera = ResolveCurrentSettingsCamera();
        settings.SetUniforms(program, texelSize, camera, height);
    }

    private void ApplyMotionBlurProgramBindings(XRRenderProgram program)
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

    private void ApplyTemporalAccumulationProgramBindings(XRRenderProgram program)
    {
        BindAdvancedTemporalReactiveMask(program);
        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        TemporalResolveSettings temporalSettings = ResolveTemporalSettings(state);
        bool temporalHistoryAllowed = !DisableHistoryBasedVrEffects();
        if (temporalHistoryAllowed && CurrentRenderingPipeline is { } pipeline &&
            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(pipeline, out var temporalData))
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
                    "Advanced.TemporalAccumulation",
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

    private static bool _loggedVolumetricFogScatterLightsOnce;
    private static bool _loggedVolumetricFogScatterCascadeOnce;
    private static ulong _lastVolumetricFogScatterLightsKey = ulong.MaxValue;

    private void VolumetricFog_SetFragmentCameraUniforms(XRRenderProgram materialProgram)
    {
        var renderState = RenderingPipelineState;
        renderState?.SceneCamera?.SetUniforms(materialProgram, true);
        renderState?.StereoRightEyeCamera?.SetUniforms(materialProgram, false);
    }

    private void Atmosphere_SetFragmentCameraUniforms(XRRenderProgram materialProgram)
    {
        var renderState = RenderingPipelineState;
        renderState?.SceneCamera?.SetUniforms(materialProgram, true);
        renderState?.StereoRightEyeCamera?.SetUniforms(materialProgram, false);
    }

    private void ApplyAtmosphereHalfScatterProgramBindings(XRRenderProgram materialProgram)
    {
        Atmosphere_SetFragmentCameraUniforms(materialProgram);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        var atmosphere = GetSettings<AtmosphericScatteringSettings>(state);
        (atmosphere ?? AtmosphericScatteringSettings.Default).SetUniforms(materialProgram);
    }

    private void ApplyAtmosphereUpscaleProgramBindings(XRRenderProgram materialProgram)
    {
        Atmosphere_SetFragmentCameraUniforms(materialProgram);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        var atmosphere = GetSettings<AtmosphericScatteringSettings>(state);
        (atmosphere ?? AtmosphericScatteringSettings.Default).SetUniforms(materialProgram);
    }

    private void ApplyAtmosphereReprojectProgramBindings(XRRenderProgram materialProgram)
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
    private void ApplyVolumetricFogHalfScatterProgramBindings(XRRenderProgram materialProgram)
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

    private void ApplyVolumetricFogUpscaleProgramBindings(XRRenderProgram materialProgram)
    {
        VolumetricFog_SetFragmentCameraUniforms(materialProgram);

        var state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
        var volumetricFog = GetSettings<VolumetricFogSettings>(state);
        (volumetricFog ?? new VolumetricFogSettings()).SetUniforms(materialProgram);
    }

    private void ApplyVolumetricFogReprojectProgramBindings(XRRenderProgram materialProgram)
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
}
