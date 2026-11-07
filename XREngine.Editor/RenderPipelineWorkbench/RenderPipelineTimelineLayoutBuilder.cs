using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Builds the Flow view layout. Each container becomes a lane; branch lanes start one column after
/// their flow command, and the parent lane resumes after the longest branch.
/// The builder reuses its scratch lists and the target layout storage.
/// </summary>
public sealed class RenderPipelineTimelineLayoutBuilder
{
    private const string RootLabel = "CommandChain";
    private readonly List<RenderCommandChildContainer> _childScratch = new(8);
    private readonly HashSet<ViewportRenderCommandContainer> _visited = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ViewportRenderCommandContainer.BranchResourceBehavior, string> _behaviorLabels = [];

    /// <summary>
    /// Rebuilds <paramref name="layout"/> for <paramref name="root"/>. Call it when the command generation changes.
    /// </summary>
    public void Build(ViewportRenderCommandContainer root, RenderPipelineTimelineLayout layout)
    {
        layout.Clear();
        _visited.Clear();
        _childScratch.Clear();

        int nextLaneIndex = 1;
        int endOrder = BuildLane(root, layout, ref nextLaneIndex, laneIndex: 0, startOrder: 0, RootLabel, parentCommandId: null);
        layout.MaxOrder = Math.Max(1, endOrder);
        layout.Lanes.Sort(static (left, right) => left.LaneIndex.CompareTo(right.LaneIndex));
        layout.Steps.Sort(static (left, right) =>
        {
            int lane = left.LaneIndex.CompareTo(right.LaneIndex);
            return lane != 0 ? lane : left.CommandIndex.CompareTo(right.CommandIndex);
        });
    }

    private int BuildLane(
        ViewportRenderCommandContainer container,
        RenderPipelineTimelineLayout layout,
        ref int nextLaneIndex,
        int laneIndex,
        int startOrder,
        string label,
        int? parentCommandId)
    {
        RenderPipelineTimelineLane lane = new()
        {
            Id = RenderPipelineWorkbenchNodeIds.Get(container),
            LaneIndex = laneIndex,
            Label = label,
            Detail = GetBehaviorLabel(container.BranchResources),
            Container = container,
            StartOrder = startOrder,
            EndOrderExclusive = startOrder + 1,
            ParentCommandId = parentCommandId,
        };
        layout.Lanes.Add(lane);
        layout.LaneById[lane.Id] = lane;

        if (!_visited.Add(container))
            return lane.EndOrderExclusive;

        int currentOrder = startOrder;
        for (int commandIndex = 0; commandIndex < container.Count; commandIndex++)
        {
            ViewportRenderCommand command = container[commandIndex];
            int commandId = RenderPipelineWorkbenchNodeIds.Get(command);

            int childStart = _childScratch.Count;
            command.CollectChildContainers(_childScratch);
            int childEnd = _childScratch.Count;

            int resumeOrderExclusive = currentOrder + 1;
            string branchSummary = string.Empty;
            for (int c = childStart; c < childEnd; c++)
            {
                RenderCommandChildContainer child = _childScratch[c];
                branchSummary = branchSummary.Length == 0 ? child.Name : string.Concat(branchSummary, " | ", child.Name);
                int childLaneIndex = nextLaneIndex++;
                int childEndOrder = BuildLane(child.Container, layout, ref nextLaneIndex, childLaneIndex, currentOrder + 1, child.Name, commandId);
                resumeOrderExclusive = Math.Max(resumeOrderExclusive, childEndOrder);
            }

            _childScratch.RemoveRange(childStart, childEnd - childStart);

            RenderPipelineTimelineStep step = new()
            {
                Id = commandId,
                LaneIndex = laneIndex,
                OrderIndex = currentOrder,
                ResumeOrderExclusive = resumeOrderExclusive,
                Command = command,
                Container = container,
                CommandIndex = commandIndex,
                Title = RenderPipelineWorkbenchText.GetCommandLabel(command),
                IndexLabel = string.Concat("#", RenderPipelineWorkbenchText.FormatCount(commandIndex + 1)),
                BranchSummary = branchSummary,
                Category = RenderCommandInfo.Get(command).Category,
            };
            layout.Steps.Add(step);
            layout.StepById[commandId] = step;
            lane.Steps.Add(step);

            currentOrder = resumeOrderExclusive;
        }

        AddStateScopes(lane, layout);
        lane.EndOrderExclusive = Math.Max(startOrder + 1, currentOrder);
        layout.MaxOrder = Math.Max(layout.MaxOrder, lane.EndOrderExclusive);
        return lane.EndOrderExclusive;
    }

    /// <summary>
    /// Adds a scope for each push command whose pop command is later in the same lane.
    /// The nesting level counts the scopes of the lane that already enclose the new scope.
    /// </summary>
    private static void AddStateScopes(RenderPipelineTimelineLane lane, RenderPipelineTimelineLayout layout)
    {
        int laneScopeStart = layout.Scopes.Count;
        List<RenderPipelineTimelineStep> steps = lane.Steps;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Command is not ViewportStateRenderCommandBase push)
                continue;

            ViewportPopStateRenderCommand pop = push.PairedPopCommand;
            for (int j = i + 1; j < steps.Count; j++)
            {
                if (!ReferenceEquals(steps[j].Command, pop))
                    continue;

                int startOrder = steps[i].OrderIndex;
                int endOrder = steps[j].OrderIndex;
                int level = 0;
                for (int s = laneScopeStart; s < layout.Scopes.Count; s++)
                {
                    RenderPipelineStateScope other = layout.Scopes[s];
                    if (other.StartOrder <= startOrder && other.EndOrder >= endOrder)
                        level = Math.Max(level, other.NestingLevel + 1);
                }

                layout.Scopes.Add(new RenderPipelineStateScope(lane.LaneIndex, startOrder, endOrder, level, steps[i].Id));
                break;
            }
        }
    }

    private string GetBehaviorLabel(ViewportRenderCommandContainer.BranchResourceBehavior behavior)
    {
        if (!_behaviorLabels.TryGetValue(behavior, out string? label))
        {
            label = behavior.ToString();
            _behaviorLabels.Add(behavior, label);
        }

        return label;
    }
}
