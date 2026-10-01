namespace XREngine.Rendering;

/// <summary>Latest retained readiness failure copied from a backend frame owner.</summary>
public readonly record struct RenderBackendPresentNowFailureSnapshot(
    long Sequence,
    ulong FrameId,
    int FrameSlot,
    ulong AcceptedSceneEpoch,
    ulong OutputGeneration,
    string ReadinessStage,
    string ActiveTicket,
    string DependencyChain,
    string Disposition,
    string FailureType,
    string Detail)
{
    /// <summary>Whether the backend has observed a readiness failure.</summary>
    public bool IsValid => Sequence != 0;
}
