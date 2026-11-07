namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>The kind of a row in the Plan tab of the Execution view.</summary>
public enum ERenderPipelinePlanRowKind
{
    /// <summary>A queue submission header.</summary>
    Submission,

    /// <summary>A batch header.</summary>
    Batch,

    /// <summary>A pass.</summary>
    Pass,
}
