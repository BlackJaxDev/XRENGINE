namespace XREngine.Components;

/// <summary>Publishes a complete world and handle identity for delayed snapshots.</summary>
internal sealed record PhysicsChainReadbackBinding(PhysicsChainWorld World, PhysicsChainRuntimeHandle Handle);
