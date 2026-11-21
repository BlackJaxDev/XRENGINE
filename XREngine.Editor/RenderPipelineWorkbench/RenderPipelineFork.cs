using System.Reflection;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Makes an editable <see cref="CustomRenderPipeline"/> from any pipeline. The fork copies the command tree, the scene
/// passes, the resource layout of one profile, and the schema. It never changes the source pipeline.
/// </summary>
/// <remarks>
/// A fork cannot keep code. Each code delegate on a command goes back to the default of the command type, and each
/// code branch loses its condition. The validator reports a branch without a condition as an error, so a fork cannot be
/// applied until the user sets each condition. Resources that only a factory can make are listed as unconverted, and
/// the commands that read them report undeclared reads.
/// </remarks>
public static class RenderPipelineFork
{
    /// <summary>Creates a fork of <paramref name="source"/> for <paramref name="profile"/>.</summary>
    public static RenderPipelineForkResult Create(RenderPipeline source, RenderPipelineResourceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<string> unconverted = [];

        ViewportRenderCommandContainer commands = source.CommandChain is { } chain
            ? RenderCommandGraphCloner.CloneContainer(chain)
            : new ViewportRenderCommandContainer();
        RenderPipelineDraftEdits.ForEachCommand(commands, command => StripCode(command, unconverted));

        List<AuthoredRenderResource> resources = [];
        try
        {
            resources = AuthoredRenderResourceConverter.FromLayout(source.DescribeResourceLayout(profile), unconverted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            unconverted.Add(string.Concat("Resource layout: ", ex.Message));
        }

        List<AuthoredPostProcessStage> stages = AuthoredPostProcessSchemaConverter.FromSchema(source.PostProcessSchema ?? RenderPipelinePostProcessSchema.Empty);
        CustomRenderPipeline fork = new()
        {
            AuthoredResources = resources,
            AuthoredStages = stages,
            RenderPasses = new Dictionary<int, IComparer<RenderCommand>?>(source.PassIndicesAndSorters ?? []),
        };
        fork.Commands = commands;
        return new RenderPipelineForkResult(fork, unconverted);
    }

    /// <summary>
    /// Sets each delegate property of <paramref name="command"/> that differs from the type default back to the
    /// default, and records it.
    /// </summary>
    private static void StripCode(ViewportRenderCommand command, List<string> unconverted)
    {
        PropertyInfo[] properties = RenderCommandGraphCloner.GetCopiedProperties(command.GetType());
        ViewportRenderCommand? defaults = null;
        for (int i = 0; i < properties.Length; i++)
        {
            PropertyInfo property = properties[i];
            if (!typeof(Delegate).IsAssignableFrom(property.PropertyType) || property.GetValue(command) is not Delegate value)
                continue;

            defaults ??= RenderCommandGraphCloner.CreateCommand(command.GetType());
            object? defaultValue = property.GetValue(defaults);
            if (Equals(value, defaultValue))
                continue;

            property.SetValue(command, defaultValue);
            unconverted.Add(string.Concat(RenderCommandInfo.Get(command).DisplayName, ".", property.Name, ": code delegate removed."));
        }
    }
}
