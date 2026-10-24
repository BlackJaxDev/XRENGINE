using XREngine.Components;

namespace XREngine.UnitTests.Editor;

public sealed class EditorListSceneTestComponent : XRComponent
{
    public List<int> Values { get; set; } = [1, 2];
}
