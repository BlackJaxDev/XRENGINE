namespace XREngine.Editor.UI;

/// <summary>Records the order, identity, and value of each linked node.</summary>
internal sealed class EditorLinkedListSnapshot(object[] nodes, object?[] values)
{
    public object[] Nodes { get; } = nodes;
    public object?[] Values { get; } = values;
}
