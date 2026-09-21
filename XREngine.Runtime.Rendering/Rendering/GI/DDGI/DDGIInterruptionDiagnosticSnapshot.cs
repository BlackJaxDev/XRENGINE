namespace XREngine.Rendering.GI.DDGI;

/// <summary>Cold diagnostic state for an intentionally interrupted DDGI update cycle.</summary>
public sealed record DDGIInterruptionDiagnosticSnapshot(
    ulong RequestToken,
    int PipelineInstanceId,
    int ResourceGeneration,
    int RemainingSkips,
    ulong? FirstSkippedRenderFrame,
    ulong? LastSkippedRenderFrame,
    uint? FirstSkippedStateFrameIndex,
    uint? LastSkippedStateFrameIndex,
    int MatchedAbortCount,
    int AcceptedNonPublishingReceiptCount,
    int UnexpectedCompleteCount,
    int UnexpectedPublicationCount,
    ulong? FirstAcceptedRecoveryRenderFrame,
    uint? FirstAcceptedRecoveryStateFrameIndex,
    bool FailedReceiptLatched);
