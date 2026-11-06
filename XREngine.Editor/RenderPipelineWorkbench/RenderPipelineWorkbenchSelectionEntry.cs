namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// One selected object in the Workbench. Each view reads the field that matches <see cref="Kind"/>.
/// </summary>
/// <param name="Kind">The kind of selected object.</param>
/// <param name="NodeId">The stable node id of a command or container, or 0.</param>
/// <param name="PassIndex">The pass index of a selected pass, or -1.</param>
/// <param name="Name">The resource name or the stage key, or null.</param>
public readonly record struct RenderPipelineWorkbenchSelectionEntry(
    ERenderPipelineWorkbenchSelectionKind Kind,
    int NodeId,
    int PassIndex,
    string? Name)
{
    /// <summary>Gets the empty selection.</summary>
    public static RenderPipelineWorkbenchSelectionEntry None { get; } = new(ERenderPipelineWorkbenchSelectionKind.None, 0, -1, null);

    /// <summary>Creates a command selection.</summary>
    public static RenderPipelineWorkbenchSelectionEntry ForCommand(int nodeId)
        => new(ERenderPipelineWorkbenchSelectionKind.Command, nodeId, -1, null);

    /// <summary>Creates a container selection.</summary>
    public static RenderPipelineWorkbenchSelectionEntry ForContainer(int nodeId)
        => new(ERenderPipelineWorkbenchSelectionKind.Container, nodeId, -1, null);

    /// <summary>Creates a pass selection.</summary>
    public static RenderPipelineWorkbenchSelectionEntry ForPass(int passIndex)
        => new(ERenderPipelineWorkbenchSelectionKind.Pass, 0, passIndex, null);

    /// <summary>Creates a resource selection.</summary>
    public static RenderPipelineWorkbenchSelectionEntry ForResource(string name)
        => new(ERenderPipelineWorkbenchSelectionKind.Resource, 0, -1, name);

    /// <summary>Creates a stage selection.</summary>
    public static RenderPipelineWorkbenchSelectionEntry ForStage(string stageKey)
        => new(ERenderPipelineWorkbenchSelectionKind.Stage, 0, -1, stageKey);
}
