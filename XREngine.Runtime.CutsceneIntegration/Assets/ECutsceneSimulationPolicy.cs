namespace XREngine.Cutscenes;

/// <summary>
/// Tells what a simulation does after a jump, a scrub, or a camera cut of a cutscene.
/// A simulation keeps state from frame to frame, so it has no value that is a pure function of the time.
/// </summary>
public enum ECutsceneSimulationPolicy
{
    /// <summary>
    /// Resets the simulation to the current pose, then runs it normally. The result is correct after the simulation settles.
    /// This is the default, and it has a low cost.
    /// </summary>
    SettleFromReset,

    /// <summary>
    /// Resets the simulation, then runs fixed steps for the preroll duration of the track, within a time budget in each frame.
    /// The result is close to correct, and the cost is medium. A simulation that cannot run steps settles from the reset,
    /// and the cutscene adds one diagnostic.
    /// </summary>
    WarmUp,
}
