namespace XREngine.Rendering.UI;

internal enum EUIWorldPassNodeState : byte
{
    Pending,
    BranchTrue,
    BranchFalse,
    Empty,
    Executed,
    Pruned,
}
