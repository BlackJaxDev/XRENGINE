namespace XREngine.AgentOrchestration;

/// <summary>
/// Token counters reported by the provider.
/// </summary>
public sealed record AgentTokenUsage
{
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long TotalTokens { get; init; }

    public static AgentTokenUsage operator +(AgentTokenUsage left, AgentTokenUsage right)
        => new()
        {
            InputTokens = left.InputTokens + right.InputTokens,
            OutputTokens = left.OutputTokens + right.OutputTokens,
            TotalTokens = left.TotalTokens + right.TotalTokens,
        };
}
