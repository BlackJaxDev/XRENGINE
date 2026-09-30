namespace XREngine.Components;

public enum PhysicsChainArenaCompactionDecisionKind : byte
{
    NotRequired,
    DeferredUntilFramesComplete,
    RebuildAndSwap,
}
