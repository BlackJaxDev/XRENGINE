using System.Reflection;
using XREngine.Components;

namespace XREngine.UnitTests.Audio;

internal sealed class AudioSourceLifecycleProbe : AudioSourceComponent
{
    public bool HasPositionUpdateTick
    {
        get
        {
            var ticks = (IEnumerable<(ETickGroup Group, int Order, WorldTick Tick)>)typeof(XRWorldObjectBase)
                .GetField("_tickCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            return ticks.Any(tick => tick.Group == ETickGroup.Late && tick.Order == (int)ETickOrder.Scene &&
                ReferenceEquals(tick.Tick.Target, this) && tick.Tick.Method.Name == "UpdatePosition");
        }
    }

    public void ActivateForTest() => OnComponentActivated();
    public void DeactivateForTest() => OnComponentDeactivated();
}
