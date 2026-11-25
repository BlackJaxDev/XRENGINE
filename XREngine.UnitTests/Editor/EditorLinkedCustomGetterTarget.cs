using System.Collections.Generic;

namespace XREngine.UnitTests.Editor;

internal sealed class EditorLinkedCustomGetterTarget
{
    private LinkedList<int> _values = new([1]);
    public int GetterCalls { get; private set; }
    public LinkedList<int> Values
    {
        get
        {
            GetterCalls++;
            return _values;
        }
        set => _values = value;
    }
}
