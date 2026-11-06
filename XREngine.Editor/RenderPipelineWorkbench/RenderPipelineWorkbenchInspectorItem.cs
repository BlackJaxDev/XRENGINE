namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// One line of the Workbench Inspector. A line with a link target selects that object when clicked.
/// </summary>
/// <param name="Text">The line text.</param>
/// <param name="Link">The object that a click selects, or <see cref="RenderPipelineWorkbenchSelectionEntry.None"/>.</param>
/// <param name="Indent">The indent level.</param>
/// <param name="IsHeader">Whether the line is a section header.</param>
public readonly record struct RenderPipelineWorkbenchInspectorItem(
    string Text,
    RenderPipelineWorkbenchSelectionEntry Link,
    int Indent = 0,
    bool IsHeader = false)
{
    /// <summary>Gets whether a click on the line selects an object.</summary>
    public bool HasLink => Link.Kind != ERenderPipelineWorkbenchSelectionKind.None;
}
