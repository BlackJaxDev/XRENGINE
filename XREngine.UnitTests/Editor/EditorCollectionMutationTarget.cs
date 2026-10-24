using System.Collections;

namespace XREngine.UnitTests.Editor;

/// <summary>Holds stable collection sources for retired editor tests.</summary>
public sealed class EditorCollectionMutationTarget
{
    public IDictionary Dictionary { get; set; } = new Hashtable { ["one"] = 1 };
    public LinkedList<int> Linked { get; set; } = new([1, 2]);
}
