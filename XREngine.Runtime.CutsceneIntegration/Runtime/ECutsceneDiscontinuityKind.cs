namespace XREngine.Cutscenes;

/// <summary>Tells which state a <see cref="CutsceneDiscontinuity"/> invalidates.</summary>
public enum ECutsceneDiscontinuityKind
{
    /// <summary>
    /// The picture jumps: a seek, a camera cut, or a stop. Temporal render history, auto exposure, and the simulations reset.
    /// </summary>
    Full,

    /// <summary>
    /// A hard resync of the clock to the audio inside one shot. The position moves by a small amount, so only the temporal
    /// render history (for example motion vectors and TAA) resets. Auto exposure and the simulations continue.
    /// </summary>
    ClockResync,
}
