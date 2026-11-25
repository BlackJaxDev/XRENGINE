namespace XREngine.Editor.UI;

internal interface IEditorDictionaryElementBinding
{
    EditorDictionarySourceLease Source { get; }
    object Key { get; }
    object? DisplayedValue { get; }
    bool IsCurrent { get; }
    Action Refresh { get; }
    void AcceptCommitted(object? value, object state);
}
