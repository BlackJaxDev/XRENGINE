using System.Collections;

namespace XREngine.UnitTests.Editor;

public sealed class EditorListMutationTestTarget
{
    public List<int> Values { get; set; } = [];
    public ArrayList LooseValues { get; set; } = new();
    public int[] FixedValues { get; set; } = [];
    public IList CustomValues { get; set; } = new System.Collections.ObjectModel.Collection<int>();
}
