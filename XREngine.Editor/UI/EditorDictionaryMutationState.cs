namespace XREngine.Editor.UI;

/// <summary>Tracks the logical state of a dictionary across Undo and Redo.</summary>
internal sealed class EditorDictionaryMutationState
{
    public object Token { get; set; } = new();
}
