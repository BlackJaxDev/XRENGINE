using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Stores one recycled fixed-height collection row and its current controls.</summary>
internal sealed class EditorVirtualCollectionRowCellState
{
    public required UITextComponent Label { get; init; }
    public required SceneNode RemoveHost { get; init; }
    public required SceneNode ValueNode { get; init; }
    public required UITextComponent ReadOnlyValue { get; init; }
    public required EditorVirtualRowControlLease ReadOnlyControl { get; init; }
    public UIButtonComponent? RemoveButton { get; set; }
    public Action<UIInteractableComponent>? RemoveAction { get; set; }
    public UIButtonComponent? NestedButton { get; set; }
    public Action<UIInteractableComponent>? NestedAction { get; set; }
    public EditorVirtualRowControlLease? NestedButtonControl { get; set; }
    public SceneNode? EditorNode { get; set; }
    public bool ReusableEditor { get; set; }
    public int Index { get; set; } = -1;
    public ulong RowId { get; set; }
    public UIVirtualRowLease Lease { get; set; }
    public ulong Intent { get; set; }
    public bool IsPublished { get; set; }
    public bool IsReadOnly { get; set; } = true;
    public object[]? Bindings { get; set; }
    public EditorVirtualRowControlLease[] EditorControls { get; set; } = [];
}
