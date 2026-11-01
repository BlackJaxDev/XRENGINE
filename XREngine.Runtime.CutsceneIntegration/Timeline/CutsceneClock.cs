namespace XREngine.Cutscenes;

/// <summary>
/// The playback clock of a cutscene. Wall time moves the position at a signed rate.
/// In audio-disciplined mode, the audio position corrects the drift in small steps.
/// A large difference makes the clock jump to the audio position.
/// </summary>
public sealed class CutsceneClock
{
    /// <summary>The largest correction per call to <see cref="Discipline"/>, as a fraction of the last advance step.</summary>
    public const double MaxSlewFraction = 0.05;

    /// <summary>The audio difference, in seconds, above which the clock jumps to the audio position.</summary>
    public const double HardResyncSeconds = 0.100;

    private static readonly long HardResyncFlicks = CutsceneTime.FromSeconds(HardResyncSeconds).Flicks;

    private long _lastStepFlicks;
    private bool _hardResync;

    /// <summary>The mode that decides which calls change <see cref="Position"/>.</summary>
    public ECutsceneClockMode Mode { get; set; }

    /// <summary>The current position of the clock.</summary>
    public CutsceneTime Position { get; private set; }

    /// <summary>The playback rate. A negative value plays backward.</summary>
    public double Rate { get; set; } = 1.0;

    /// <summary>
    /// Moves the position by the delta times <see cref="Rate"/>.
    /// This call has no effect in <see cref="ECutsceneClockMode.Manual"/> mode.
    /// </summary>
    /// <param name="deltaSeconds">The elapsed wall time in seconds.</param>
    public void Advance(double deltaSeconds)
    {
        if (Mode == ECutsceneClockMode.Manual)
            return;

        CutsceneTime step = CutsceneTime.FromSeconds(deltaSeconds * Rate);
        _lastStepFlicks = Math.Abs(step.Flicks);
        Position += step;
    }

    /// <summary>
    /// Moves the position toward the audio position. The move is at most <see cref="MaxSlewFraction"/>
    /// times the last advance step. If the difference is above <see cref="HardResyncSeconds"/>,
    /// the position jumps to the audio position and <see cref="ConsumeHardResync"/> returns true once.
    /// This call has no effect outside <see cref="ECutsceneClockMode.AudioDisciplined"/> mode.
    /// </summary>
    /// <param name="audioPosition">The position that the audio device reports.</param>
    public void Discipline(CutsceneTime audioPosition)
    {
        if (Mode != ECutsceneClockMode.AudioDisciplined)
            return;

        long difference = audioPosition.Flicks - Position.Flicks;
        if (Math.Abs(difference) > HardResyncFlicks)
        {
            Position = audioPosition;
            _hardResync = true;
            return;
        }

        long maxCorrection = (long)(_lastStepFlicks * MaxSlewFraction);
        Position = new CutsceneTime(Position.Flicks + Math.Clamp(difference, -maxCorrection, maxCorrection));
    }

    /// <summary>Sets the position. This call works in every mode.</summary>
    /// <param name="position">The new position.</param>
    public void SetPosition(CutsceneTime position)
        => Position = position;

    /// <summary>Returns true once after a hard resync. The call clears the flag.</summary>
    public bool ConsumeHardResync()
    {
        bool value = _hardResync;
        _hardResync = false;
        return value;
    }
}
