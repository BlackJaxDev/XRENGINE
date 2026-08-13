namespace XREngine;

public readonly record struct RenderOutputDagNodeStatus(
    ERenderOutputNodeState State,
    float Progress,
    uint ContentAgeFrames,
    uint LastCompletedFrame,
    bool AuthorizedReuse,
    bool HasCompletedResult,
    ERenderOutputWorkDisposition Disposition = ERenderOutputWorkDisposition.FreshRender,
    ERenderOutputPolicyReason PolicyReason = ERenderOutputPolicyReason.None,
    uint ConsecutiveDeferrals = 0u);
