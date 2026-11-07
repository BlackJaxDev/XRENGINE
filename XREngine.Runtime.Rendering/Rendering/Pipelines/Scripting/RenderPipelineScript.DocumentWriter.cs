using System.Globalization;
using System.Reflection;
using System.Text;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Overlays;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering;

// Writes pipelines as documents.
public sealed partial class RenderPipelineScript
{
    /// <summary>The first line of each written script.</summary>
    internal const string ScriptHeaderLine = "// XRENGINE render pipeline script.";

    private static readonly string[] s_resourceSkip = [nameof(AuthoredRenderResource.Name), nameof(AuthoredRenderResource.Kind)];
    private static readonly string[] s_parameterSkip = [nameof(AuthoredPostProcessParameter.Name)];

    internal static string WritePipeline(RenderPipeline pipeline)
    {
        if (pipeline.Overlays is { Count: > 0 } overlays && overlays[^1].ParentPath is not null)
            return WriteDerivedFromParent(pipeline, overlays[^1]);

        if (pipeline is CustomRenderPipeline custom)
            return WriteStandalone(custom);

        if (RenderPipelineScriptBases.TryGet(pipeline.GetType(), out RenderPipelineScriptBase? entry) && entry is not null)
            return WriteDerived(pipeline, entry);

        throw new InvalidOperationException($"No script base is registered for {pipeline.GetType().Name}. Use the expanded export.");
    }

