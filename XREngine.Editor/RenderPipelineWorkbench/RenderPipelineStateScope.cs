namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// A push command and its paired pop command in one Flow lane. The Flow view draws it as a bracket over the steps
/// that run with the pushed state.
/// </summary>
/// <param name="LaneIndex">The lane that contains both commands.</param>
/// <param name="StartOrder">The order column of the push command.</param>
/// <param name="EndOrder">The order column of the pop command.</param>
/// <param name="NestingLevel">The number of other scopes in the lane that contain this scope.</param>
/// <param name="PushNodeId">The node id of the push command.</param>
public readonly record struct RenderPipelineStateScope(int LaneIndex, int StartOrder, int EndOrder, int NestingLevel, int PushNodeId);
