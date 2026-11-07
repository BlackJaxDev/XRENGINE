namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>One cached row of the Plan tab of the Execution view.</summary>
/// <param name="Kind">The row kind.</param>
/// <param name="Text">The row text.</param>
/// <param name="Detail">The secondary text, such as the stage and the barrier count.</param>
/// <param name="PassIndex">The pass of a pass row, or -1.</param>
/// <param name="Executed">Whether the pass executed in the last recorded frame. Header rows report true.</param>
/// <param name="Reordered">Whether the plan moved the pass from its authored position.</param>
public readonly record struct RenderPipelinePlanRow(
    ERenderPipelinePlanRowKind Kind,
    string Text,
    string Detail,
    int PassIndex,
    bool Executed,
    bool Reordered);
