namespace XREngine.Components;

/// <summary>Bounded delayed inspection state for one explicitly selected instance.</summary>
public readonly record struct PhysicsChainSelectedActivityDiagnostic(
    PhysicsChainRuntimeHandle Handle,
    bool IsSleeping,
    PhysicsChainActivitySnapshot Activity,
    PhysicsChainWakeReason LastWakeReason,
    ulong WakeCount);
