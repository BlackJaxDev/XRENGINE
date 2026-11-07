using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// One command in a <see cref="RenderPipelineStructureSnapshot"/>, in depth-first order.
/// </summary>
public sealed class RenderPipelineStructureCommand
{
    internal RenderPipelineStructureCommand(
        int index,
        ViewportRenderCommand command,
        int parentIndex,
        int depth,
        ViewportRenderCommandContainer container,
        int indexInContainer,
        string containerName,
        string profilingPath)
    {
        Index = index;
        Command = command;
        ParentIndex = parentIndex;
        Depth = depth;
        Container = container;
        IndexInContainer = indexInContainer;
        ContainerName = containerName;
        ProfilingPath = profilingPath;
        Info = RenderCommandInfo.Get(command);
    }

    /// <summary>Gets the depth-first index of the command in the snapshot.</summary>
    public int Index { get; }

    /// <summary>Gets the command.</summary>
    public ViewportRenderCommand Command { get; }

    /// <summary>Gets the index of the flow command that owns the container of this command, or -1 for the root container.</summary>
    public int ParentIndex { get; }

    /// <summary>Gets the nesting depth. Commands in the root container have depth 0.</summary>
    public int Depth { get; }

    /// <summary>Gets the container that holds the command.</summary>
    public ViewportRenderCommandContainer Container { get; }

    /// <summary>Gets the position of the command in its container.</summary>
    public int IndexInContainer { get; }

    /// <summary>Gets the name of the container, for example <c>CommandChain</c>, <c>True</c>, or <c>Case 2</c>.</summary>
    public string ContainerName { get; }

    /// <summary>
    /// Gets the GPU profiler path of the command below the pipeline root, with <c>/</c> between scope names.
    /// It matches the scope names that <see cref="ViewportRenderCommand.GpuProfilingName"/> produces.
    /// </summary>
    public string ProfilingPath { get; }

    /// <summary>Gets the tool description of the command type.</summary>
    public RenderCommandInfo Info { get; }

    /// <summary>Gets the indices of the passes that the command described, in ascending order.</summary>
    public IReadOnlyList<int> PassIndices { get; internal set; } = [];

    /// <summary>Gets the index one past the last command nested below this command.</summary>
    public int SubtreeEnd { get; internal set; }

    /// <summary>Gets whether the command does GPU work but described no pass.</summary>
    public bool IsGpuWorkWithoutPass => Info.DoesGpuWork && PassIndices.Count == 0;
}
