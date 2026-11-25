namespace XREngine.Editor;

/// <summary>Reports whether another live dock host accepted a panel.</summary>
internal enum NativeDockTransferResult
{
    NoTarget,
    Rejected,
    Committed,
}