    internal static string WriteExpandedPipeline(RenderPipeline pipeline, RenderPipelineResourceProfile profile, List<string> unconverted)
    {
        if (pipeline is CustomRenderPipeline custom)
            return WriteStandalone(custom);

        List<string> headerNotes = [];
        List<AuthoredRenderResource> resources;
        try
        {
            resources = AuthoredRenderResourceConverter.FromLayout(pipeline.DescribeResourceLayout(profile), headerNotes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            resources = [];
            headerNotes.Add(string.Concat("resource layout: ", ex.Message));
        }

        List<AuthoredPostProcessStage> stages = AuthoredPostProcessSchemaConverter.FromSchema(pipeline.PostProcessSchema ?? RenderPipelinePostProcessSchema.Empty);

        StringBuilder builder = new();
        builder.AppendLine(ScriptHeaderLine);
        builder.Append("// Expanded from ").Append(pipeline.GetType().Name).AppendLine(" for one resource profile.");
        StringBuilder body = new();
        AppendPasses(body, pipeline.PassIndicesAndSorters, [], headerNotes);
        AppendResources(body, resources);
        AppendSchema(body, stages);
        for (int i = 0; i < headerNotes.Count; i++)
            builder.Append("// ").Append(UnconvertedCommentPrefix).Append(' ').AppendLine(headerNotes[i]);
        unconverted.AddRange(headerNotes);

        builder.Append(body);
        if (pipeline.CommandChain is { Count: > 0 } chain)
        {
            builder.AppendLine();
            ScriptExporter.AppendContainer(builder, chain, 0, unconverted);
        }

        return builder.ToString();
    }

    internal static string WriteDerivedFromScript(string parentPath)
    {
        StringBuilder builder = new();
        builder.AppendLine(ScriptHeaderLine);
        builder.Append("derive ").Append(ScriptExporter.Quote(parentPath.Replace('\\', '/'))).AppendLine(";");
        return builder.ToString();
    }

    private static string WriteStandalone(CustomRenderPipeline pipeline)
    {
        StringBuilder builder = new();
        builder.AppendLine(ScriptHeaderLine);
        if (pipeline.GetType() == typeof(CustomRenderPipeline))
            AppendSettings(builder, pipeline, new CustomRenderPipeline());

        AppendPasses(builder, pipeline.RenderPasses, pipeline.PassDependencies, null);
        AppendResources(builder, pipeline.AuthoredResources);
        AppendSchema(builder, pipeline.AuthoredStages);

        ViewportRenderCommandContainer? commands = pipeline.Commands ?? pipeline.CommandChain;
        if (commands is { Count: > 0 })
        {
            builder.AppendLine();
            ScriptExporter.AppendContainer(builder, commands, 0, null);
        }

        return builder.ToString();
    }

    private static string WriteDerived(RenderPipeline pipeline, RenderPipelineScriptBase entry)
    {
        object?[] arguments = entry.GetArguments(pipeline);
        StringBuilder builder = new();
        builder.AppendLine(ScriptHeaderLine);
        IReadOnlyList<RenderPipelineOverlay> overlays = pipeline.Overlays;
        if (HasPatches(overlays, 0))
            AppendBaseHash(builder, pipeline.ComputeBaseStructureHash(0));
        builder.Append("derive ").Append(entry.Name);
        int written = 0;
        for (int i = 0; i < entry.Parameters.Count; i++)
        {
            RenderPipelineScriptBaseParameter parameter = entry.Parameters[i];
            if (Equals(arguments[i], parameter.DefaultValue) || !ScriptExporter.TrySerializeValue(arguments[i], out string? text))
                continue;

            builder.Append(written == 0 ? "(" : ", ").Append(parameter.Name).Append(": ").Append(text);
            written++;
        }

        if (written > 0)
            builder.Append(')');
        builder.AppendLine(";");

        RenderPipeline defaults = entry.Create(arguments);
        AppendSettings(builder, pipeline, defaults);
        // A code base flattens every overlay into one, in apply order.
        for (int i = 0; i < overlays.Count; i++)
            AppendPatches(builder, overlays[i]);
        return builder.ToString();
    }

    /// <summary>
    /// Writes a pipeline whose last overlay came from <c>derive "parent.xrs";</c>: the parent path, the settings that
    /// differ from the parent, and the patches of the last overlay.
    /// </summary>
    private static string WriteDerivedFromParent(RenderPipeline pipeline, RenderPipelineOverlay overlay)
    {
        StringBuilder builder = new();
        builder.AppendLine(ScriptHeaderLine);
        int layersBelow = pipeline.Overlays.Count - 1;
        if (overlay.PatchCount > 0)
            AppendBaseHash(builder, pipeline.ComputeBaseStructureHash(layersBelow));
        builder.Append("derive ").Append(ScriptExporter.Quote(overlay.ParentPath!.Replace('\\', '/'))).AppendLine(";");

        RenderPipeline? parent = null;
        if (overlay.ParentFullPath is { } parentPath && File.Exists(parentPath))
            parent = RenderPipelineScriptLoader.LoadFile(parentPath).Pipeline;

        if (parent is not null && parent.GetType() == pipeline.GetType())
            AppendSettings(builder, pipeline, parent);
        else
            AppendSettingOverrides(builder, overlay.SettingOverrides);

        AppendPatches(builder, overlay);
        return builder.ToString();
    }

    private static bool HasPatches(IReadOnlyList<RenderPipelineOverlay> overlays, int start)
    {
        for (int i = start; i < overlays.Count; i++)
        {
            if (overlays[i].PatchCount > 0)
                return true;
        }

        return false;
    }

    private static void AppendBaseHash(StringBuilder builder, ulong hash)
        => builder.Append("// ").Append(BaseHashCommentPrefix).Append(' ').AppendLine(RenderPipelineStructureHash.Format(hash));

    private static void AppendSettingOverrides(StringBuilder builder, Dictionary<string, string> overrides)
    {
        if (overrides.Count == 0)
            return;

        builder.AppendLine().AppendLine("settings {");
        foreach ((string name, string value) in overrides)
            builder.Append("    ").Append(name).Append(" = ").Append(value).AppendLine(";");
        builder.AppendLine("}");
    }

    /// <summary>Writes the resource patches, the schema patches, and the command patches of <paramref name="overlay"/>.</summary>
    private static void AppendPatches(StringBuilder builder, RenderPipelineOverlay overlay)
    {
        if (overlay.ResourcePatches.Count > 0)
        {
            AuthoredRenderResource defaults = new();
            builder.AppendLine().AppendLine("resources {");
            for (int i = 0; i < overlay.ResourcePatches.Count; i++)
            {
                RenderResourcePatch patch = overlay.ResourcePatches[i];
                if (patch.Kind == ERenderResourcePatchKind.Remove || patch.Resource is null)
                {
                    builder.Append("    remove ").Append(ScriptExporter.FormatName(patch.Name)).AppendLine(";");
                    continue;
                }

                AuthoredRenderResource resource = patch.Resource.Clone();
                resource.Name = patch.Name;
                AppendResourceLine(builder, resource, defaults, patch.Kind == ERenderResourcePatchKind.Override ? "override " : string.Empty);
            }

            builder.AppendLine("}");
        }

        if (overlay.SchemaPatches.Count > 0)
        {
            AuthoredPostProcessStage stageDefaults = new();
            AuthoredPostProcessParameter parameterDefaults = new();
            builder.AppendLine().AppendLine("schema {");
            for (int i = 0; i < overlay.SchemaPatches.Count; i++)
            {
                PostProcessSchemaPatch patch = overlay.SchemaPatches[i];
                string key = ScriptExporter.FormatName(patch.StageKey);
                switch (patch.Kind)
                {
                    case EPostProcessSchemaPatchKind.AddStage when patch.Stage is not null:
                        AuthoredPostProcessStage stage = patch.Stage.Clone();
                        stage.Key = patch.StageKey;
                        AppendStageLines(builder, stage, i, stageDefaults, parameterDefaults);
                        break;
                    case EPostProcessSchemaPatchKind.HideStage:
                        builder.Append("    hide ").Append(key).AppendLine(";");
                        break;
                    case EPostProcessSchemaPatchKind.OverrideDefault:
                        builder.Append("    default ").Append(key).Append('.').Append(ScriptExporter.FormatName(patch.ParameterName ?? string.Empty))
                            .Append(" = ").Append(patch.ValueText).AppendLine(";");
                        break;
                    case EPostProcessSchemaPatchKind.MoveToCategory:
                        builder.Append("    category ").Append(key).Append(" = ").Append(ScriptExporter.FormatName(patch.CategoryKey ?? string.Empty)).AppendLine(";");
                        break;
                }
            }

            builder.AppendLine("}");
        }

        for (int i = 0; i < overlay.CommandPatches.Count; i++)
            AppendCommandPatch(builder, overlay.CommandPatches[i]);
    }

    private static void AppendCommandPatch(StringBuilder builder, RenderCommandPatch patch)
    {
        string target = FormatTarget(patch.Target);
        builder.AppendLine();
        switch (patch.Kind)
        {
            case ERenderCommandPatchKind.Remove:
                builder.Append("remove(").Append(target).AppendLine(");");
                return;
            case ERenderCommandPatchKind.SetProperty:
                builder.Append("set(").Append(target).Append(").").Append(patch.PropertyName).Append(" = ").Append(patch.ValueText).AppendLine(";");
                return;
            case ERenderCommandPatchKind.SetCondition:
                builder.Append("condition(").Append(target).Append(") = (")
                    .Append(patch.Condition is null ? CodeConditionText : ScriptExporter.FormatCondition(patch.Condition)).AppendLine(");");
                return;
            case ERenderCommandPatchKind.Wrap when patch.Condition is not null && string.IsNullOrWhiteSpace(patch.WrapScopeText):
                builder.Append("wrap(").Append(target).Append(") if (").Append(ScriptExporter.FormatCondition(patch.Condition)).Append(')');
                if (!string.IsNullOrEmpty(patch.WrapLabel))
                    builder.Append(" as ").Append(ScriptExporter.Quote(patch.WrapLabel));
                builder.AppendLine(";");
                return;
        }

        string word = patch.Kind switch
        {
            ERenderCommandPatchKind.InsertBefore => "before",
            ERenderCommandPatchKind.InsertAfter => "after",
            ERenderCommandPatchKind.Replace => "replace",
            ERenderCommandPatchKind.Wrap => "wrap",
            _ => "append",
        };

        builder.Append(word).Append('(').Append(target).AppendLine(") {");
        string body = patch.Kind == ERenderCommandPatchKind.Wrap ? patch.WrapScopeText ?? string.Empty : patch.CommandText;
        foreach (string line in body.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Length > 0)
                builder.Append("    ").AppendLine(line);
        }

        builder.AppendLine("}");
    }

