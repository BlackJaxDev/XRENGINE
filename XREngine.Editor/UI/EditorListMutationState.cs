namespace XREngine.Editor.UI;

/// <summary>Tracks a list state across ordered Undo and Redo operations.</summary>
internal sealed class EditorListMutationState
{
    public object Token { get; set; } = new();
}
