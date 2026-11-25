using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

internal sealed class EditorDictionaryElementBindingTestDouble(
    EditorDictionarySourceLease source, object key) : IEditorDictionaryElementBinding
{
    public EditorDictionarySourceLease Source { get; } = source;
    public object Key { get; } = key;
    public object? DisplayedValue { get; set; } = source.Displayed.Values[
        source.Adapter.IndexOf(source.Displayed, key)];
    public bool IsCurrent => Source.IsCurrent() &&
        Source.Adapter.IndexOf(Source.Displayed, Key) >= 0;
    public Action Refresh => static () => { };
    public Action? OnAccept { get; set; }

    public void AcceptCommitted(object? value, object state)
    {
        OnAccept?.Invoke();
        DisplayedValue = value;
    }
}
