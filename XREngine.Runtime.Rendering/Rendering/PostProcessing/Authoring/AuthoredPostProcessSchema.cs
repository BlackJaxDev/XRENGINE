namespace XREngine.Rendering.PostProcessing;

/// <summary>Lowers authored post-process stages into <see cref="RenderPipelinePostProcessSchemaBuilder"/> calls.</summary>
public static class AuthoredPostProcessSchema
{
    /// <summary>Gets the keys of the library stages that an authored pipeline can reuse.</summary>
    public static IReadOnlyList<string> LibraryStageKeys { get; } =
    [
        CommonPostProcessStages.StageKeys.Tonemapping,
        CommonPostProcessStages.StageKeys.ColorGrading,
        CommonPostProcessStages.StageKeys.Vignette,
        CommonPostProcessStages.StageKeys.Bloom,
        CommonPostProcessStages.StageKeys.AmbientOcclusion,
        CommonPostProcessStages.StageKeys.TemporalAntiAliasing,
        CommonPostProcessStages.StageKeys.MotionBlur,
        CommonPostProcessStages.StageKeys.DepthOfField,
        CommonPostProcessStages.StageKeys.LensDistortion,
        CommonPostProcessStages.StageKeys.ChromaticAberration,
        CommonPostProcessStages.StageKeys.Fog,
        CommonPostProcessStages.StageKeys.AtmosphericScattering,
        CommonPostProcessStages.StageKeys.VolumetricFog,
        CommonPostProcessStages.StageKeys.GpuBvhDebug,
    ];

    /// <summary>Adds the stages to the builder in <see cref="AuthoredPostProcessStage.Order"/> order.</summary>
    /// <exception cref="ArgumentException">Thrown when a stage is not valid.</exception>
    public static void Describe(RenderPipelinePostProcessSchemaBuilder builder, IReadOnlyList<AuthoredPostProcessStage> stages)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(stages);

        List<AuthoredPostProcessStage> ordered = [.. stages];
        // A stable sort keeps the declaration order for stages with the same order value.
        for (int i = 1; i < ordered.Count; i++)
        {
            AuthoredPostProcessStage item = ordered[i];
            int j = i - 1;
            while (j >= 0 && ordered[j].Order > item.Order)
            {
                ordered[j + 1] = ordered[j];
                j--;
            }
            ordered[j + 1] = item;
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            AuthoredPostProcessStage stage = ordered[i];
            if (string.IsNullOrWhiteSpace(stage.Key))
                throw new ArgumentException("An authored post-process stage has no key.", nameof(stages));

            string displayName = string.IsNullOrWhiteSpace(stage.DisplayName) ? stage.Key : stage.DisplayName;
            RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder builderStage = builder.Stage(stage.Key, displayName);
            if (stage.IsLibraryStage)
                DescribeLibraryStage(builderStage, stage.Key);
            else
                DescribeCustomStage(builderStage, stage);

            builderStage.InEditorSection(stage.EditorSection);
            if (!string.IsNullOrWhiteSpace(stage.Category))
                builder.Category(stage.Category, string.IsNullOrWhiteSpace(stage.CategoryDisplayName) ? null : stage.CategoryDisplayName).IncludeStage(stage.Key);
        }
    }

    /// <summary>Returns whether a key names a library stage.</summary>
    public static bool IsLibraryStageKey(string key)
    {
        for (int i = 0; i < LibraryStageKeys.Count; i++)
            if (string.Equals(LibraryStageKeys[i], key, StringComparison.Ordinal))
                return true;

        return false;
    }

    private static void DescribeLibraryStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage, string key)
    {
        switch (key)
        {
            case CommonPostProcessStages.StageKeys.Tonemapping:
                CommonPostProcessStages.DescribeTonemappingStage(stage.BackedBy<TonemappingSettings>());
                break;
            case CommonPostProcessStages.StageKeys.ColorGrading:
                CommonPostProcessStages.DescribeColorGradingStage(stage.BackedBy<ColorGradingSettings>(), true);
                break;
            case CommonPostProcessStages.StageKeys.Vignette:
                CommonPostProcessStages.DescribeVignetteStage(stage.BackedBy<VignetteSettings>());
                break;
            case CommonPostProcessStages.StageKeys.Bloom:
                CommonPostProcessStages.DescribeBloomStage(stage.BackedBy<BloomSettings>());
                break;
            case CommonPostProcessStages.StageKeys.AmbientOcclusion:
                CommonPostProcessStages.DescribeAmbientOcclusionStage(stage.BackedBy<AmbientOcclusionSettings>());
                break;
            case CommonPostProcessStages.StageKeys.TemporalAntiAliasing:
                CommonPostProcessStages.DescribeTemporalAntiAliasingStage(stage);
                break;
            case CommonPostProcessStages.StageKeys.MotionBlur:
                CommonPostProcessStages.DescribeMotionBlurStage(stage.BackedBy<MotionBlurSettings>());
                break;
            case CommonPostProcessStages.StageKeys.DepthOfField:
                CommonPostProcessStages.DescribeDepthOfFieldStage(stage.BackedBy<DepthOfFieldSettings>());
                break;
            case CommonPostProcessStages.StageKeys.LensDistortion:
                CommonPostProcessStages.DescribeLensDistortionStage(stage.BackedBy<LensDistortionSettings>());
                break;
            case CommonPostProcessStages.StageKeys.ChromaticAberration:
                CommonPostProcessStages.DescribeChromaticAberrationStage(stage.BackedBy<ChromaticAberrationSettings>());
                break;
            case CommonPostProcessStages.StageKeys.Fog:
                CommonPostProcessStages.DescribeFogStage(stage.BackedBy<FogSettings>());
                break;
            case CommonPostProcessStages.StageKeys.AtmosphericScattering:
                CommonPostProcessStages.DescribeAtmosphericScatteringStage(stage.BackedBy<AtmosphericScatteringSettings>());
                break;
            case CommonPostProcessStages.StageKeys.VolumetricFog:
                CommonPostProcessStages.DescribeVolumetricFogStage(stage.BackedBy<VolumetricFogSettings>());
                break;
            case CommonPostProcessStages.StageKeys.GpuBvhDebug:
                CommonPostProcessStages.DescribeGpuBvhDebugStage(stage.BackedBy<GpuBvhDebugSettings>());
                break;
            default:
                throw new ArgumentException(string.Concat("'", key, "' is not a library post-process stage."), nameof(key));
        }
    }

    private static void DescribeCustomStage(RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder stage, AuthoredPostProcessStage authored)
    {
        if (authored.Parameters.Count == 0)
            throw new ArgumentException(string.Concat("Custom post-process stage '", authored.Key, "' has no parameters."));

        stage.BackedBy(typeof(PostProcessPropertyBag), static () => new PostProcessPropertyBag());
        for (int i = 0; i < authored.Parameters.Count; i++)
        {
            AuthoredPostProcessParameter parameter = authored.Parameters[i];
            stage.AddParameter(
                parameter.Name,
                parameter.Kind,
                parameter.GetDefaultValue(),
                string.IsNullOrWhiteSpace(parameter.DisplayName) ? null : parameter.DisplayName,
                parameter.Min,
                parameter.Max,
                parameter.Step,
                parameter.IsColor);
        }
    }
}
