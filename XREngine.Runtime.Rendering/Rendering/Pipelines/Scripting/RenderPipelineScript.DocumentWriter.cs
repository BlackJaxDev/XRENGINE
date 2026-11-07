using System.Globalization;
using System.Reflection;
using System.Text;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
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
        return builder.ToString();
    }

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
        {
            AuthoredRenderResource resource = resources[i];
            builder.Append("    ").Append(resource.Kind.ToString()).Append(' ').Append(ScriptExporter.FormatName(resource.Name));
            AppendObject(builder, resource, defaults, s_resourceSkip);
            builder.AppendLine(";");
        }

        builder.AppendLine("}");
    }

    private static void AppendSchema(StringBuilder builder, IReadOnlyList<AuthoredPostProcessStage> stages)
    {
        if (stages.Count == 0)
            return;

        AuthoredPostProcessStage defaults = new();
        AuthoredPostProcessParameter parameterDefaults = new();
        builder.AppendLine().AppendLine("schema {");
        for (int i = 0; i < stages.Count; i++)
        {
            AuthoredPostProcessStage stage = stages[i];
            string[] skip = stage.Order == i
                ? [nameof(AuthoredPostProcessStage.Key), nameof(AuthoredPostProcessStage.IsLibraryStage), nameof(AuthoredPostProcessStage.Parameters), nameof(AuthoredPostProcessStage.Order)]
                : [nameof(AuthoredPostProcessStage.Key), nameof(AuthoredPostProcessStage.IsLibraryStage), nameof(AuthoredPostProcessStage.Parameters)];

            builder.Append("    ").Append(stage.IsLibraryStage ? "library " : "stage ").Append(ScriptExporter.FormatName(stage.Key));
            AppendObject(builder, stage, defaults, skip);
            if (stage.Parameters.Count == 0)
            {
                builder.AppendLine(";");
                continue;
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

        builder.AppendLine("}");
    }

    /// <summary>Appends <c>(Name=value, ...)</c> for the properties that differ from <paramref name="defaults"/>, or nothing.</summary>
    private static void AppendObject(StringBuilder builder, object value, object defaults, string[] skip)
    {
        StringBuilder properties = new();
        if (ScriptExporter.AppendObjectProperties(properties, value, defaults, skip) > 0)
            builder.Append('(').Append(properties).Append(')');
    }
}
