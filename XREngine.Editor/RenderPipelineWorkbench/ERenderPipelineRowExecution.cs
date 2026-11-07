namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Tells whether an outline row executed in the newest traced frame of the selected pipeline instance.
/// </summary>
public enum ERenderPipelineRowExecution : byte
{
    /// <summary>The trace has no evidence for the row. Tools draw it as normal.</summary>
    Unknown,
    /// <summary>A branch command selected the row in the newest traced frame.</summary>
    Executed,
    /// <summary>A branch command did not select the row, or the row is inside a branch that did not execute.</summary>
    Skipped,
}
