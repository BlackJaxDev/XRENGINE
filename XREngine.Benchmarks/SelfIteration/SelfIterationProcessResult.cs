namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Captures one owned child-process invocation.
/// </summary>
public sealed class SelfIterationProcessResult
{
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public TimeSpan Duration { get; init; }
    public string StandardOutputPath { get; init; } = string.Empty;
    public string StandardErrorPath { get; init; } = string.Empty;
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool Succeeded => !TimedOut && ExitCode == 0;
}
