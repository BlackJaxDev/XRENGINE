namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Outcome of applying an attempted fix to an isolated live editor.
/// </summary>
public sealed class SelfIterationReloadResult
{
    public bool Succeeded { get; init; }
    public SelfIterationReloadMode RequestedMode { get; init; }
    public SelfIterationReloadMode EffectiveMode { get; init; }
    public bool EditorRelaunched { get; init; }
    public string Details { get; init; } = string.Empty;
}
