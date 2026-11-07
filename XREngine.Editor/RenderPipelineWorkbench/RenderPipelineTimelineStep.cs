using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// One command block in the Flow view.
/// </summary>
public sealed class RenderPipelineTimelineStep
{
    /// <summary>Gets the stable node id of the command.</summary>
    public required int Id { get; init; }

    /// <summary>Gets the lane of the step.</summary>
    public required int LaneIndex { get; init; }

    /// <summary>Gets the order column of the step.</summary>
    public required int OrderIndex { get; init; }

    /// <summary>Gets the order column where the lane continues after the branches of the step end.</summary>
    public required int ResumeOrderExclusive { get; init; }

    /// <summary>Gets the command.</summary>
    public required ViewportRenderCommand Command { get; init; }

    /// <summary>Gets the container that holds the command.</summary>
    public required ViewportRenderCommandContainer Container { get; init; }

    /// <summary>Gets the position of the command in its container.</summary>
    public required int CommandIndex { get; init; }

    /// <summary>Gets the block title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the position text, for example <c>#3</c>.</summary>
    public required string IndexLabel { get; init; }

    /// <summary>Gets the branch names joined with <c> | </c>, or an empty string.</summary>
    public required string BranchSummary { get; init; }

    /// <summary>Gets whether the command owns nested containers.</summary>
    public bool HasBranches => BranchSummary.Length > 0;

    /// <summary>Gets the command category.</summary>
    public ERenderCommandCategory Category { get; init; }
}
