namespace XREngine.AgentRuntimes;

/// <summary>Selects an installed client and optional account boundary for one run.</summary>
public sealed record LocalCliRuntimeOptions
{
    public string ExecutablePath { get; init; } = string.Empty;
    public string? SessionDirectory { get; init; }
    public string? ExpectedAccountFingerprint { get; init; }
}
