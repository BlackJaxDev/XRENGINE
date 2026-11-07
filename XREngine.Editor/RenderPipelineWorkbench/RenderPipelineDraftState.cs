using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The editable data of a custom render pipeline: commands, scene passes, pass dependencies, resource declarations,
/// and stage declarations. The Workbench edits a state in place and keeps copies for undo.
/// </summary>
public sealed class RenderPipelineDraftState
{
    /// <summary>Gets or sets the root command container. It has no parent pipeline.</summary>
    public ViewportRenderCommandContainer Commands { get; set; } = new();

    /// <summary>Gets the scene pass indices and their sorters. A null sorter keeps submission order.</summary>
    public Dictionary<int, IComparer<RenderCommand>?> RenderPasses { get; set; } = [];

    /// <summary>Gets the explicit pass dependencies.</summary>
    public List<AuthoredPassDependency> PassDependencies { get; set; } = [];

    /// <summary>Gets the resource declarations.</summary>
    public List<AuthoredRenderResource> Resources { get; set; } = [];

    /// <summary>Gets the post-process stage declarations.</summary>
    public List<AuthoredPostProcessStage> Stages { get; set; } = [];

    /// <summary>Reads a deep copy of the authored data of <paramref name="pipeline"/>.</summary>
    public static RenderPipelineDraftState From(CustomRenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        RenderPipelineDraftState state = new()
        {
            Commands = pipeline.Commands is { } commands ? RenderCommandGraphCloner.CloneContainer(commands) : new ViewportRenderCommandContainer(),
            RenderPasses = pipeline.RenderPasses is { } passes ? new Dictionary<int, IComparer<RenderCommand>?>(passes) : [],
        };

        CopyInto(pipeline.PassDependencies, state.PassDependencies, static d => d.Clone());
        CopyInto(pipeline.AuthoredResources, state.Resources, static r => r.Clone());
        CopyInto(pipeline.AuthoredStages, state.Stages, static s => s.Clone());
        return state;
    }

    /// <summary>Returns a deep copy of this state.</summary>
    public RenderPipelineDraftState Clone()
    {
        RenderPipelineDraftState copy = new()
        {
            Commands = RenderCommandGraphCloner.CloneContainer(Commands),
            RenderPasses = new Dictionary<int, IComparer<RenderCommand>?>(RenderPasses),
        };

        CopyInto(PassDependencies, copy.PassDependencies, static d => d.Clone());
        CopyInto(Resources, copy.Resources, static r => r.Clone());
        CopyInto(Stages, copy.Stages, static s => s.Clone());
        return copy;
    }

    /// <summary>
    /// Writes a deep copy of this state to <paramref name="pipeline"/>. Call it on the render thread. The pipeline
    /// builds a pending generation through its normal lifecycle, and the active generation renders until it commits.
    /// </summary>
    public void WriteTo(CustomRenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        RenderPipelineDraftState copy = Clone();

        // Resources and stages go first, so the command rebuild sees the final layout and schema.
        pipeline.AuthoredResources = copy.Resources;
        pipeline.AuthoredStages = copy.Stages;
        pipeline.RenderPasses = copy.RenderPasses;
        pipeline.PassDependencies = copy.PassDependencies;
        pipeline.Commands = copy.Commands;
    }

    private static void CopyInto<T>(List<T>? source, List<T> target, Func<T, T> clone)
    {
        if (source is null)
            return;

        target.Capacity = Math.Max(target.Capacity, source.Count);
        for (int i = 0; i < source.Count; i++)
            target.Add(clone(source[i]));
    }
}
