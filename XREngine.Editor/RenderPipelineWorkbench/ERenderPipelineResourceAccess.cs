namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Tells how a command or pass uses a resource.
/// </summary>
[Flags]
public enum ERenderPipelineResourceAccess
{
    /// <summary>No use.</summary>
    None = 0,
    /// <summary>The resource is read.</summary>
    Read = 1,
    /// <summary>The resource is written.</summary>
    Write = 2,
}
