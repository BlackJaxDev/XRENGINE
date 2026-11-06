namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Identifies one central view of the Workbench.
/// </summary>
public enum ERenderPipelineWorkbenchView
{
    /// <summary>The command flow with branch lanes.</summary>
    Flow,
    /// <summary>The pass graph with resource hazard edges.</summary>
    Passes,
    /// <summary>The logical resources and their generations.</summary>
    Resources,
    /// <summary>The backend execution plan.</summary>
    Execution,
    /// <summary>The pipeline settings and the post-process schema.</summary>
    Settings,
    /// <summary>The problems that the validator found.</summary>
    Validation,
    /// <summary>The render pipeline script text.</summary>
    Script,
}
