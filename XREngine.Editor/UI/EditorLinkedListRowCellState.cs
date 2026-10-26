using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Stores one recycled linked-list row and its active source lease.</summary>
internal sealed class EditorLinkedListRowCellState
{
    public required UITextComponent Label { get; init; }
    public required SceneNode RemoveHost { get; init; }
    public required SceneNode ValueNode { get; init; }
    public UIButtonComponent? RemoveButton { get; set; }
    public Action<UIInteractableComponent>? RemoveAction { get; set; }
    public SceneNode? EditorNode { get; set; }
    public bool ReusableEditor { get; set; }
    public int Index { get; set; } = -1;
    public ulong RowId { get; set; }
    public UIVirtualRowLease Lease { get; set; }
    public ulong Intent { get; set; }
    public bool IsPublished { get; set; }
    public IEditorLinkedElementBinding[]? Bindings { get; set; }
    public EditorLinkedListControlLease[] EditorControls { get; set; } = [];
}
