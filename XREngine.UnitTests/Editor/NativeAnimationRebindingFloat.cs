using System.Collections;
using XREngine.Animation;

namespace XREngine.UnitTests.Editor;

public sealed class NativeAnimationRebindingFloat : PropAnimFloat, IEnumerable
{
    public NativeAnimationRebindingFloat() : base(2, false, true) { }

    public Action? OnEnumerate { get; set; }

    IEnumerator IEnumerable.GetEnumerator()
    {
        OnEnumerate?.Invoke();
        return base.GetEnumerator();
    }
}
