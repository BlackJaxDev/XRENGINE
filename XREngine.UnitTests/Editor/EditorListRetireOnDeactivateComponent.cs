using XREngine.Components;

namespace XREngine.UnitTests.Editor;

/// <summary>Runs a test callback when an old detail child is deactivated.</summary>
public sealed class EditorListRetireOnDeactivateComponent : XRComponent
{
    public Action? OnDeactivate { get; set; }
    public bool Deactivated { get; private set; }

    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        Deactivated = true;
        OnDeactivate?.Invoke();
    }
}
