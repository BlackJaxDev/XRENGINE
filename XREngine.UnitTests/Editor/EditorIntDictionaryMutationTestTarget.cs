using System.Collections.Generic;

namespace XREngine.UnitTests.Editor;

internal sealed class EditorIntDictionaryMutationTestTarget
{
    public Dictionary<int, int> Values { get; set; } = new();
}
