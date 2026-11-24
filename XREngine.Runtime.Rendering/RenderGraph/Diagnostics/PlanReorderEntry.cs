namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>A pass whose plan position differs from its authored position.</summary>
/// <param name="PassIndex">The moved pass.</param>
/// <param name="AuthoredPosition">The position of the pass in declaration order.</param>
/// <param name="ExecutionPosition">The position of the pass in the plan.</param>
/// <param name="Cause">The dependency that moved the pass, as text.</param>
public sealed record PlanReorderEntry(
    int PassIndex,
    int AuthoredPosition,
    int ExecutionPosition,
    string Cause);
