namespace XREngine.Components;

/// <summary>Observable world lifecycle/arena diagnostics.</summary>
public readonly record struct PhysicsChainWorldLifecycleSnapshot(
    long ActiveFrame,
    int LiveInstances,
    int LiveStates,
    int LiveOutputs,
    int DeferredRetirements,
    long AppliedStructuralCommands,
    long AppliedDynamicCommands);
