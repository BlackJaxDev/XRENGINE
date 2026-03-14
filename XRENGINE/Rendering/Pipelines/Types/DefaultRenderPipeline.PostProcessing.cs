using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;
using XREngine.Rendering.Pipelines.Commands;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    protected override void DescribePostProcessSchema(RenderPipelinePostProcessSchemaBuilder builder)
    {
        DescribeTonemappingStage(builder.Stage(TonemappingStageKey, "Tonemapping"));
        DescribeColorGradingStage(builder.Stage(ColorGradingStageKey, "Color Grading").BackedBy<ColorGradingSettings>());
        DescribeBloomStage(builder.Stage(BloomStageKey, "Bloom").BackedBy<BloomSettings>());
        DescribeAmbientOcclusionStage(builder.Stage(AmbientOcclusionStageKey, "Ambient Occlusion").BackedBy<AmbientOcclusionSettings>());
        DescribeMotionBlurStage(builder.Stage(MotionBlurStageKey, "Motion Blur").BackedBy<MotionBlurSettings>());
        DescribeDepthOfFieldStage(builder.Stage(DepthOfFieldStageKey, "Depth of Field").BackedBy<DepthOfFieldSettings>());
        DescribeLensDistortionStage(builder.Stage(LensDistortionStageKey, "Lens Distortion").BackedBy<LensDistortionSettings>());
        DescribeChromaticAberrationStage(builder.Stage(ChromaticAberrationStageKey, "Chromatic Aberration").BackedBy<ChromaticAberrationSettings>());
        DescribeFogStage(builder.Stage(FogStageKey, "Depth Fog").BackedBy<FogSettings>());

        builder.Category("imaging", "Imaging")
            .IncludeStages(TonemappingStageKey, ColorGradingStageKey);

        builder.Category("bloom", "Bloom")
            .IncludeStage(BloomStageKey);

        builder.Category("ambient-occlusion", "Ambient Occlusion")
            .IncludeStage(AmbientOcclusionStageKey);

        builder.Category("motion", "Motion Blur")
            .IncludeStage(MotionBlurStageKey);

        builder.Category("lens", "Lens & Aberration")
            .IncludeStages(LensDistortionStageKey, ChromaticAberrationStageKey, DepthOfFieldStageKey);

        builder.Category("atmosphere", "Atmosphere")
            .IncludeStage(FogStageKey);
    }

    private static void DescribeTonemappingStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            PostProcessParameterNames.TonemappingOperator,
            PostProcessParameterKind.Int,
            (int)ETonemappingType.Mobius,
            displayName: "Operator",
            enumOptions: BuildEnumOptions<ETonemappingType>());
    }

    private static void DescribeColorGradingStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
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

    private static void DescribeBloomStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
    {
        stage.AddParameter(
            nameof(BloomSettings.Intensity),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Intensity",
            min: 0.0f,
            max: 5.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(BloomSettings.Threshold),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Threshold",
            min: 0.1f,
            max: 5.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(BloomSettings.SoftKnee),
            PostProcessParameterKind.Float,
            0.5f,
            displayName: "Soft Knee",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(BloomSettings.Radius),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Blur Radius",
            min: 0.1f,
            max: 8.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(BloomSettings.StartMip),
            PostProcessParameterKind.Int,
            0,
            displayName: "Start Mip (Quality)",
            min: 0,
            max: 4,
            step: 1);

        stage.AddParameter(
            nameof(BloomSettings.EndMip),
            PostProcessParameterKind.Int,
            4,
            displayName: "End Mip",
            min: 0,
            max: 4,
            step: 1);

        stage.AddParameter(
            nameof(BloomSettings.Lod0Weight),
            PostProcessParameterKind.Float,
            0.6f,
            displayName: "LOD0 Weight",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(BloomSettings.Lod1Weight),
            PostProcessParameterKind.Float,
            0.5f,
            displayName: "LOD1 Weight",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(BloomSettings.Lod2Weight),
            PostProcessParameterKind.Float,
            0.35f,
            displayName: "LOD2 Weight",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(BloomSettings.Lod3Weight),
            PostProcessParameterKind.Float,
            0.2f,
            displayName: "LOD3 Weight",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f);

        stage.AddParameter(
            nameof(BloomSettings.Lod4Weight),
            PostProcessParameterKind.Float,
            0.1f,
            displayName: "LOD4 Weight",
            min: 0.0f,
            max: 2.0f,
            step: 0.01f);
    }

    private static void DescribeAmbientOcclusionStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
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

        bool IsVXAO(object o) => AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type) == AmbientOcclusionSettings.EType.VoxelAmbientOcclusion;

        bool IsMVAO(object o)
        {
            var type = AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type);
            return type == AmbientOcclusionSettings.EType.MultiViewCustom;
        }

        bool IsPrototypeObscurance(object o)
            => AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type) == AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype;

        bool IsSpatialHash(object o) => AmbientOcclusionSettings.NormalizeType(((AmbientOcclusionSettings)o).Type) == AmbientOcclusionSettings.EType.SpatialHashExperimental;

        bool UsesRadius(object o) => IsSSAO(o) || IsHBAO(o) || IsHBAOPlus(o) || IsGTAO(o) || IsVXAO(o) || IsMVAO(o) || IsSpatialHash(o);
        bool UsesPower(object o) => IsSSAO(o) || IsHBAO(o) || IsHBAOPlus(o) || IsGTAO(o) || IsVXAO(o) || IsMVAO(o) || IsSpatialHash(o);
        bool UsesBias(object o) => IsHBAO(o) || IsHBAOPlus(o) || IsGTAO(o) || IsMVAO(o) || IsPrototypeObscurance(o) || IsSpatialHash(o);

        stage.AddParameter(
            nameof(AmbientOcclusionSettings.Radius),
            PostProcessParameterKind.Float,
            0.9f,
            displayName: "Radius",
            min: 0.1f,
            max: 5.0f,
            step: 0.01f,
            visibilityCondition: UsesRadius);

        stage.AddParameter(
            nameof(AmbientOcclusionSettings.Power),
            PostProcessParameterKind.Float,
            1.4f,
            displayName: "Contrast",
            min: 0.5f,
            max: 3.0f,
            step: 0.01f,
            visibilityCondition: UsesPower);

        stage.AddParameter(
            nameof(AmbientOcclusionSettings.Bias),
            PostProcessParameterKind.Float,
            0.05f,
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
            3,
            displayName: "Slice Count",
            min: 1.0f,
            max: 8.0f,
            step: 1.0f,
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.StepsPerSlice)),
            PostProcessParameterKind.Int,
            6,
            displayName: "Steps / Slice",
            min: 1.0f,
            max: 16.0f,
            step: 1.0f,
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.FalloffStartRatio)),
            PostProcessParameterKind.Float,
            0.4f,
            displayName: "Falloff Start Ratio",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.DenoiseEnabled)),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Denoise Enabled",
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.DenoiseRadius)),
            PostProcessParameterKind.Int,
            4,
            displayName: "Denoise Radius",
            min: 0.0f,
            max: 16.0f,
            step: 1.0f,
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.DenoiseSharpness)),
            PostProcessParameterKind.Float,
            4.0f,
            displayName: "Denoise Sharpness",
            min: 0.0f,
            max: 16.0f,
            step: 0.1f,
            visibilityCondition: IsGTAO);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.GroundTruth), nameof(GroundTruthAmbientOcclusionSettings.UseInputNormals)),
            PostProcessParameterKind.Bool,
            true,
            displayName: "Use Input Normals",
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
            8.0f,
            displayName: "Feature Size (px)",
            min: 1.0f,
            max: 20.0f,
            step: 0.5f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.CellSize)),
            PostProcessParameterKind.Float,
            0.01f,
            displayName: "Min Cell Size",
            min: 0.01f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.Steps)),
            PostProcessParameterKind.Int,
            8,
            displayName: "Ray Steps",
            min: 1.0f,
            max: 32.0f,
            step: 1.0f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.Thickness)),
            PostProcessParameterKind.Float,
            0.5f,
            displayName: "Thickness",
            min: 0.01f,
            max: 2.0f,
            step: 0.01f,
            visibilityCondition: IsSpatialHash);

        stage.AddParameter(
            AoPath(nameof(AmbientOcclusionSettings.SpatialHash), nameof(SpatialHashAmbientOcclusionSettings.JitterScale)),
            PostProcessParameterKind.Float,
            1.0f,
            displayName: "Jitter Scale",
            min: 0.0f,
            max: 1.0f,
            step: 0.01f,
            visibilityCondition: IsSpatialHash);

    }

    private static void DescribeMotionBlurStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
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

    private static void DescribeDepthOfFieldStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
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

        stage.AddParameter(
            nameof(DepthOfFieldSettings.FocusDistance),
            PostProcessParameterKind.Float,
            5.0f,
            displayName: "Focus Distance",
            min: 0.1f,
            max: 2000.0f,
            step: 0.05f);

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

    private static void DescribeLensDistortionStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
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

    private static void DescribeChromaticAberrationStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
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

    private static void DescribeFogStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage)
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

    private static PostProcessEnumOption[] BuildEnumOptions<TEnum>() where TEnum : struct, Enum
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

    private static PostProcessEnumOption[] BuildAmbientOcclusionTypeOptions()
        =>
        [
            new("SSAO", (int)AmbientOcclusionSettings.EType.ScreenSpace),
            new("Multi-View AO (Custom)", (int)AmbientOcclusionSettings.EType.MultiViewCustom),
            new("Multi-Radius AO (Prototype)", (int)AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype),
            new("HBAO (Deferred)", (int)AmbientOcclusionSettings.EType.HorizonBased),
            new("HBAO+", (int)AmbientOcclusionSettings.EType.HorizonBasedPlus),
            new("GTAO (Experimental)", (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion),
            new("VXAO / Voxel AO (Planned)", (int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion),
            new("Spatial Hash AO (Experimental)", (int)AmbientOcclusionSettings.EType.SpatialHashExperimental),
        ];

    private static MotionBlurSettings? GetMotionBlurSettings()
    {
        var renderState = Engine.Rendering.State.RenderingPipelineState;
        var stage = renderState?.SceneCamera?.GetPostProcessStageState<MotionBlurSettings>();
        return stage?.TryGetBacking(out MotionBlurSettings? settings) == true ? settings : null;
    }

    private static bool DisableHistoryBasedVrEffects()
        => Engine.VRState.IsInVR && !Engine.Rendering.Settings.RenderVRSinglePassStereo;

    private static bool ShouldUseMotionBlur()
        => !DisableHistoryBasedVrEffects()
        && GetMotionBlurSettings() is { Enabled: true };

    private static DepthOfFieldSettings? GetDepthOfFieldSettings()
    {
        var renderState = Engine.Rendering.State.RenderingPipelineState;
        var stage = renderState?.SceneCamera?.GetPostProcessStageState<DepthOfFieldSettings>();
        return stage?.TryGetBacking(out DepthOfFieldSettings? settings) == true ? settings : null;
    }

    private static bool ShouldUseDepthOfField()
        => GetDepthOfFieldSettings() is { Enabled: true };

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

        program.Uniform("BloomIntensity", 1.0f);
        program.Uniform("BloomThreshold", 1.0f);
        program.Uniform("SoftKnee", 0.5f);
        program.Uniform("Luminance", Engine.Rendering.Settings.DefaultLuminance);
    }

    private static void ApplyPostProcessUniforms(PipelinePostProcessState? state, XRRenderProgram program)
    {
        var vignette = GetSettings<VignetteSettings>(state);
        (vignette ?? new VignetteSettings()).SetUniforms(program);

        var color = GetSettings<ColorGradingSettings>(state);
        (color ?? new ColorGradingSettings()).SetUniforms(program);

        var chroma = GetSettings<ChromaticAberrationSettings>(state);
        (chroma ?? new ChromaticAberrationSettings()).SetUniforms(program);

        var fog = GetSettings<FogSettings>(state);
        (fog ?? new FogSettings()).SetUniforms(program);

        var lens = GetSettings<LensDistortionSettings>(state);
        float widthPx = Math.Max(1, InternalWidth);
        float heightPx = Math.Max(1, InternalHeight);
        float fallbackAspectRatio = (float)widthPx / heightPx;
        float? cameraFov = null;
        float aspectRatio = fallbackAspectRatio;
        Vector2 distortionCenterUv = LensDistortionSettings.DefaultDistortionCenterUv;

        var cameraParams = RenderingPipelineState?.SceneCamera?.Parameters;
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
        (lens ?? new LensDistortionSettings()).SetUniforms(program, cameraFov, aspectRatio, distortionCenterUv);

        var bloom = GetSettings<BloomSettings>(state);
        (bloom ?? new BloomSettings()).SetCombineUniforms(program);

        var tonemapStage = state?.FindStageByParameter(PostProcessParameterNames.TonemappingOperator);
        int tonemap = tonemapStage?.GetValue<int>(PostProcessParameterNames.TonemappingOperator, (int)ETonemappingType.Reinhard)
            ?? (int)ETonemappingType.Reinhard;
        program.Uniform("TonemapType", tonemap);
    }

    private void PostProcessFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        materialProgram.Uniform("OutputHDR", Engine.Rendering.Settings.OutputHDR);

        var state = RenderingPipelineState?.SceneCamera?.GetActivePostProcessState();
        ApplyPostProcessUniforms(state, materialProgram);
    }

    private void FxaaFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        float width = Math.Max(1u, FullWidth);
        float height = Math.Max(1u, FullHeight);
        var texelStep = new Vector2(1.0f / width, 1.0f / height);
        materialProgram.Uniform("FxaaTexelStep", texelStep);
    }

    private void BrightPassFBO_SettingUniforms(XRRenderProgram program)
    {
        var state = RenderingPipelineState?.SceneCamera?.GetActivePostProcessState();
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
        if (!DisableHistoryBasedVrEffects() && VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData))
        {
            float width = Math.Max(1u, temporalData.Width);
            float height = Math.Max(1u, temporalData.Height);
            bool historyReady = temporalData.HistoryReady && temporalData.HistoryExposureReady;
            program.Uniform("HistoryReady", historyReady);
            program.Uniform("TexelSize", new Vector2(1.0f / width, 1.0f / height));
        }
        else
        {
            program.Uniform("HistoryReady", false);
            program.Uniform("TexelSize", Vector2.Zero);
        }

        program.Uniform("FeedbackMin", TemporalFeedbackMin);
        program.Uniform("FeedbackMax", TemporalFeedbackMax);
        program.Uniform("VarianceGamma", TemporalVarianceGamma);
        program.Uniform("CatmullRadius", TemporalCatmullRadius);
        program.Uniform("DepthRejectThreshold", TemporalDepthRejectThreshold);
        program.Uniform("ReactiveTransparencyRange", TemporalReactiveTransparencyRange);
        program.Uniform("ReactiveVelocityScale", TemporalReactiveVelocityScale);
        program.Uniform("ReactiveLumaThreshold", TemporalReactiveLumaThreshold);
        program.Uniform("DepthDiscontinuityScale", TemporalDepthDiscontinuityScale);
        program.Uniform("ConfidencePower", TemporalConfidencePower);
    }
}
