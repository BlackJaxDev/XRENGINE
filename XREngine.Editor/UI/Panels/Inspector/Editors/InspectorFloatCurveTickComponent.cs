using XREngine.Components;

namespace XREngine.Editor;

/// <summary>Refreshes a retained curve detail on the UI owner thread.</summary>
public sealed class InspectorFloatCurveTickComponent : XRComponent
{
    public Action? Refresh { get; set; }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        Refresh = null;
        base.OnDestroying();
    }

    private void RefreshTick() => Refresh?.Invoke();
}
