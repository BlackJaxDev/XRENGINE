namespace XREngine.AgentRuntimes;

/// <summary>Configures the locally installed executable without accepting shell commands.</summary>
public sealed record CodexRuntimeOptions
{
    public string ExecutablePath { get; init; } = "codex";

    /// <summary>Optional host-owned folder for isolated session working directories.</summary>
    public string? SessionDirectory { get; init; }

    /// <summary>Rejects a changed CLI account before sending existing conversation context.</summary>
    public string? ExpectedAccountFingerprint { get; init; }
}
