using System.Collections.Generic;

namespace XREngine.UnitTests.Editor;

internal sealed class EditorDictionaryMutationTestTarget
{
    public Dictionary<string, int> Values { get; set; } = new(StringComparer.Ordinal);
}
