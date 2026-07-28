namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Configures the external autonomous LLM command used for proposals and edits.
/// </summary>
public sealed class SelfIterationAgentConfiguration
{
    public string Executable { get; set; } = string.Empty;
    public string[] ProposalArguments { get; set; } = [];
    public string[] ImplementationArguments { get; set; } = [];
    public bool PromptViaStandardInput { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 1800;
    public Dictionary<string, string> Environment { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal void Validate(bool required)
    {
        if (required && string.IsNullOrWhiteSpace(Executable))
            throw new InvalidDataException("Agent.Executable is required for a full self-iteration run.");
        if (TimeoutSeconds is < 10 or > 86400)
            throw new InvalidDataException("Agent.TimeoutSeconds must be between 10 and 86400.");
    }
}
