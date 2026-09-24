using System;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Rendering.PostProcessing;

public static class CommonPostProcessStages
{
    public static class StageKeys
    {
        public const string Tonemapping = "tonemapping";
        public const string ColorGrading = "colorGrading";
        public const string Vignette = "vignette";
        public const string Bloom = "bloom";
        public const string AmbientOcclusion = "ambientOcclusion";
        public const string TemporalAntiAliasing = "temporalAntiAliasing";
        public const string MotionBlur = "motionBlur";
        public const string DepthOfField = "depthOfField";
        public const string LensDistortion = "lensDistortion";
        public const string ChromaticAberration = "chromaticAberration";
        public const string Fog = "fog";
        public const string AtmosphericScattering = "atmosphericScattering";
        public const string VolumetricFog = "volumetricFog";
        public const string GpuBvhDebug = "gpuBvhDebug";
    }

    public static class CategoryKeys
    {
        public const string Imaging = "imaging";
        public const string Bloom = "bloom";
        public const string AmbientOcclusion = "ambient-occlusion";
        public const string AntiAliasing = "anti-aliasing";
        public const string Motion = "motion";
        public const string Lens = "lens";
        public const string Atmosphere = "atmosphere";
        public const string Debug = "debug";
    }

    public const string TonemappingStageKey = StageKeys.Tonemapping;
    public const string ColorGradingStageKey = StageKeys.ColorGrading;
    public const string VignetteStageKey = StageKeys.Vignette;
    public const string BloomStageKey = StageKeys.Bloom;
    public const string AmbientOcclusionStageKey = StageKeys.AmbientOcclusion;
    public const string TemporalAntiAliasingStageKey = StageKeys.TemporalAntiAliasing;
    public const string MotionBlurStageKey = StageKeys.MotionBlur;
    public const string DepthOfFieldStageKey = StageKeys.DepthOfField;
    public const string LensDistortionStageKey = StageKeys.LensDistortion;
    public const string ChromaticAberrationStageKey = StageKeys.ChromaticAberration;
    public const string FogStageKey = StageKeys.Fog;
    public const string AtmosphericScatteringStageKey = StageKeys.AtmosphericScattering;
    public const string VolumetricFogStageKey = StageKeys.VolumetricFog;
    public const string GpuBvhDebugStageKey = StageKeys.GpuBvhDebug;

    public const float DefaultTemporalFeedbackMin = 0.16f;
    public const float DefaultTemporalFeedbackMax = 0.96f;
    public const float DefaultTemporalVarianceGamma = 1.0f;
    public const float DefaultTemporalCatmullRadius = 1.0f;
    public const float DefaultTemporalDepthRejectThreshold = 0.0075f;
    public static readonly Vector2 DefaultTemporalReactiveTransparencyRange = new(0.4f, 0.85f);
    public const float DefaultTemporalReactiveVelocityScale = 0.55f;
    public const float DefaultTemporalReactiveLumaThreshold = 0.35f;
    public const float DefaultTemporalDepthDiscontinuityScale = 140.0f;
    public const float DefaultTemporalConfidencePower = 0.55f;

    public static void AddStandardPipelineSchema(RenderPipelinePostProcessSchemaBuilder builder)
    {
        AddStandardStages(builder);
        AddStandardCategories(builder);
    }

    public static void AddStandardStages(RenderPipelinePostProcessSchemaBuilder builder)
    {
        DescribeTonemappingStage(builder.Stage(StageKeys.Tonemapping, "Tonemapping").BackedBy<TonemappingSettings>());
        DescribeColorGradingStage(builder.Stage(StageKeys.ColorGrading, "Color Grading").BackedBy<ColorGradingSettings>());
        DescribeVignetteStage(builder.Stage(StageKeys.Vignette, "Vignette").BackedBy<VignetteSettings>());
        DescribeBloomStage(builder.Stage(StageKeys.Bloom, "Bloom").BackedBy<BloomSettings>());
        DescribeAmbientOcclusionStage(builder.Stage(StageKeys.AmbientOcclusion, "Ambient Occlusion").BackedBy<AmbientOcclusionSettings>());
        DescribeTemporalAntiAliasingStage(builder.Stage(StageKeys.TemporalAntiAliasing, "Temporal AA"));
        DescribeMotionBlurStage(builder.Stage(StageKeys.MotionBlur, "Motion Blur").BackedBy<MotionBlurSettings>());
        DescribeDepthOfFieldStage(builder.Stage(StageKeys.DepthOfField, "Depth of Field").BackedBy<DepthOfFieldSettings>());
        DescribeLensDistortionStage(builder.Stage(StageKeys.LensDistortion, "Lens Distortion").BackedBy<LensDistortionSettings>());
        DescribeChromaticAberrationStage(builder.Stage(StageKeys.ChromaticAberration, "Chromatic Aberration").BackedBy<ChromaticAberrationSettings>());
        DescribeFogStage(builder.Stage(StageKeys.Fog, "Depth Fog").BackedBy<FogSettings>());
        DescribeAtmosphericScatteringStage(builder.Stage(StageKeys.AtmosphericScattering, "Atmospheric Scattering").BackedBy<AtmosphericScatteringSettings>());
        DescribeVolumetricFogStage(builder.Stage(StageKeys.VolumetricFog, "Volumetric Fog").BackedBy<VolumetricFogSettings>());
        DescribeGpuBvhDebugStage(builder.Stage(StageKeys.GpuBvhDebug, "Debug Visualization")
            .BackedBy<GpuBvhDebugSettings>()
            .InEditorSection(PipelineEditorSection.Debug));
    }

    public static void AddStandardCategories(RenderPipelinePostProcessSchemaBuilder builder)
    {
        builder.Category(CategoryKeys.Imaging, "Imaging")
            .IncludeStages(StageKeys.Tonemapping, StageKeys.ColorGrading, StageKeys.Vignette);

        builder.Category(CategoryKeys.Bloom, "Bloom")
            .IncludeStage(StageKeys.Bloom);

        builder.Category(CategoryKeys.AmbientOcclusion, "Ambient Occlusion")
            .IncludeStage(StageKeys.AmbientOcclusion);

        builder.Category(CategoryKeys.AntiAliasing, "Anti-Aliasing")
            .IncludeStage(StageKeys.TemporalAntiAliasing);

        builder.Category(CategoryKeys.Motion, "Motion Blur")
            .IncludeStage(StageKeys.MotionBlur);

        builder.Category(CategoryKeys.Lens, "Lens & Aberration")
            .IncludeStages(StageKeys.LensDistortion, StageKeys.ChromaticAberration, StageKeys.DepthOfField);

        builder.Category(CategoryKeys.Atmosphere, "Atmosphere")
            .IncludeStages(StageKeys.Fog, StageKeys.AtmosphericScattering, StageKeys.VolumetricFog);

        builder.Category(CategoryKeys.Debug, "Debug")
            .IncludeStage(StageKeys.GpuBvhDebug);
    }

