using System.Collections;

namespace XREngine.UnitTests.Editor;

/// <summary>Runs a test callback when a collection source is read.</summary>
public sealed class EditorCollectionGetterTarget
{
    public Action? OnGet { get; set; }

    public IDictionary Dictionary
    {
        get { OnGet?.Invoke(); return new Hashtable { ["one"] = 1 }; }
    }

    public LinkedList<int> Linked
    {
        get { OnGet?.Invoke(); return new LinkedList<int>([1, 2]); }
    }

    public ICollection Generic
    {
        get { OnGet?.Invoke(); return new Queue<int>([1, 2]); }
    }
}
