namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Identifies the kind of object that the Workbench selection points at.
/// </summary>
public enum ERenderPipelineWorkbenchSelectionKind
{
    /// <summary>Nothing is selected.</summary>
    None,
    /// <summary>A command in the command chain.</summary>
    Command,
    /// <summary>A command container, such as a branch or a loop body.</summary>
    Container,
    /// <summary>A render pass.</summary>
    Pass,
    /// <summary>A logical resource.</summary>
    Resource,
    /// <summary>A post-process schema stage.</summary>
    Stage,
}
