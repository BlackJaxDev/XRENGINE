namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>The tabs of the Workbench Execution view.</summary>
public enum ERenderPipelineExecutionTab
{
    /// <summary>Submissions, batches, and passes in plan order.</summary>
    Plan,

    /// <summary>The physical barriers of the plan.</summary>
    Barriers,

    /// <summary>The resource lifetimes as bars over plan positions, colored by alias group.</summary>
    Lifetimes,

    /// <summary>The passes that the plan moved, with the cause, and the order violations.</summary>
    Reorders,

    /// <summary>The authored order next to the backend order, and GPU work that has no pass.</summary>
    Compare,
}
