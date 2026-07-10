namespace XREngine;

public readonly record struct RvcOpenXrVisibilityMaskState(
    uint ViewIndex,
    EVrOutputViewKind ViewKind,
    bool HiddenAreaMeshAvailable,
    bool VisibleAreaMeshAvailable,
    uint HiddenAreaVertexCount,
    uint HiddenAreaIndexCount,
    uint VisibleAreaVertexCount,
    uint VisibleAreaIndexCount,
    ulong Revision,
    ERvcOpenXrVisibilityMaskStatus Status,
    string Diagnostic)
{
    public bool CanUseHiddenAreaStencil => HiddenAreaMeshAvailable && Status == ERvcOpenXrVisibilityMaskStatus.ReadyForStencilPrepass;

    public RvcOpenXrVisibilityMaskState MarkInvalidated(string diagnostic)
        => this with
        {
            Revision = Revision + 1UL,
            Status = ERvcOpenXrVisibilityMaskStatus.InvalidatedByRuntime,
            Diagnostic = diagnostic,
        };
}
