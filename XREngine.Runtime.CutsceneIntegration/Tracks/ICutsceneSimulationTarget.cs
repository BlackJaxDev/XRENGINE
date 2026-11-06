namespace XREngine.Cutscenes;

/// <summary>
/// One simulation that a <see cref="SimulationControlTrack"/> controls, for example a physics chain or a particle emitter.
/// <see cref="SimulationControlEvaluator"/> calls these methods only in the cutscene tick, never in a seek call.
/// </summary>
public interface ICutsceneSimulationTarget
{
    /// <summary>The simulation object, for example the component. Two targets with the same source are one target.</summary>
    object Source { get; }

    /// <summary>A short name for diagnostics.</summary>
    string Name { get; }

    /// <summary>True if the target can run a warm-up with <see cref="WarmUp"/>.</summary>
    bool SupportsWarmUp { get; }

    /// <summary>Resets the simulation to the current pose of the target.</summary>
    /// <param name="seed">The random seed for a target that uses random values.</param>
    void ResetToCurrentPose(int seed);

    /// <summary>Starts or stops a hold. While held, the simulation does not move away from the reset pose.</summary>
    /// <param name="held">True to hold, false to release.</param>
    void SetHeld(bool held);

    /// <summary>Keeps a held simulation at the reset pose for one more frame. The evaluator calls it in each held frame with no discontinuity.</summary>
    void KeepHeld();

    /// <summary>
    /// Resets the simulation, then runs fixed steps for <paramref name="seconds"/> of simulation time.
    /// The steps use at most <paramref name="budgetMilliseconds"/> of wall time in each frame, and the rest continue in later frames.
    /// </summary>
    /// <param name="seconds">The preroll duration in seconds.</param>
    /// <param name="seed">The random seed for a target that uses random values.</param>
    /// <param name="budgetMilliseconds">The wall time for the steps in each frame, in milliseconds.</param>
    void WarmUp(float seconds, int seed, double budgetMilliseconds);
}
