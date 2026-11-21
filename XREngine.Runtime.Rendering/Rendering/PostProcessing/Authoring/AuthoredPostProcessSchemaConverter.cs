namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Converts a code post-process schema into authored stage declarations. Library stages stay library stages; other
/// stages become custom stages with their parameters. Fork and the expanded script export use it.
/// </summary>
public static class AuthoredPostProcessSchemaConverter
{
    /// <summary>Converts each stage of <paramref name="schema"/>, in schema order.</summary>
    public static List<AuthoredPostProcessStage> FromSchema(RenderPipelinePostProcessSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        List<AuthoredPostProcessStage> stages = [];
        int order = 0;
        foreach (PostProcessStageDescriptor descriptor in schema.StagesByKey.Values)
        {
            AuthoredPostProcessStage stage = new()
            {
                Key = descriptor.Key,
                DisplayName = descriptor.DisplayName,
                IsLibraryStage = AuthoredPostProcessSchema.IsLibraryStageKey(descriptor.Key),
                EditorSection = descriptor.EditorSection,
                Order = order++,
            };

            for (int c = 0; c < schema.Categories.Count; c++)
            {
                PostProcessCategoryDescriptor category = schema.Categories[c];
                if (!category.StageKeys.Contains(descriptor.Key))
                    continue;

                stage.Category = category.Key;
                stage.CategoryDisplayName = category.DisplayName;
                break;
            }

            if (!stage.IsLibraryStage)
            {
                for (int p = 0; p < descriptor.Parameters.Count; p++)
                    stage.Parameters.Add(ConvertParameter(descriptor.Parameters[p]));
            }

            stages.Add(stage);
        }

        return stages;
    }

    private static AuthoredPostProcessParameter ConvertParameter(PostProcessParameterDescriptor descriptor)
    {
        AuthoredPostProcessParameter parameter = new()
        {
            Name = descriptor.Name,
            DisplayName = descriptor.DisplayName,
            Kind = descriptor.Kind,
            Min = descriptor.Min,
            Max = descriptor.Max,
            Step = descriptor.Step,
            IsColor = descriptor.IsColor,
        };

        if (descriptor.DefaultValue is { } value)
        {
            try
            {
                parameter.SetDefaultValue(value);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidCastException)
            {
                // A default of an unexpected type keeps the zero default of the parameter.
            }
        }

        return parameter;
    }
}
