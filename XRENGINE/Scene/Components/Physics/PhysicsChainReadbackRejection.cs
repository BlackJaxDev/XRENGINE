namespace XREngine.Components;

public enum PhysicsChainReadbackRejection : byte
{
    None,
    InvalidInstance,
    NoFields,
    InvalidFields,
    InvalidFrame,
    InvalidSelection,
    LayoutOverflow,
    ByteCountMismatch,
    ElementBudgetExceeded,
    ByteBudgetExceeded,
}
