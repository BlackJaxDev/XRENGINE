using XREngine.UnitTests.Core;

// Compile each supported accessor kind through the production source generator.
// Test targets keep representative declarations out of the runtime host contracts.
[assembly: XREngine.RuntimeMemberAccess(typeof(RuntimeMemberAccessTarget), nameof(RuntimeMemberAccessTarget.Value))]
[assembly: XREngine.RuntimeMemberAccess(typeof(RuntimeMemberAccessTarget), nameof(RuntimeMemberAccessTarget.Target))]
[assembly: XREngine.RuntimeMemberAccess(typeof(RuntimeMemberAccessTarget), nameof(RuntimeMemberAccessTarget.SetValue))]
[assembly: XREngine.RuntimeMemberAccess(typeof(RuntimeMemberAccessTarget), nameof(RuntimeMemberAccessTarget.Changed))]

namespace XREngine.UnitTests.Core;

/// <summary>Provides isolated property, field, method, and event targets for generated accessor tests.</summary>
public sealed class RuntimeMemberAccessTarget
{
    public float Value { get; set; }
    public RuntimeMemberAccessTarget? Target = null;
    public event Action? Changed;

    public void SetValue(float value) => Value = value;
    public void RaiseChanged() => Changed?.Invoke();
}
