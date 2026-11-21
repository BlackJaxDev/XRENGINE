namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>One queue submission of a plan snapshot.</summary>
/// <param name="SubmissionIndex">The submission index in plan order.</param>
/// <param name="Label">The queue or submission label.</param>
/// <param name="BatchIndices">The batches of the submission in execution order.</param>
public sealed record PlanSubmissionSnapshot(
    int SubmissionIndex,
    string Label,
    IReadOnlyList<int> BatchIndices);
