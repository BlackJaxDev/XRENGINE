using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// One horizontal lane of the Flow view. Each lane shows the commands of one container.
/// </summary>
public sealed class RenderPipelineTimelineLane
{
    /// <summary>Gets the stable node id of the container.</summary>
    public required int Id { get; init; }

    /// <summary>Gets the vertical lane position.</summary>
    public required int LaneIndex { get; init; }

    /// <summary>Gets the lane title.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the subtitle, which names the branch resource behavior.</summary>
    public required string Detail { get; init; }

    /// <summary>Gets the container.</summary>
    public required ViewportRenderCommandContainer Container { get; init; }

    /// <summary>Gets the first order column of the lane.</summary>
    public required int StartOrder { get; init; }

    /// <summary>Gets the order column one past the end of the lane.</summary>
    public int EndOrderExclusive { get; set; }

    /// <summary>Gets the node id of the flow command that owns the lane, or null for the root lane.</summary>
    public int? ParentCommandId { get; init; }

    /// <summary>Gets the steps of the lane in command order.</summary>
    public List<RenderPipelineTimelineStep> Steps { get; } = [];
}
