using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

internal sealed class EditorLinkedElementBindingTestDouble(EditorLinkedSourceLease source, int index)
    : IEditorLinkedElementBinding
{
    public EditorLinkedSourceLease Source { get; } = source;
    public object Node { get; } = source.Displayed.Nodes[index];
    public object? DisplayedValue { get; set; } = source.Displayed.Values[index];
    public Action? OnAccept { get; set; }
    public bool IsCurrent => Source.IsCurrent() &&
        Array.IndexOf(Source.Displayed.Nodes, Node) >= 0;
    public Action Refresh => static () => { };

    public void AcceptCommitted(object? value, object state)
    {
        OnAccept?.Invoke();
        DisplayedValue = value;
    }
}
