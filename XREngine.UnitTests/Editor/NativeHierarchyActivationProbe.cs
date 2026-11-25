using XREngine.Components;

namespace XREngine.UnitTests.Editor;

/// <summary>Records actual component activation for hierarchy replay tests.</summary>
internal sealed class NativeHierarchyActivationProbe : XRComponent
{
    public int ActivationCount { get; private set; }
    public int DeactivationCount { get; private set; }
    public IRuntimeWorldContext? WorldAtActivation { get; private set; }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        WorldAtActivation = World;
        ActivationCount++;
    }

    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        DeactivationCount++;
    }
}
