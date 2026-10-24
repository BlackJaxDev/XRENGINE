namespace XREngine.Editor.UI;

internal interface IEditorLinkedElementBinding
{
    EditorLinkedSourceLease Source { get; }
    object Node { get; }
    object? DisplayedValue { get; }
    bool IsCurrent { get; }
    Action Refresh { get; }
    void AcceptCommitted(object? value, object state);
}
