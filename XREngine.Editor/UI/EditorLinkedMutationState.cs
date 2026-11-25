namespace XREngine.Editor.UI;

/// <summary>Tracks the logical state of a linked list across Undo and Redo.</summary>
internal sealed class EditorLinkedMutationState
{
    public object Token { get; set; } = new();
}