    public static void DescribeTonemappingStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.WithStateEvaluator(static camera =>
        {
            bool effectiveHDR = camera.OutputHDROverride ?? RuntimeEngine.Rendering.Settings.OutputHDR;
            return effectiveHDR
                ? (true, "Tonemapping is bypassed while HDR output is active.")
                : (false, null);
        });

        bool IsMobius(object o) => ((TonemappingSettings)o).Tonemapping == ETonemappingType.Mobius;

        stage.AddParameter(
            PostProcessParameterNames.TonemappingOperator,
            PostProcessParameterKind.Int,
            (int)ETonemappingType.Mobius,
            displayName: "Operator",
            enumOptions: BuildEnumOptions<ETonemappingType>());

        stage.AddParameter(
            PostProcessParameterNames.MobiusTransition,
            PostProcessParameterKind.Float,
            TonemappingSettings.DefaultMobiusTransition,
            displayName: "Mobius Transition",
            min: TonemappingSettings.MinMobiusTransition,
            max: TonemappingSettings.MaxMobiusTransition,
            step: 0.01f,
            visibilityCondition: IsMobius);
    }

    public static void DescribeColorGradingStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(ColorGradingSettings.Tint),
            PostProcessParameterKind.Vector3,
            Vector3.One,
            displayName: "Tint",
            isColor: true);

        stage.AddParameter(
            nameof(ColorGradingSettings.ExposureMode),
            PostProcessParameterKind.Int,
            (int)ColorGradingSettings.ExposureControlMode.Artist,
            displayName: "Exposure Mode",
            enumOptions: BuildEnumOptions<ColorGradingSettings.ExposureControlMode>());

        bool IsArtistMode(object o) => ((ColorGradingSettings)o).ExposureMode == ColorGradingSettings.ExposureControlMode.Artist;
        bool IsPhysicalMode(object o) => ((ColorGradingSettings)o).ExposureMode == ColorGradingSettings.ExposureControlMode.Physical;

        stage.AddParameter(
            nameof(ColorGradingSettings.AutoExposure),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Auto Exposure");

        bool IsAutoExposure(object o) => ((ColorGradingSettings)o).AutoExposure;
        bool IsManualExposure(object o) => IsArtistMode(o) && !((ColorGradingSettings)o).AutoExposure;
        bool IsIgnoreTopPercent(object o)
            => IsAutoExposure(o)
            && ((ColorGradingSettings)o).AutoExposureMetering == ColorGradingSettings.AutoExposureMeteringMode.IgnoreTopPercent;
        bool IsCenterWeighted(object o)
            => IsAutoExposure(o)
            && ((ColorGradingSettings)o).AutoExposureMetering == ColorGradingSettings.AutoExposureMeteringMode.CenterWeighted;
        bool IsAdvancedMetering(object o)
            => IsAutoExposure(o)
            && ((ColorGradingSettings)o).AutoExposureMetering != ColorGradingSettings.AutoExposureMeteringMode.Average;

        stage.AddParameter(
            nameof(ColorGradingSettings.Exposure),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Manual Exposure",
            min: 0.0001f,
            max: 10.0f,
            step: 0.0001f,
            visibilityCondition: IsManualExposure);

        stage.AddParameter(
            nameof(ColorGradingSettings.PhysicalApertureFNumber),
            PostProcessParameterKind.Float,
            2.8f,
            displayName: "Aperture (f-stop)",
            min: 0.1f,
            max: 64.0f,
            step: 0.1f,
            visibilityCondition: IsPhysicalMode);

        stage.AddParameter(
            nameof(ColorGradingSettings.PhysicalShutterSpeedSeconds),
            PostProcessParameterKind.Float,
            1.0f / 60.0f,
            displayName: "Shutter (seconds)",
            min: 0.00001f,
            max: 10.0f,
            step: 0.00001f,
            visibilityCondition: IsPhysicalMode);

        stage.AddParameter(
            nameof(ColorGradingSettings.PhysicalISO),
            PostProcessParameterKind.Float,
            100.0f,
            displayName: "ISO",
            min: 1.0f,
            max: 51200.0f,
            step: 1.0f,
            visibilityCondition: IsPhysicalMode);

        stage.AddParameter(
            nameof(ColorGradingSettings.PhysicalExposureCompensationEV),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "Exposure Compensation (EV)",
            min: -10.0f,
            max: 10.0f,
            step: 0.1f,
            visibilityCondition: IsPhysicalMode);

        stage.AddParameter(
            nameof(ColorGradingSettings.PhysicalExposureScale),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Exposure Scale",
            min: 0.0f,
            max: 10.0f,
            step: 0.01f,
            visibilityCondition: IsPhysicalMode);

        stage.AddParameter(
            nameof(ColorGradingSettings.AutoExposureBias),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "Exposure Bias",
            min: -10.0f,
            max: 10.0f,
            step: 0.1f,
            visibilityCondition: IsAutoExposure);

        stage.AddParameter(
            nameof(ColorGradingSettings.AutoExposureScale),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Exposure Scale",
            min: 0.1f,
            max: 5.0f,
            step: 0.01f,
            visibilityCondition: IsAutoExposure);

        stage.AddParameter(
            nameof(ColorGradingSettings.MinExposure),
            PostProcessParameterKind.Float,
            0.0001f,
            displayName: "Min Exposure",
            min: 0.0f,
            max: 10.0f,
            step: 0.0001f,
            visibilityCondition: IsAutoExposure);

        stage.AddParameter(
            nameof(ColorGradingSettings.MaxExposure),
            PostProcessParameterKind.Float,
            100.0f,
            displayName: "Max Exposure",
            min: 0.0f,
            max: 1000.0f,
            step: 1.0f,
            visibilityCondition: IsAutoExposure);

        stage.AddParameter(
            nameof(ColorGradingSettings.ExposureDividend),
            PostProcessParameterKind.Float,
            0.1f,
            displayName: "Exposure Dividend",
            min: 0.0f,
            max: 10.0f,
            step: 0.01f,
            visibilityCondition: IsAutoExposure);

        stage.AddParameter(
            nameof(ColorGradingSettings.ExposureTransitionSpeed),
            PostProcessParameterKind.Float,
            0.5f,
            displayName: "Transition Speed",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsAutoExposure);

        stage.AddParameter(
            nameof(ColorGradingSettings.AutoExposureMetering),
            PostProcessParameterKind.Int,
            (int)ColorGradingSettings.AutoExposureMeteringMode.LogAverage,
            displayName: "Metering Mode",
            enumOptions: BuildEnumOptions<ColorGradingSettings.AutoExposureMeteringMode>(),
            visibilityCondition: IsAutoExposure);

        stage.AddParameter(
            nameof(ColorGradingSettings.AutoExposureMeteringTargetSize),
            PostProcessParameterKind.Int,
            16,
            displayName: "Metering Target Size",
            min: 1,
            max: 64,
            step: 1,
            visibilityCondition: IsAdvancedMetering);

        stage.AddParameter(
            nameof(ColorGradingSettings.AutoExposureLuminanceWeights),
            PostProcessParameterKind.Vector3,
            new Vector3(0.299f, 0.587f, 0.114f),
            displayName: "Luminance Weights",
            min: 0.0f,
            max: 1.0f,
            step: 0.001f,
            visibilityCondition: IsAutoExposure);

        stage.AddParameter(
            nameof(ColorGradingSettings.AutoExposureIgnoreTopPercent),
            PostProcessParameterKind.Float,
            0.02f,
            displayName: "Ignore Brightest %",
            min: 0.0f,
            max: 0.5f,
            step: 0.005f,
            visibilityCondition: IsIgnoreTopPercent);

        stage.AddParameter(
            nameof(ColorGradingSettings.AutoExposureCenterWeightStrength),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Center Weight Strength",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsCenterWeighted);

        stage.AddParameter(
            nameof(ColorGradingSettings.AutoExposureCenterWeightPower),
            PostProcessParameterKind.Float,
            2.0f,
            displayName: "Center Weight Power",
            min: 0.1f,
            max: 8.0f,
            step: 0.1f,
            visibilityCondition: IsCenterWeighted);

        stage.AddParameter(
            nameof(ColorGradingSettings.Contrast),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Contrast",
            min: -50.0f,
            max: 50.0f,
            step: 0.1f);

        stage.AddParameter(
            nameof(ColorGradingSettings.Gamma),
            PostProcessParameterKind.Float,
            2.2f,
            displayName: "Gamma",
            min: 0.1f,
            max: 4.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(ColorGradingSettings.Hue),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Hue",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(ColorGradingSettings.Saturation),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Saturation",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(ColorGradingSettings.Brightness),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Brightness",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f);
    }

    public static void DescribeVignetteStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(VignetteSettings.Enabled),
            PostProcessParameterKind.Bool,
            false,
            displayName: "Enabled");

        bool IsEnabled(object o) => ((VignetteSettings)o).Enabled;

        stage.AddParameter(
            nameof(VignetteSettings.Color),
            PostProcessParameterKind.Vector3,
            Vector3.Zero,
            displayName: "Color",
            isColor: true,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(VignetteSettings.Intensity),
            PostProcessParameterKind.Float,
            0.35f,
            displayName: "Intensity",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(VignetteSettings.Power),
            PostProcessParameterKind.Float,
            2.0f,
            displayName: "Power",
            min: 0.01f,
            max: 8.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);
    }

    public static void DescribeBloomStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(BloomSettings.Enabled),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Enabled");

        bool IsEnabled(object o) => ((BloomSettings)o).Enabled;

        stage.AddParameter(
            nameof(BloomSettings.Intensity),
            PostProcessParameterKind.Float,
            0.530f,
            displayName: "Intensity",
            min: 0.0f,
            max: 5.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.Threshold),
            PostProcessParameterKind.Float,
            0.138f,
            displayName: "Threshold",
            min: 0.0f,
            max: 5.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.SoftKnee),
            PostProcessParameterKind.Float,
            0.5f,
            displayName: "Soft Knee",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.Radius),
            PostProcessParameterKind.Float,
            1.495f,
            displayName: "Blur Radius",
            min: 0.1f,
            max: 8.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.Scatter),
            PostProcessParameterKind.Float,
            0.919f,
            displayName: "Scatter",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.Strength),
            PostProcessParameterKind.Float,
            0.5805f,
            displayName: "Bloom Strength",
            min: 0.0f,
            max: 1.0f,
            step: 0.001f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.StartMip),
            PostProcessParameterKind.Int,
            1,
            displayName: "Start Mip (Quality)",
            min: 0,
            max: 4,
            step: 1,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.EndMip),
            PostProcessParameterKind.Int,
            4,
            displayName: "End Mip",
            min: 0,
            max: 4,
            step: 1,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.Lod0Weight),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "LOD0 Weight",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.Lod1Weight),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "LOD1 Weight",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.Lod2Weight),
            PostProcessParameterKind.Float,
            0.649f,
            displayName: "LOD2 Weight",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.Lod3Weight),
            PostProcessParameterKind.Float,
            0.397f,
            displayName: "LOD3 Weight",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.Lod4Weight),
            PostProcessParameterKind.Float,
            0.102f,
            displayName: "LOD4 Weight",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(BloomSettings.DebugBloomOnly),
            PostProcessParameterKind.Bool,
            false,
            displayName: "Debug: Show Bloom Only",
            visibilityCondition: IsEnabled,
            editorSection: PipelineEditorSection.Debug);
    }

    public static void DescribeTemporalAntiAliasingStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.WithStateEvaluator(static camera =>
        {
            EAntiAliasingMode mode = camera.AntiAliasingModeOverride ?? RuntimeEngine.EffectiveSettings.AntiAliasingMode;
            bool active = mode is EAntiAliasingMode.Taa or EAntiAliasingMode.Tsr or EAntiAliasingMode.Dlaa;
            return active
                ? (false, null)
                : (true, "Temporal AA controls are inactive because the camera is not using TAA, TSR, or DLAA.");
        });

        stage.AddParameter(
            PostProcessParameterNames.TemporalFeedbackMin,
            PostProcessParameterKind.Float,
            DefaultTemporalFeedbackMin,
            displayName: "History Weight Min",
            min: 0.0f,
            max: 1.0f,
            step: 0.001f);

        stage.AddParameter(
            PostProcessParameterNames.TemporalFeedbackMax,
            PostProcessParameterKind.Float,
            DefaultTemporalFeedbackMax,
            displayName: "History Weight Max",
            min: 0.0f,
            max: 1.0f,
            step: 0.001f);

        stage.AddParameter(
            PostProcessParameterNames.TemporalVarianceGamma,
            PostProcessParameterKind.Float,
            DefaultTemporalVarianceGamma,
            displayName: "Neighborhood Gamma",
            min: 0.1f,
            max: 4.0f,
            step: 0.01f);

        stage.AddParameter(
            PostProcessParameterNames.TemporalCatmullRadius,
            PostProcessParameterKind.Float,
            DefaultTemporalCatmullRadius,
            displayName: "History Filter Radius",
            min: 0.25f,
            max: 2.0f,
            step: 0.01f);

        stage.AddParameter(
            PostProcessParameterNames.TemporalDepthRejectThreshold,
            PostProcessParameterKind.Float,
            DefaultTemporalDepthRejectThreshold,
            displayName: "Depth Reject Threshold",
            min: 0.0f,
            max: 0.05f,
            step: 0.0001f);

        stage.AddParameter(
            PostProcessParameterNames.TemporalReactiveTransparencyRange,
            PostProcessParameterKind.Vector2,
            DefaultTemporalReactiveTransparencyRange,
            displayName: "Reactive Alpha Range",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f);

        stage.AddParameter(
            PostProcessParameterNames.TemporalReactiveVelocityScale,
            PostProcessParameterKind.Float,
            DefaultTemporalReactiveVelocityScale,
            displayName: "Reactive Velocity Scale",
            min: 0.0f,
            max: 4.0f,
            step: 0.01f);

        stage.AddParameter(
            PostProcessParameterNames.TemporalReactiveLumaThreshold,
            PostProcessParameterKind.Float,
            DefaultTemporalReactiveLumaThreshold,
            displayName: "Reactive Luma Threshold",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f);

        stage.AddParameter(
            PostProcessParameterNames.TemporalDepthDiscontinuityScale,
            PostProcessParameterKind.Float,
            DefaultTemporalDepthDiscontinuityScale,
            displayName: "Depth Edge Scale",
            min: 0.0f,
            max: 1000.0f,
            step: 1.0f);

        stage.AddParameter(
            PostProcessParameterNames.TemporalConfidencePower,
            PostProcessParameterKind.Float,
            DefaultTemporalConfidencePower,
            displayName: "Confidence Power",
            min: 0.0f,
            max: 4.0f,
            step: 0.01f);

        stage.AddParameter(
            PostProcessParameterNames.TemporalDebugViewMode,
            PostProcessParameterKind.Int,
            (int)TemporalDebugViewMode.Disabled,
            displayName: "Debug View",
            enumOptions: BuildEnumOptions<TemporalDebugViewMode>(),
            editorSection: PipelineEditorSection.Debug);
    }

    public static void DescribeAmbientOcclusionStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        static string AoPath(string groupName, string propertyName)
            => $"{groupName}.{propertyName}";

        stage.AddParameter(
            nameof(AmbientOcclusionSettings.Enabled),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Enabled");

        stage.AddParameter(
            nameof(AmbientOcclusionSettings.Type),
            PostProcessParameterKind.Int,
            (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion,
            displayName: "Method",
            enumOptions: BuildAmbientOcclusionTypeOptions());

        bool IsSSAO(object o) => AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type) == AmbientOcclusionSettings.EType.ScreenSpace;

        bool IsHBAO(object o) => AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type) == AmbientOcclusionSettings.EType.HorizonBased;

        bool IsHBAOPlus(object o) => AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type) == AmbientOcclusionSettings.EType.HorizonBasedPlus;

        bool IsGTAO(object o) => AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type) == AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion;

        bool UsesGtaoVisibilityBitmask(object o) => IsGTAO(o) && ((AmbientOcclusionSettings)o).GroundTruth.UseVisibilityBitmask;

        bool UsesClassicGtaoHorizon(object o) => IsGTAO(o) && !((AmbientOcclusionSettings)o).GroundTruth.UseVisibilityBitmask;

        bool IsVXAO(object o) => AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type) == AmbientOcclusionSettings.EType.VoxelAmbientOcclusion;

        bool IsMVAO(object o)
        {
            var type = AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type);
            return type == AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion;
        }

        bool IsPrototypeObscurance(object o)
            => AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type) == AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance;

        bool IsSpatialHash(object o) => AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type) == AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion;

        bool UsesRadius(object o) => IsSSAO(o) || IsHBAO(o) || IsHBAOPlus(o) || IsGTAO(o) || IsVXAO(o) || IsMVAO(o) || IsSpatialHash(o);
        bool UsesPower(object o) => IsSSAO(o) || IsHBAO(o) || IsHBAOPlus(o) || IsGTAO(o) || IsVXAO(o) || IsMVAO(o) || IsSpatialHash(o);
        bool UsesBias(object o) => IsHBAO(o) || IsHBAOPlus(o) || IsGTAO(o) || IsMVAO(o) || IsPrototypeObscurance(o) || IsSpatialHash(o);

        stage.AddParameter(
            nameof(AmbientOcclusionSettings.Radius),
            PostProcessParameterKind.Float,
            AmbientOcclusionSettings.DefaultRadius,
            displayName: "Radius",
            min: 0.1f,
            max: 5.0f,
            step: 0.01f,
            visibilityCondition: UsesRadius);

        stage.AddParameter(
            nameof(AmbientOcclusionSettings.Power),
            PostProcessParameterKind.Float,
            AmbientOcclusionSettings.DefaultPower,
            displayName: "Contrast",
            min: 0.5f,
            max: 3.0f,
            step: 0.01f,
            visibilityCondition: UsesPower);

        stage.AddParameter(
            nameof(AmbientOcclusionSettings.Bias),
            PostProcessParameterKind.Float,
            AmbientOcclusionSettings.DefaultBias,
            displayName: "Bias",
            min: 0.0f,
            max: 0.2f,
            step: 0.001f,
            visibilityCondition: UsesBias);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.Prototype), nameof(PrototypeAmbientOcclusionSettings.Intensity)),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Intensity",
            min: 0.0f,
            max: 4.0f,
            step: 0.01f,
            visibilityCondition: IsPrototypeObscurance);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.MultiView), nameof(MultiViewAmbientOcclusionSettings.SecondaryRadius)),
            PostProcessParameterKind.Float,
            1.6f,
            displayName: "Secondary Radius",
            min: 0.1f,
            max: 5.0f,
            step: 0.01f,
            visibilityCondition: IsMVAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.MultiView), nameof(MultiViewAmbientOcclusionSettings.Blend)),
            PostProcessParameterKind.Float,
            0.6f,
            displayName: "Blend",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsMVAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.MultiView), nameof(MultiViewAmbientOcclusionSettings.Spread)),
            PostProcessParameterKind.Float,
            0.5f,
            displayName: "Spread",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsMVAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.MultiView), nameof(MultiViewAmbientOcclusionSettings.DepthPhi)),
            PostProcessParameterKind.Float,
            4.0f,
            displayName: "Depth Phi",
            min: 0.1f,
            max: 10.0f,
            step: 0.1f,
            visibilityCondition: IsMVAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.MultiView), nameof(MultiViewAmbientOcclusionSettings.NormalPhi)),
            PostProcessParameterKind.Float,
            64.0f,
            displayName: "Normal Phi",
            min: 1.0f,
            max: 128.0f,
            step: 1.0f,
            visibilityCondition: IsMVAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.HorizonBased), nameof(HorizonBasedAmbientOcclusionSettings.DirectionCount)),
            PostProcessParameterKind.Int,
            8,
            displayName: "Direction Count",
            min: 4.0f,
            max: 16.0f,
            step: 1.0f,
            visibilityCondition: IsHBAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.HorizonBased), nameof(HorizonBasedAmbientOcclusionSettings.StepsPerDirection)),
            PostProcessParameterKind.Int,
            4,
            displayName: "Steps / Direction",
            min: 2.0f,
            max: 16.0f,
            step: 1.0f,
            visibilityCondition: IsHBAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.HorizonBased), nameof(HorizonBasedAmbientOcclusionSettings.TangentBias)),
            PostProcessParameterKind.Float,
            0.1f,
            displayName: "Tangent Bias",
            min: 0.0f,
            max: 0.5f,
            step: 0.001f,
            visibilityCondition: IsHBAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.HorizonBasedPlus), nameof(HorizonBasedPlusAmbientOcclusionSettings.DetailAO)),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "Detail AO",
            min: 0.0f,
            max: 5.0f,
            step: 0.01f,
            visibilityCondition: IsHBAOPlus);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.HorizonBasedPlus), nameof(HorizonBasedPlusAmbientOcclusionSettings.BlurEnabled)),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Blur Enabled",
            visibilityCondition: IsHBAOPlus);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.HorizonBasedPlus), nameof(HorizonBasedPlusAmbientOcclusionSettings.BlurRadius)),
            PostProcessParameterKind.Int,
            8,
            displayName: "Blur Radius",
            min: 0.0f,
            max: 16.0f,
            step: 1.0f,
            visibilityCondition: IsHBAOPlus);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.HorizonBasedPlus), nameof(HorizonBasedPlusAmbientOcclusionSettings.BlurSharpness)),
            PostProcessParameterKind.Float,
            4.0f,
            displayName: "Blur Sharpness",
            min: 0.0f,
            max: 16.0f,
            step: 0.1f,
            visibilityCondition: IsHBAOPlus);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.HorizonBasedPlus), nameof(HorizonBasedPlusAmbientOcclusionSettings.UseInputNormals)),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Use Input Normals",
            visibilityCondition: IsHBAOPlus);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.HorizonBasedPlus), nameof(HorizonBasedPlusAmbientOcclusionSettings.MetersToViewSpaceUnits)),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Meters To View Units",
            min: 0.01f,
            max: 100.0f,
            step: 0.01f,
            visibilityCondition: IsHBAOPlus);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.SliceCount)),
            PostProcessParameterKind.Int,
            GroundTruthAmbientOcclusionSettings.DefaultSliceCount,
            displayName: "Slice Count",
            min: 1.0f,
            max: 8.0f,
            step: 1.0f,
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.StepsPerSlice)),
            PostProcessParameterKind.Int,
            GroundTruthAmbientOcclusionSettings.DefaultStepsPerSlice,
            displayName: "Steps / Slice",
            min: 1.0f,
            max: 16.0f,
            step: 1.0f,
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.FalloffStartRatio)),
            PostProcessParameterKind.Float,
            GroundTruthAmbientOcclusionSettings.DefaultFalloffStartRatio,
            displayName: "Falloff Start Ratio",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: UsesClassicGtaoHorizon);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.DenoiseEnabled)),
            PostProcessParameterKind.Bool,
            GroundTruthAmbientOcclusionSettings.DefaultDenoiseEnabled,
            displayName: "Denoise Enabled",
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.DenoiseRadius)),
            PostProcessParameterKind.Int,
            GroundTruthAmbientOcclusionSettings.DefaultDenoiseRadius,
            displayName: "Denoise Radius",
            min: 0.0f,
            max: 16.0f,
            step: 1.0f,
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.DenoiseSharpness)),
            PostProcessParameterKind.Float,
            GroundTruthAmbientOcclusionSettings.DefaultDenoiseSharpness,
            displayName: "Denoise Sharpness",
            min: 0.0f,
            max: 16.0f,
            step: 0.1f,
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.UseInputNormals)),
            PostProcessParameterKind.Bool,
            GroundTruthAmbientOcclusionSettings.DefaultUseInputNormals,
            displayName: "Use Input Normals",
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.UseVisibilityBitmask)),
            PostProcessParameterKind.Bool,
            GroundTruthAmbientOcclusionSettings.DefaultUseVisibilityBitmask,
            displayName: "Use Visibility Bitmask",
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.VisibilityBitmaskThickness)),
            PostProcessParameterKind.Float,
            GroundTruthAmbientOcclusionSettings.DefaultVisibilityBitmaskThickness,
            displayName: "Visibility Bitmask Thickness",
            min: 0.001f,
            max: 2.0f,
            step: 0.001f,
            visibilityCondition: UsesGtaoVisibilityBitmask);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.ThicknessHeuristic)),
            PostProcessParameterKind.Float,
            GroundTruthAmbientOcclusionSettings.DefaultThicknessHeuristic,
            displayName: "Thickness Heuristic",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: UsesClassicGtaoHorizon);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.MultiBounceEnabled)),
            PostProcessParameterKind.Bool,
            GroundTruthAmbientOcclusionSettings.DefaultMultiBounceEnabled,
            displayName: "Multi-Bounce AO",
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.SpecularOcclusionEnabled)),
            PostProcessParameterKind.Bool,
            GroundTruthAmbientOcclusionSettings.DefaultSpecularOcclusionEnabled,
            displayName: "Specular Occlusion (GTSO)",
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.Resolution)),
            PostProcessParameterKind.Int,
            (int)GroundTruthAmbientOcclusionSettings.DefaultResolution,
            displayName: "Resolution",
            enumOptions: BuildEnumOptions<GroundTruthAmbientOcclusionSettings.EResolution>(),
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.UseNormalWeightedBlur)),
            PostProcessParameterKind.Bool,
            GroundTruthAmbientOcclusionSettings.DefaultUseNormalWeightedBlur,
            displayName: "Normal-Weighted Blur",
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.Voxel), nameof(VoxelAmbientOcclusionSettings.VoxelGridResolution)),
            PostProcessParameterKind.Int,
            128,
            displayName: "Voxel Grid Resolution",
            min: 32.0f,
            max: 512.0f,
            step: 32.0f,
            visibilityCondition: IsVXAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.Voxel), nameof(VoxelAmbientOcclusionSettings.CoverageExtent)),
            PostProcessParameterKind.Float,
            24.0f,
            displayName: "Coverage Extent",
            min: 1.0f,
            max: 256.0f,
            step: 1.0f,
            visibilityCondition: IsVXAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.Voxel), nameof(VoxelAmbientOcclusionSettings.VoxelOpacityScale)),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Voxel Opacity Scale",
            min: 0.0f,
            max: 8.0f,
            step: 0.01f,
            visibilityCondition: IsVXAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.Voxel), nameof(VoxelAmbientOcclusionSettings.TemporalReuseEnabled)),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Temporal Reuse Enabled",
            visibilityCondition: IsVXAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.Voxel), nameof(VoxelAmbientOcclusionSettings.CombineWithScreenSpaceDetail)),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Combine With Screen-Space Detail",
            visibilityCondition: IsVXAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.Voxel), nameof(VoxelAmbientOcclusionSettings.DetailBlend)),
            PostProcessParameterKind.Float,
            0.35f,
            displayName: "Detail Blend",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsVXAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.SamplesPerPixel)),
            PostProcessParameterKind.Float,
            SpatialHashAmbientOcclusionSettings.DefaultSamplesPerPixel,
            displayName: "Feature Size (px)",
            min: 1.0f,
            max: 20.0f,
            step: 0.5f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.CellSize)),
            PostProcessParameterKind.Float,
            SpatialHashAmbientOcclusionSettings.DefaultCellSize,
            displayName: "Min Cell Size",
            min: 0.01f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.Steps)),
            PostProcessParameterKind.Int,
            SpatialHashAmbientOcclusionSettings.DefaultSteps,
            displayName: "Ray Steps",
            min: 1.0f,
            max: 32.0f,
            step: 1.0f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.Thickness)),
            PostProcessParameterKind.Float,
            SpatialHashAmbientOcclusionSettings.DefaultThickness,
            displayName: "Thickness",
            min: 0.01f,
            max: 2.0f,
            step: 0.01f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.JitterScale)),
            PostProcessParameterKind.Float,
            SpatialHashAmbientOcclusionSettings.DefaultJitterScale,
            displayName: "Jitter Scale",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.TemporalReuseEnabled)),
            PostProcessParameterKind.Bool,
            SpatialHashAmbientOcclusionSettings.DefaultTemporalReuseEnabled,
            displayName: "Temporal Reuse",
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.TemporalBlendFactor)),
            PostProcessParameterKind.Float,
            SpatialHashAmbientOcclusionSettings.DefaultTemporalBlendFactor,
            displayName: "Temporal Blend",
            min: 0.0f,
            max: 0.99f,
            step: 0.01f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.TemporalClamp)),
            PostProcessParameterKind.Float,
            SpatialHashAmbientOcclusionSettings.DefaultTemporalClamp,
            displayName: "Temporal Clamp",
            min: 0.001f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.TemporalDepthRejectThreshold)),
            PostProcessParameterKind.Float,
            SpatialHashAmbientOcclusionSettings.DefaultTemporalDepthRejectThreshold,
            displayName: "Temporal Depth Reject",
            min: 0.0001f,
            max: 0.1f,
            step: 0.001f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.TemporalMotionRejectionScale)),
            PostProcessParameterKind.Float,
            SpatialHashAmbientOcclusionSettings.DefaultTemporalMotionRejectionScale,
            displayName: "Temporal Motion Reject",
            min: 0.0001f,
            max: 2.0f,
            step: 0.01f,
            visibilityCondition: IsSpatialHash);
    }

    public static void DescribeMotionBlurStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(MotionBlurSettings.Enabled),
            PostProcessParameterKind.Bool,
            false,
            displayName: "Enabled");

        stage.AddParameter(
            nameof(MotionBlurSettings.ShutterScale),
            PostProcessParameterKind.Float,
            0.75f,
            displayName: "Shutter Scale",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(MotionBlurSettings.MaxSamples),
            PostProcessParameterKind.Int,
            12,
            displayName: "Max Samples",
            min: 4.0f,
            max: 64.0f,
            step: 1.0f);

        stage.AddParameter(
            nameof(MotionBlurSettings.MaxBlurPixels),
            PostProcessParameterKind.Float,
            12.0f,
            displayName: "Max Blur (px)",
            min: 1.0f,
            max: 64.0f,
            step: 0.5f);

        stage.AddParameter(
            nameof(MotionBlurSettings.VelocityThreshold),
            PostProcessParameterKind.Float,
            0.002f,
            displayName: "Velocity Threshold",
            min: 0.0f,
            max: 0.5f,
            step: 0.0005f);

        stage.AddParameter(
            nameof(MotionBlurSettings.DepthRejectThreshold),
            PostProcessParameterKind.Float,
            0.002f,
            displayName: "Depth Reject",
            min: 0.0f,
            max: 0.05f,
            step: 0.0005f);

        stage.AddParameter(
            nameof(MotionBlurSettings.SampleFalloff),
            PostProcessParameterKind.Float,
            2.0f,
            displayName: "Sample Falloff",
            min: 0.1f,
            max: 8.0f,
            step: 0.01f);
    }

    public static void DescribeDepthOfFieldStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(DepthOfFieldSettings.Enabled),
            PostProcessParameterKind.Bool,
            false,
            displayName: "Enabled");

        stage.AddParameter(
            nameof(DepthOfFieldSettings.Mode),
            PostProcessParameterKind.Int,
            (int)DepthOfFieldSettings.DepthOfFieldControlMode.Artist,
            displayName: "Mode",
            enumOptions: BuildEnumOptions<DepthOfFieldSettings.DepthOfFieldControlMode>());

        bool IsArtistMode(object o) => ((DepthOfFieldSettings)o).Mode == DepthOfFieldSettings.DepthOfFieldControlMode.Artist;
        bool IsPhysicalMode(object o) => ((DepthOfFieldSettings)o).Mode == DepthOfFieldSettings.DepthOfFieldControlMode.Physical;
        bool IsTargetMode(object o) => ((DepthOfFieldSettings)o).Mode == DepthOfFieldSettings.DepthOfFieldControlMode.TargetTransform;
        bool IsNotTargetMode(object o) => !IsTargetMode(o);

        stage.AddParameter(
            nameof(DepthOfFieldSettings.FocusDistance),
            PostProcessParameterKind.Float,
            5.0f,
            displayName: "Focus Distance",
            min: 0.1f,
            max: 2000.0f,
            step: 0.05f,
            visibilityCondition: IsNotTargetMode);

        stage.AddParameter(
            nameof(DepthOfFieldSettings.FocusRange),
            PostProcessParameterKind.Float,
            1.5f,
            displayName: "Focus Range",
            min: 0.05f,
            max: 500.0f,
            step: 0.05f,
            visibilityCondition: IsArtistMode);

        stage.AddParameter(
            nameof(DepthOfFieldSettings.FocusTargetOffset),
            PostProcessParameterKind.Vector3,
            Vector3.Zero,
            displayName: "Focus Target Offset",
            step: 0.05f,
            visibilityCondition: IsTargetMode);

        stage.AddParameter(
            nameof(DepthOfFieldSettings.PhysicalCircleOfConfusionMm),
            PostProcessParameterKind.Float,
            0.03f,
            displayName: "Physical CoC Ref (mm)",
            min: 0.001f,
            max: 0.2f,
            step: 0.001f,
            visibilityCondition: IsPhysicalMode);

        stage.AddParameter(
            nameof(DepthOfFieldSettings.Aperture),
            PostProcessParameterKind.Float,
            2.8f,
            displayName: "Aperture (f-stop)",
            min: 0.1f,
            max: 32.0f,
            step: 0.05f);

        stage.AddParameter(
            nameof(DepthOfFieldSettings.MaxCoCRadius),
            PostProcessParameterKind.Float,
            6.0f,
            displayName: "Max CoC (px)",
            min: 0.0f,
            max: 32.0f,
            step: 0.1f);

        stage.AddParameter(
            nameof(DepthOfFieldSettings.BokehRadius),
            PostProcessParameterKind.Float,
            1.25f,
            displayName: "Bokeh Radius",
            min: 0.25f,
            max: 4.0f,
            step: 0.05f);

        stage.AddParameter(
            nameof(DepthOfFieldSettings.NearBlur),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Enable Near Blur");
    }

    public static void DescribeLensDistortionStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(LensDistortionSettings.ControlMode),
            PostProcessParameterKind.Int,
            (int)LensDistortionSettings.LensDistortionControlMode.Artist,
            displayName: "Control Mode",
            enumOptions: BuildEnumOptions<LensDistortionSettings.LensDistortionControlMode>());

        bool IsArtistControlMode(object o) => ((LensDistortionSettings)o).ControlMode == LensDistortionSettings.LensDistortionControlMode.Artist;
        bool IsPhysicalControlMode(object o) => ((LensDistortionSettings)o).ControlMode == LensDistortionSettings.LensDistortionControlMode.Physical;

        stage.AddParameter(
            nameof(LensDistortionSettings.Mode),
            PostProcessParameterKind.Int,
            (int)ELensDistortionMode.None,
            displayName: "Mode",
            enumOptions: BuildEnumOptions<ELensDistortionMode>(),
            visibilityCondition: IsArtistControlMode);

        bool IsRadialMode(object o) => ((LensDistortionSettings)o).Mode == ELensDistortionMode.Radial;
        bool IsPaniniMode(object o) => ((LensDistortionSettings)o).Mode == ELensDistortionMode.Panini;

        stage.AddParameter(
            nameof(LensDistortionSettings.Intensity),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "Intensity",
            min: -1.0f,
            max: 1.0f,
            step: 0.001f,
            visibilityCondition: o => IsArtistControlMode(o) && IsRadialMode(o));

        stage.AddParameter(
            nameof(LensDistortionSettings.PaniniDistance),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Distance",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: o => IsArtistControlMode(o) && IsPaniniMode(o));

        stage.AddParameter(
            nameof(LensDistortionSettings.PaniniCropToFit),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Crop to Fit",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: o => IsArtistControlMode(o) && IsPaniniMode(o));

        stage.AddParameter(
            nameof(LensDistortionSettings.BrownConradyK1),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "k1",
            min: -2.0f,
            max: 2.0f,
            step: 0.0001f,
            visibilityCondition: IsPhysicalControlMode);

        stage.AddParameter(
            nameof(LensDistortionSettings.BrownConradyK2),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "k2",
            min: -2.0f,
            max: 2.0f,
            step: 0.0001f,
            visibilityCondition: IsPhysicalControlMode);

        stage.AddParameter(
            nameof(LensDistortionSettings.BrownConradyK3),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "k3",
            min: -2.0f,
            max: 2.0f,
            step: 0.0001f,
            visibilityCondition: IsPhysicalControlMode);

        stage.AddParameter(
            nameof(LensDistortionSettings.BrownConradyP1),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "p1",
            min: -1.0f,
            max: 1.0f,
            step: 0.0001f,
            visibilityCondition: IsPhysicalControlMode);

        stage.AddParameter(
            nameof(LensDistortionSettings.BrownConradyP2),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "p2",
            min: -1.0f,
            max: 1.0f,
            step: 0.0001f,
            visibilityCondition: IsPhysicalControlMode);
    }

    public static void DescribeChromaticAberrationStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(ChromaticAberrationSettings.Enabled),
            PostProcessParameterKind.Bool,
            false,
            displayName: "Enabled");

        bool IsEnabled(object o) => ((ChromaticAberrationSettings)o).Enabled;

        stage.AddParameter(
            nameof(ChromaticAberrationSettings.Intensity),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "Intensity",
            min: 0.0f,
            max: 1.0f,
            step: 0.001f,
            visibilityCondition: IsEnabled);
    }

    public static void DescribeFogStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(FogSettings.DepthFogIntensity),
            PostProcessParameterKind.Float,
            0.0f,
            displayName: "Intensity",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(FogSettings.DepthFogStartDistance),
            PostProcessParameterKind.Float,
            100.0f,
            displayName: "Start Distance",
            min: 0.0f,
            max: 100000.0f,
            step: 1.0f);

        stage.AddParameter(
            nameof(FogSettings.DepthFogEndDistance),
            PostProcessParameterKind.Float,
            10000.0f,
            displayName: "End Distance",
            min: 0.0f,
            max: 100000.0f,
            step: 1.0f);

        stage.AddParameter(
            nameof(FogSettings.DepthFogColor),
            PostProcessParameterKind.Vector3,
            new Vector3(0.5f, 0.5f, 0.5f),
            displayName: "Color",
            isColor: true);
    }

    public static void DescribeAtmosphericScatteringStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(AtmosphericScatteringSettings.Enabled),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Enabled");

        bool IsEnabled(object o) => ((AtmosphericScatteringSettings)o).Enabled;

        stage.AddParameter(
            nameof(AtmosphericScatteringSettings.RenderSky),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Render Sky",
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(AtmosphericScatteringSettings.AerialPerspective),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Aerial Perspective",
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(AtmosphericScatteringSettings.Quality),
            PostProcessParameterKind.Int,
            (int)AtmosphericScatteringSettings.EQualityMode.Balanced,
            displayName: "Quality",
            enumOptions: BuildEnumOptions<AtmosphericScatteringSettings.EQualityMode>(),
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(AtmosphericScatteringSettings.ViewSamples),
            PostProcessParameterKind.Int,
            8,
            displayName: "View Samples",
            min: 1.0f,
            max: 64.0f,
            step: 1.0f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(AtmosphericScatteringSettings.OpticalDepthSamples),
            PostProcessParameterKind.Int,
            0,
            displayName: "Optical Depth Samples",
            min: 0.0f,
            max: 32.0f,
            step: 1.0f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(AtmosphericScatteringSettings.MaxDistance),
            PostProcessParameterKind.Float,
            200000.0f,
            displayName: "Max Distance",
            min: 0.0f,
            max: 10000000.0f,
            step: 100.0f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(AtmosphericScatteringSettings.JitterStrength),
            PostProcessParameterKind.Float,
            0.5f,
            displayName: "Jitter Strength",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(AtmosphericScatteringSettings.TemporalEnabled),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Temporal",
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(AtmosphericScatteringSettings.DebugMode),
            PostProcessParameterKind.Int,
            (int)AtmosphericScatteringSettings.EDebugMode.Off,
            displayName: "Debug Mode",
            enumOptions: BuildEnumOptions<AtmosphericScatteringSettings.EDebugMode>(),
            visibilityCondition: IsEnabled,
            editorSection: PipelineEditorSection.Debug);
    }

    public static void DescribeVolumetricFogStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(VolumetricFogSettings.Enabled),
            PostProcessParameterKind.Bool,
            false,
            displayName: "Enabled");

        bool IsEnabled(object o) => ((VolumetricFogSettings)o).Enabled;

        stage.AddParameter(
            nameof(VolumetricFogSettings.Intensity),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Intensity",
            min: 0.0f,
            max: 4.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(VolumetricFogSettings.MaxDistance),
            PostProcessParameterKind.Float,
            150.0f,
            displayName: "Max Distance",
            min: 0.0f,
            max: 10000.0f,
            step: 1.0f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(VolumetricFogSettings.StepSize),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Step Size",
            min: 0.25f,
            max: 128.0f,
            step: 0.25f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(VolumetricFogSettings.JitterStrength),
            PostProcessParameterKind.Float,
            0.5f,
            displayName: "Jitter Strength",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(VolumetricFogSettings.DebugMode),
            PostProcessParameterKind.Int,
            (int)VolumetricFogSettings.EDebugMode.Off,
            displayName: "Debug Mode",
            enumOptions: BuildEnumOptions<VolumetricFogSettings.EDebugMode>(),
            visibilityCondition: IsEnabled,
            editorSection: PipelineEditorSection.Debug);
    }

    public static void DescribeGpuBvhDebugStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(GpuBvhDebugSettings.FullOverdrawEnabled),
            PostProcessParameterKind.Bool,
            false,
            displayName: "Full Overdraw");

        bool IsFullOverdrawEnabled(object o) => ((GpuBvhDebugSettings)o).FullOverdrawEnabled;

        stage.AddParameter(
            nameof(GpuBvhDebugSettings.FullOverdrawSaturationCount),
            PostProcessParameterKind.Int,
            GpuBvhDebugSettings.DefaultFullOverdrawSaturationCount,
            displayName: "Overdraw Saturation Count",
            min: GpuBvhDebugSettings.MinFullOverdrawSaturationCount,
            max: GpuBvhDebugSettings.MaxFullOverdrawSaturationCount,
            step: 1,
            visibilityCondition: IsFullOverdrawEnabled);

        stage.AddParameter(
            nameof(GpuBvhDebugSettings.FullOverdrawOverlayOpacity),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Overdraw Overlay Opacity",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsFullOverdrawEnabled);

        stage.AddParameter(
            nameof(GpuBvhDebugSettings.MeshletDebugDisplayEnabled),
            PostProcessParameterKind.Bool,
            false,
            displayName: "Meshlet Debug Display");

        stage.AddParameter(
            nameof(GpuBvhDebugSettings.Enabled),
            PostProcessParameterKind.Bool,
            false,
            displayName: "GPU BVH");

        bool IsEnabled(object o) => ((GpuBvhDebugSettings)o).Enabled;

        stage.AddParameter(
            nameof(GpuBvhDebugSettings.Filter),
            PostProcessParameterKind.Int,
            (int)GpuBvhDebugSettings.NodeFilter.All,
            displayName: "GPU BVH Filter",
            enumOptions: BuildEnumOptions<GpuBvhDebugSettings.NodeFilter>(),
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(GpuBvhDebugSettings.MaxNodes),
            PostProcessParameterKind.Int,
            16384,
            displayName: "GPU BVH Max Nodes",
            min: 1,
            max: 1048576,
            step: 256,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(GpuBvhDebugSettings.LineWidth),
            PostProcessParameterKind.Float,
            0.0015f,
            displayName: "GPU BVH Line Width",
            min: 0.0001f,
            max: 0.05f,
            step: 0.0001f,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(GpuBvhDebugSettings.LeafColor),
            PostProcessParameterKind.Vector4,
            new Vector4(0.20f, 1.00f, 0.40f, 1.00f),
            displayName: "GPU BVH Leaf Color",
            isColor: true,
            visibilityCondition: IsEnabled);

        stage.AddParameter(
            nameof(GpuBvhDebugSettings.InternalColor),
            PostProcessParameterKind.Vector4,
            new Vector4(1.00f, 0.65f, 0.10f, 0.55f),
            displayName: "GPU BVH Internal Color",
            isColor: true,
            visibilityCondition: IsEnabled);
    }

    public static PostProcessEnumOption[] BuildEnumOptions<TEnum>() where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>();
        PostProcessEnumOption[] options = new PostProcessEnumOption[values.Length];

        for (int i = 0; i < values.Length; i++)
        {
            var value = values[i];
            options[i] = new PostProcessEnumOption(value.ToString(), Convert.ToInt32(value));
        }

        return options;
    }

    public static PostProcessEnumOption[] BuildAmbientOcclusionTypeOptions()
        =>
        [
            new("SSAO", (int)AmbientOcclusionSettings.EType.ScreenSpace),
            new("MVAO", (int)AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion),
            new("MSVO", (int)AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance),
            new("HBAO+", (int)AmbientOcclusionSettings.EType.HorizonBasedPlus),
            new("GTAO", (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion),
            new("VXAO / Voxel AO (Planned)", (int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion),
            new("Spatial Hash AO", (int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion),
        ];
}
