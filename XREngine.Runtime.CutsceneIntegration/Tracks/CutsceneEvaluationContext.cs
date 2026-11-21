namespace XREngine.Cutscenes;

/// <summary>
/// The data that a track evaluator needs for one evaluation.
/// The type is a ref struct, so it never escapes the evaluation call and never allocates.
/// </summary>
public readonly ref struct CutsceneEvaluationContext
{
    /// <summary>Creates a context.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="bindings">The table that holds the bound nodes.</param>
    /// <param name="snapshot">The store of the original values of the bound nodes.</param>
    /// <param name="time">The cutscene time of the evaluation.</param>
    /// <param name="deltaSeconds">The wall time since the last evaluation, in seconds.</param>
    public CutsceneEvaluationContext(
        CompiledCutscene compiled,
        CutsceneBindingTable bindings,
        CutsceneStateSnapshot snapshot,
        CutsceneTime time,
        double deltaSeconds)
    {
        Compiled = compiled;
        Bindings = bindings;
        Snapshot = snapshot;
        Time = time;
        DeltaSeconds = deltaSeconds;
    }

    /// <summary>The compiled cutscene.</summary>
    public CompiledCutscene Compiled { get; }

    /// <summary>The table that holds the bound nodes.</summary>
    public CutsceneBindingTable Bindings { get; }

    /// <summary>The store of the original values of the bound nodes.</summary>
    public CutsceneStateSnapshot Snapshot { get; }

    /// <summary>The cutscene time of the evaluation.</summary>
    public CutsceneTime Time { get; }

    /// <summary>The wall time since the last evaluation, in seconds. It is zero for a seek.</summary>
    public double DeltaSeconds { get; }
}