    /// <summary>Writes an anchor id as is when it is a dotted name, and quotes any other target.</summary>
    private static string FormatTarget(string target)
        => ScriptExporter.FormatAnchor(target);

    private static void AppendSettings(StringBuilder builder, RenderPipeline pipeline, RenderPipeline defaults)
    {
        IReadOnlyList<PropertyInfo> settings = RenderPipelineScriptSettings.GetSettingProperties(pipeline.GetType());
        bool opened = false;
        for (int i = 0; i < settings.Count; i++)
        {
            PropertyInfo property = settings[i];
            if (!ScriptExporter.TrySerializeValue(property.GetValue(pipeline), out string? text))
                continue;
            if (ScriptExporter.TrySerializeValue(property.GetValue(defaults), out string? defaultText) && string.Equals(text, defaultText, StringComparison.Ordinal))
                continue;

            if (!opened)
            {
                builder.AppendLine().AppendLine("settings {");
                opened = true;
            }

            builder.Append("    ").Append(property.Name).Append(" = ").Append(text).AppendLine(";");
        }

        if (opened)
            builder.AppendLine("}");
    }

    private static void AppendPasses(
        StringBuilder builder,
        IReadOnlyDictionary<int, IComparer<RenderCommand>?>? passes,
        IReadOnlyList<AuthoredPassDependency> dependencies,
        List<string>? unconverted)
    {
        if ((passes is null || passes.Count == 0) && dependencies.Count == 0)
            return;

        builder.AppendLine().AppendLine("passes {");
        if (passes is not null)
        {
            List<int> indices = [.. passes.Keys];
            indices.Sort();
            for (int i = 0; i < indices.Count; i++)
            {
                IComparer<RenderCommand>? sorter = passes[indices[i]];
                string? sorterName = RenderPipelineScriptSorters.GetName(sorter);
                if (sorterName is null)
                {
                    unconverted?.Add(string.Concat("sorter ", sorter!.GetType().Name, " of scene pass ", indices[i].ToString(CultureInfo.InvariantCulture)));
                    sorterName = RenderPipelineScriptSorters.None;
                }

                builder.Append("    scene ").Append(indices[i].ToString(CultureInfo.InvariantCulture));
                if (sorterName != RenderPipelineScriptSorters.None)
                    builder.Append(" sorter ").Append(sorterName);
                builder.AppendLine(";");
            }
        }

        for (int i = 0; i < dependencies.Count; i++)
        {
            builder.Append("    depends ").Append(dependencies[i].PassIndex.ToString(CultureInfo.InvariantCulture))
                .Append(" on ").Append(dependencies[i].DependsOnPassIndex.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
        }

        builder.AppendLine("}");
    }

    private static void AppendResources(StringBuilder builder, IReadOnlyList<AuthoredRenderResource> resources)
    {
        if (resources.Count == 0)
            return;

        AuthoredRenderResource defaults = new();
        builder.AppendLine().AppendLine("resources {");
        for (int i = 0; i < resources.Count; i++)
            AppendResourceLine(builder, resources[i], defaults, string.Empty);

        builder.AppendLine("}");
    }

    private static void AppendResourceLine(StringBuilder builder, AuthoredRenderResource resource, AuthoredRenderResource defaults, string prefix)
    {
        builder.Append("    ").Append(prefix).Append(resource.Kind.ToString()).Append(' ').Append(ScriptExporter.FormatName(resource.Name));
        AppendObject(builder, resource, defaults, s_resourceSkip);
        builder.AppendLine(";");
    }

    private static void AppendSchema(StringBuilder builder, IReadOnlyList<AuthoredPostProcessStage> stages)
    {
        if (stages.Count == 0)
            return;

        AuthoredPostProcessStage defaults = new();
        AuthoredPostProcessParameter parameterDefaults = new();
        builder.AppendLine().AppendLine("schema {");
        for (int i = 0; i < stages.Count; i++)
            AppendStageLines(builder, stages[i], i, defaults, parameterDefaults);

        builder.AppendLine("}");
    }

    /// <summary>Writes one stage declaration. The order is written only when it differs from <paramref name="index"/>.</summary>
    private static void AppendStageLines(StringBuilder builder, AuthoredPostProcessStage stage, int index, AuthoredPostProcessStage defaults, AuthoredPostProcessParameter parameterDefaults)
    {
        string[] skip = stage.Order == index
            ? [nameof(AuthoredPostProcessStage.Key), nameof(AuthoredPostProcessStage.IsLibraryStage), nameof(AuthoredPostProcessStage.Parameters), nameof(AuthoredPostProcessStage.Order)]
            : [nameof(AuthoredPostProcessStage.Key), nameof(AuthoredPostProcessStage.IsLibraryStage), nameof(AuthoredPostProcessStage.Parameters)];

        builder.Append("    ").Append(stage.IsLibraryStage ? "library " : "stage ").Append(ScriptExporter.FormatName(stage.Key));
        AppendObject(builder, stage, defaults, skip);
        if (stage.Parameters.Count == 0)
        {
            builder.AppendLine(";");
            return;
        }

        builder.AppendLine(" {");
        for (int p = 0; p < stage.Parameters.Count; p++)
        {
            AuthoredPostProcessParameter parameter = stage.Parameters[p];
            builder.Append("        param ").Append(ScriptExporter.FormatName(parameter.Name));
            AppendObject(builder, parameter, parameterDefaults, s_parameterSkip);
            builder.AppendLine(";");
        }

        builder.AppendLine("    }");
    }

    /// <summary>Appends <c>(Name=value, ...)</c> for the properties that differ from <paramref name="defaults"/>, or nothing.</summary>
    private static void AppendObject(StringBuilder builder, object value, object defaults, string[] skip)
    {
        StringBuilder properties = new();
        if (ScriptExporter.AppendObjectProperties(properties, value, defaults, skip) > 0)
            builder.Append('(').Append(properties).Append(')');
    }
}
