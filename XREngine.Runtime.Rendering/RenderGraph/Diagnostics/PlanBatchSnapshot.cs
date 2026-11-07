namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>One batch of a plan snapshot: passes that share a stage and an attachment signature.</summary>
/// <param name="BatchIndex">The batch index in plan order.</param>
/// <param name="Stage">The stage of every pass in the batch.</param>
/// <param name="AttachmentSignature">The attachment signature that the passes share.</param>
/// <param name="PassIndices">The passes of the batch in execution order.</param>
/// <param name="SubmissionIndex">The submission that holds the batch, or -1 when the source has no submissions.</param>
public sealed record PlanBatchSnapshot(
    int BatchIndex,
    ERenderGraphPassStage Stage,
    string AttachmentSignature,
    IReadOnlyList<int> PassIndices,
    int SubmissionIndex);
