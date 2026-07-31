namespace XREngine.AgentOrchestration;

/// <summary>
/// Compact handoff context used when a caller explicitly delegates work to another model tier.
/// </summary>
public sealed record AgentEvidencePacket
{
    public IReadOnlyList<string> RelevantFilesAndSymbols { get; init; } = [];

    public string CurrentDiff { get; init; } = string.Empty;

    public IReadOnlyList<string> CommandsAndResults { get; init; } = [];

    public IReadOnlyList<string> FailedHypotheses { get; init; } = [];

    public IReadOnlyList<string> UnresolvedQuestions { get; init; } = [];

    public string NextDecision { get; init; } = string.Empty;
}
