namespace XREngine.Cutscenes;

/// <summary>
/// Limits a play step of a <see cref="CutscenePlayer"/> before the player evaluates it. The residency session uses it
/// to hold playback before a camera cut until the next shot is resident.
/// </summary>
public interface ICutscenePlaybackGate
{
    /// <summary>
    /// Gets the position that a play step can reach. The player calls it after the clock step and before the evaluation,
    /// only for a play step and not for a seek. The player limits the result to the range from
    /// <paramref name="from"/> to <paramref name="to"/>. A result other than <paramref name="to"/> holds playback:
    /// the player evaluates at the result and stops the audio sources until a step is not held.
    /// </summary>
    /// <param name="from">The position before the step.</param>
    /// <param name="to">The position after the clock step.</param>
    /// <param name="rate">The playback rate.</param>
    /// <param name="seek">The <see cref="CutscenePlayer.LastTickSeek"/> of the tick: a jump after a <see cref="CutscenePlayer.Stop"/> in the same tick, otherwise null.</param>
    /// <param name="deltaSeconds">The wall time of the step, in seconds.</param>
    CutsceneTime LimitPlayStep(CutsceneTime from, CutsceneTime to, double rate, ESeekIntent? seek, double deltaSeconds);
}
