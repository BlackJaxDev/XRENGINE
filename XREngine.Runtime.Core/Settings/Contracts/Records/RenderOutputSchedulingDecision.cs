namespace XREngine;

/// <summary>
/// Allocation-free admission result produced before an output begins collect,
/// recording, acquisition, or submission work.
/// </summary>
public readonly record struct RenderOutputSchedulingDecision(
    bool Execute,
    ERenderOutputWorkDisposition Disposition,
    ERenderOutputPolicyReason Reason,
    uint ContentAgeFrames,
    bool XrCriticalPathReserved,
    bool ForcedRefresh)
{
    public static RenderOutputSchedulingDecision Fresh(bool xrCriticalPathReserved = false)
        => new(
            Execute: true,
            ERenderOutputWorkDisposition.FreshRender,
            xrCriticalPathReserved
                ? ERenderOutputPolicyReason.XrCriticalPathReserved
                : ERenderOutputPolicyReason.None,
            ContentAgeFrames: 0u,
            xrCriticalPathReserved,
            ForcedRefresh: false);
}
