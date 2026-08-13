namespace XREngine;

/// <summary>
/// Immutable bridge from host pacing/admission into the executable backend
/// output plan for the same render frame.
/// </summary>
public readonly record struct RenderOutputSchedulingSnapshot(
    RenderOutputRequest Request,
    RenderOutputSchedulingDecision Decision)
{
    public bool IsDefined => Request.IsDefined;
}
