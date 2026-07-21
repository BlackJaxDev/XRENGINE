namespace XREngine.Components;

public enum PhysicsChainReadbackTransferFailure : byte
{
    None,
    InvalidRequest,
    InvalidEpoch,
    InvalidFrame,
    RequestNotPending,
    RequestNotInFlight,
    LayoutOverflow,
    ByteCountMismatch,
    NoStagingSlot,
    InvalidLease,
    FenceFailed,
}
