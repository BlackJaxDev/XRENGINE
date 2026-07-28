namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Read-only first phase of an LLM attempt, fingerprinted before edits are allowed.
/// </summary>
public sealed class SelfIterationAgentProposal
{
    public string IssueKey { get; set; } = string.Empty;
    public string AttemptKey { get; set; } = string.Empty;
    public string TargetScenario { get; set; } = string.Empty;
    public string Hypothesis { get; set; } = string.Empty;
    public string PlannedChange { get; set; } = string.Empty;
    public string ExpectedMetric { get; set; } = string.Empty;
    public SelfIterationReloadMode ReloadMode { get; set; } = SelfIterationReloadMode.Auto;

    internal void Validate(IReadOnlyCollection<SelfIterationScenario> scenarios)
    {
        if (string.IsNullOrWhiteSpace(IssueKey) || string.IsNullOrWhiteSpace(AttemptKey))
            throw new InvalidDataException("Agent proposal requires non-empty issueKey and attemptKey.");
        if (string.IsNullOrWhiteSpace(Hypothesis) || string.IsNullOrWhiteSpace(PlannedChange))
            throw new InvalidDataException("Agent proposal requires hypothesis and plannedChange.");
        if (string.IsNullOrWhiteSpace(TargetScenario))
            TargetScenario = scenarios.First().Name;
        if (!scenarios.Any(scenario => scenario.Name.Equals(TargetScenario, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Agent proposal selected unknown scenario '{TargetScenario}'.");
    }
}
