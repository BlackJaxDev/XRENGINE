namespace XREngine;

public readonly record struct FrameOutputPacingDecision(
    EVrOutputViewKind ViewKind,
    EFrameOutputKind OutputKind,
    ulong FrameId,
    bool IsDue,
    bool CadenceSkipped,
    bool AutoSkipped,
    EFrameOutputSkipReason SkipReason,
    float ConfiguredTargetRateHz,
    float SourceRateHz,
    double AchievedRateHz,
    int TotalRenderCount,
    int TotalSkipCount,
    RenderOutputRequest Request = default)
{
    public bool Skipped => !IsDue;

    public static FrameOutputPacingDecision Due(
        EVrOutputViewKind viewKind,
        EFrameOutputKind outputKind,
        ulong frameId = 0UL,
        float configuredTargetRateHz = 0.0f,
        float sourceRateHz = 0.0f)
    {
        RenderOutputRequest request = RenderOutputRequest.CreateDefault(
            viewKind,
            outputKind,
            frameId,
            configuredTargetRateHz,
            sourceRateHz);
        return new(
            viewKind,
            outputKind,
            frameId,
            IsDue: true,
            CadenceSkipped: false,
            AutoSkipped: false,
            EFrameOutputSkipReason.None,
            configuredTargetRateHz,
            sourceRateHz,
            sourceRateHz > 0.0f ? sourceRateHz : 0.0,
            TotalRenderCount: 0,
            TotalSkipCount: 0,
            request);
    }
}
