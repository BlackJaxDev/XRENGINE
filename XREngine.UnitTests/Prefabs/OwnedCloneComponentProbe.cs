using XREngine.Components;

namespace XREngine.UnitTests.Prefabs;

/// <summary>Provides a simple component with observable destruction for clone ownership tests.</summary>
public sealed class OwnedCloneComponentProbe : XRComponent
{
    public int DestructionCount { get; private set; }

    protected override void OnDestroying()
    {
        DestructionCount++;
        base.OnDestroying();
    }
}
