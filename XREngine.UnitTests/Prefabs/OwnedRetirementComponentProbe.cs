using XREngine.Components;

namespace XREngine.UnitTests.Prefabs;

/// <summary>Observes canonical cleanup and an intentionally incomplete derived cleanup.</summary>
public sealed class OwnedRetirementComponentProbe : XRComponent
{
    public int DestructionCount;
    public bool SkipBaseCleanup;
    public Action? DestroyCallback;
    public Action? ActivationCallback;
    public Action? DeactivationCallback;
    public Action? BeginPlayCallback;
    public Action? EndPlayCallback;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        ActivationCallback?.Invoke();
    }

    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        DeactivationCallback?.Invoke();
    }

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        BeginPlayCallback?.Invoke();
    }

    protected override void OnEndPlay()
    {
        base.OnEndPlay();
        EndPlayCallback?.Invoke();
    }

    protected override void OnDestroying()
    {
        DestructionCount++;
        DestroyCallback?.Invoke();
        if (!SkipBaseCleanup) base.OnDestroying();
    }
}
