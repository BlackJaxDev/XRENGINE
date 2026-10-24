using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Stores the controls of one recycled list row.</summary>
internal sealed class EditorListRowCellState
{
    public required UITextComponent Label { get; init; }
    public required SceneNode RemoveHost { get; init; }
    public UIButtonComponent? RemoveButton { get; set; }
    public required SceneNode ValueNode { get; init; }
    public SceneNode? EditorNode { get; set; }
    public bool ReusableEditor { get; set; }
    public int Index { get; set; } = -1;
    public int LastIndex { get; set; } = -1;
    public ulong RowId { get; set; }
    public ulong BoundRevision { get; set; }
    public IEditorListElementBinding[]? Bindings { get; set; }
    public IEditorListElementBinding[]? LastBindings { get; set; }
}
