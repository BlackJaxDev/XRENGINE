namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Structured final response from the source-editing phase.
/// </summary>
public sealed class SelfIterationAgentImplementation
{
    public bool Implemented { get; set; }
    public string ChangeSummary { get; set; } = string.Empty;
    public SelfIterationReloadMode ReloadMode { get; set; } = SelfIterationReloadMode.Auto;
}
