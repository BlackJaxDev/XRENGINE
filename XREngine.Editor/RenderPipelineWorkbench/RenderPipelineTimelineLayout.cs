namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The cached lanes and steps of the Flow view for one command generation.
/// </summary>
public sealed class RenderPipelineTimelineLayout
{
    /// <summary>Gets the lanes, ordered by <see cref="RenderPipelineTimelineLane.LaneIndex"/>.</summary>
    public List<RenderPipelineTimelineLane> Lanes { get; } = new(16);

    /// <summary>Gets every step, ordered by lane and command index.</summary>
    public List<RenderPipelineTimelineStep> Steps { get; } = new(64);

    /// <summary>Gets the steps by command node id.</summary>
    public Dictionary<int, RenderPipelineTimelineStep> StepById { get; } = new(64);

    /// <summary>Gets the lanes by container node id.</summary>
    public Dictionary<int, RenderPipelineTimelineLane> LaneById { get; } = new(16);

    /// <summary>Gets the largest order column of any lane.</summary>
    public int MaxOrder { get; internal set; }

    /// <summary>Removes all lanes and steps but keeps the storage.</summary>
    public void Clear()
    {
        Lanes.Clear();
        Steps.Clear();
        StepById.Clear();
        LaneById.Clear();
        MaxOrder = 0;
    }
}
