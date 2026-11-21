namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>One cached bar of the Lifetimes tab of the Execution view.</summary>
/// <param name="Name">The logical resource name.</param>
/// <param name="FirstOrder">The plan position of the first use.</param>
/// <param name="LastOrder">The plan position of the last use.</param>
/// <param name="AliasGroup">The alias group, or -1.</param>
/// <param name="Tooltip">The tooltip text.</param>
public readonly record struct RenderPipelinePlanLifetimeRow(
    string Name,
    int FirstOrder,
    int LastOrder,
    int AliasGroup,
    string Tooltip);
