namespace XREngine.Cutscenes;

/// <summary>
/// Writes the sampled state of one kind of track to the bound nodes.
/// An evaluator reads curves by absolute time, so the result does not depend on earlier evaluations.
/// </summary>
public interface ICutsceneTrackEvaluator
{
    /// <summary>
    /// Evaluates the tracks that this evaluator handles. A target that was written earlier and has no
    /// active clip now gets its original value back.
    /// </summary>
    /// <param name="context">The data of the evaluation.</param>
    /// <param name="clips">
    /// All active clips at the evaluation time. The clips of one track are next to each other.
    /// </param>
    void Evaluate(in CutsceneEvaluationContext context, ReadOnlySpan<CutsceneActiveClip> clips);

    /// <summary>Forgets which targets the evaluator wrote. The caller restores the targets itself.</summary>
    void Reset();
}
