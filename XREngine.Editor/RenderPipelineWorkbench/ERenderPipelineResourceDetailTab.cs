namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>The detail tabs of the Resources view.</summary>
public enum ERenderPipelineResourceDetailTab
{
    /// <summary>The declaration fields and the predicate result.</summary>
    Spec,
    /// <summary>The active generation, its build state, and the key changes from retired generations.</summary>
    Generations,
    /// <summary>The passes that write and read the resource.</summary>
    Usage,
    /// <summary>The physical instance that the generation holds.</summary>
    Physical,
}
