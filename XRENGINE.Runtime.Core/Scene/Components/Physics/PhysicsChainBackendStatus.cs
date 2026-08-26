namespace XREngine.Components;

/// <summary>
/// Observable backend state for a physics-chain output. Unsupported and
/// failed GPU selections remain explicit instead of silently using the CPU.
/// </summary>
public enum PhysicsChainBackendStatus : byte
{
    Uninitialized,
    Ready,
    Unsupported,
    CapacityExceeded,
    Faulted,
    Retired,
}
