namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>The row filters of the Resources view. A row shows when it matches every set filter.</summary>
[Flags]
public enum ERenderPipelineResourceFilter
{
    /// <summary>Show every row.</summary>
    None = 0,
    /// <summary>Show enabled declarations that no pass uses.</summary>
    DeclaredButUnused = 1 << 0,
    /// <summary>Show names that passes use but the layout does not declare.</summary>
    UsedButUndeclared = 1 << 1,
    /// <summary>Show resources that keep content across frames.</summary>
    History = 1 << 2,
    /// <summary>Show external or imported resources.</summary>
    External = 1 << 3,
    /// <summary>Show resources that allow memory aliasing.</summary>
    Aliased = 1 << 4,
}
