namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>How the Resources view sorts its rows into groups.</summary>
public enum ERenderPipelineResourceGrouping
{
    /// <summary>Layout order.</summary>
    None,
    /// <summary>Group by resource kind.</summary>
    Kind,
    /// <summary>Group by lifetime.</summary>
    Lifetime,
}
