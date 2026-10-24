using System.Collections.Generic;

namespace XREngine.UnitTests.Editor;

internal sealed class EditorLinkedMutationTestTarget
{
    public LinkedList<int> Values { get; set; } = new();
}
