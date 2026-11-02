using XREngine.Components;

namespace XREngine.Cutscenes;

/// <summary>
/// Plays a compiled cutscene. Play, seek, scrub, and reverse all use one evaluation path:
/// the player finds the clips that are active at the position and the track evaluators write the sampled state.
/// A seek stores a pending target. The next <see cref="Tick"/> resolves it, so many seeks in one frame
/// give one evaluation at the last target.
/// </summary>
public sealed class CutscenePlayer
{
    private readonly CompiledCutscene _compiled;
    private readonly CutsceneBindingTable _bindings;
    private readonly CutsceneStateSnapshot _snapshot;
    private readonly CutsceneClock _clock = new();
    private readonly CutsceneActiveClip[] _buffer;
    private readonly ICutsceneTrackEvaluator[] _evaluators;
    private readonly CameraCutTrackEvaluator _cameraCut;
    private readonly AnimationClipTrackEvaluator _animation;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics = [];

    private bool _hasPendingSeek;
    private CutsceneTime _pendingTarget;
    private ESeekIntent _pendingIntent;
    private ECutsceneTransportState _stateBeforeScrub;
    private CameraComponent? _reportedCamera;

    /// <summary>Creates a player.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="bindings">The table that holds the bound nodes.</param>
    /// <param name="snapshot">The store of the original values of the bound nodes.</param>
    public CutscenePlayer(CompiledCutscene compiled, CutsceneBindingTable bindings, CutsceneStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(snapshot);

        _compiled = compiled;
        _bindings = bindings;
        _snapshot = snapshot;
        _buffer = new CutsceneActiveClip[Math.Max(1, compiled.MaxActiveClips)];
        _cameraCut = new CameraCutTrackEvaluator(_diagnostics);
        _animation = new AnimationClipTrackEvaluator(compiled, bindings, _diagnostics);
        _evaluators =
        [
            new TransformTrackEvaluator(bindings.SlotCount, _diagnostics),
            _animation,
            new ActivationTrackEvaluator(bindings.SlotCount, _diagnostics),
            _cameraCut,
            new CameraPropertyTrackEvaluator(bindings.SlotCount, _diagnostics),
        ];
    }

    /// <summary>
    /// Raised once in a frame in which the picture jumps: a seek changed the position, the selected camera changed,
    /// or <see cref="Stop"/> moved the position or the camera. A seek to the same position with the same camera raises nothing.
    /// </summary>
    public event Action<CutsceneDiscontinuity>? Discontinuity;

    /// <summary>
    /// Raised after an evaluation in which the selected camera changed, before <see cref="Discontinuity"/> of the same frame.
    /// The argument is the new camera, or null if no camera is selected.
    /// </summary>
    public event Action<CameraComponent?>? ActiveCameraChanged;

    /// <summary>The camera that the camera cut tracks select at the current position, or null if none is selected.</summary>
    public CameraComponent? ActiveCamera => _cameraCut.ActiveCamera;

    /// <summary>The current position. A seek changes it at the next tick.</summary>
    public CutsceneTime Position => _clock.Position;

    /// <summary>The playback rate. A negative value plays backward.</summary>
    public double Rate => _clock.Rate;

    /// <summary>The transport state.</summary>
    public ECutsceneTransportState State { get; private set; } = ECutsceneTransportState.Stopped;

    /// <summary>The clock that moves the position. The caller can change its mode.</summary>
    public CutsceneClock Clock => _clock;

    /// <summary>The problems that the evaluators found. Each problem is added once.</summary>
    public IReadOnlyList<CutsceneCompileDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Starts or resumes playback.</summary>
    public void Play()
    {
        if (State == ECutsceneTransportState.Scrubbing)
            _stateBeforeScrub = ECutsceneTransportState.Playing;
        else
            State = ECutsceneTransportState.Playing;
    }

    /// <summary>Holds the position.</summary>
    public void Pause()
    {
        if (State == ECutsceneTransportState.Scrubbing)
            _stateBeforeScrub = ECutsceneTransportState.Paused;
        else
            State = ECutsceneTransportState.Paused;
    }

    /// <summary>
    /// Stops playback, moves the position to zero, and puts back the original values of the targets.
    /// The targets that have the keep-state setting stay as they are.
    /// </summary>
    public void Stop()
        => StopCore(restoreEverySlot: false);

    /// <summary>
    /// Stops playback, moves the position to zero, and puts back every recorded value.
    /// The finish behavior of the tracks has no effect, and a target of a removed track is also restored.
    /// </summary>
    public void StopAndRestoreAll()
        => StopCore(restoreEverySlot: true);

    private void StopCore(bool restoreEverySlot)
    {
        State = ECutsceneTransportState.Stopped;
        _hasPendingSeek = false;
        CutsceneTime from = _clock.Position;
        _clock.SetPosition(CutsceneTime.Zero);
        _animation.RestoreForStop(restoreEverySlot || _compiled.Asset.ForceRestoreAll, _snapshot);
        for (int i = 0; i < _evaluators.Length; i++)
            _evaluators[i].Reset();
        if (restoreEverySlot)
            _snapshot.RestoreAll();
        else
            _snapshot.Restore(_compiled.Asset.ForceRestoreAll, default);

        // The camera returns to the previous one and the position jumps to zero, so history must reset once.
        bool cameraChanged = PublishActiveCamera();
        if (cameraChanged || from != CutsceneTime.Zero)
            Discontinuity?.Invoke(new CutsceneDiscontinuity(from, CutsceneTime.Zero, ESeekIntent.Jump));
    }

    /// <summary>Sets the playback rate. A negative value plays backward.</summary>
    /// <param name="rate">The new rate.</param>
    public void SetRate(double rate)
        => _clock.Rate = rate;

    /// <summary>
    /// Asks for a new position. The player limits the target to the range from zero to the duration.
    /// The next <see cref="Tick"/> moves the position. The last seek before a tick wins.
    /// </summary>
    /// <param name="target">The new position.</param>
    /// <param name="intent">Tells if more seeks are likely to follow.</param>
    public void Seek(CutsceneTime target, ESeekIntent intent)
    {
        _pendingTarget = target;
        _pendingIntent = intent;
        _hasPendingSeek = true;
    }

    /// <summary>Starts a scrub. The clock does not move the position until <see cref="EndScrub"/>.</summary>
    public void BeginScrub()
    {
        if (State == ECutsceneTransportState.Scrubbing)
            return;
        _stateBeforeScrub = State;
        State = ECutsceneTransportState.Scrubbing;
    }

    /// <summary>Moves the position during a scrub.</summary>
    /// <param name="target">The new position.</param>
    public void UpdateScrub(CutsceneTime target)
        => Seek(target, ESeekIntent.Scrub);

    /// <summary>Ends a scrub and goes back to the state from before the scrub.</summary>
    public void EndScrub()
    {
        if (State != ECutsceneTransportState.Scrubbing)
            return;
        State = _stateBeforeScrub;
    }

    /// <summary>
    /// Resolves a pending seek or advances the clock, then evaluates the tracks once.
    /// When playback reaches the end of the range, the position stays at the end and the state becomes paused.
    /// </summary>
    /// <param name="deltaSeconds">The wall time since the last tick, in seconds.</param>
    public void Tick(double deltaSeconds)
    {
        CutsceneTime duration = _compiled.Asset.Duration;

        if (_hasPendingSeek)
        {
            _hasPendingSeek = false;
            CutsceneTime from = _clock.Position;
            CutsceneTime target = _pendingTarget.Clamp(CutsceneTime.Zero, duration);
            _clock.SetPosition(target);
            EvaluateAt(target, 0.0);

            // A seek that changes the camera still raises only one discontinuity.
            bool seekCameraChanged = PublishActiveCamera();
            if (target != from || seekCameraChanged)
                Discontinuity?.Invoke(new CutsceneDiscontinuity(from, target, _pendingIntent));
            return;
        }

        if (State != ECutsceneTransportState.Playing)
            return;

        CutsceneTime before = _clock.Position;
        _clock.Advance(deltaSeconds);
        CutsceneTime position = _clock.Position.Clamp(CutsceneTime.Zero, duration);
        _clock.SetPosition(position);
        EvaluateAt(position, deltaSeconds);

        // A cut during playback is a jump of the picture, so it raises one discontinuity.
        if (PublishActiveCamera())
            Discontinuity?.Invoke(new CutsceneDiscontinuity(before, position, ESeekIntent.Jump));

        double rate = _clock.Rate;
        if ((rate > 0.0 && position >= duration) || (rate < 0.0 && position <= CutsceneTime.Zero))
            State = ECutsceneTransportState.Paused;
    }

    private bool PublishActiveCamera()
    {
        CameraComponent? camera = _cameraCut.ActiveCamera;
        if (ReferenceEquals(camera, _reportedCamera))
            return false;

        _reportedCamera = camera;
        ActiveCameraChanged?.Invoke(camera);
        return true;
    }

    private void EvaluateAt(CutsceneTime time, double deltaSeconds)
    {
        // The end of the range is exclusive in the time index. A query at the duration uses the last
        // flick inside the range, so the last pose stays visible at the duration.
        long lastFlick = Math.Max(0L, _compiled.Asset.Duration.Flicks - 1);
        CutsceneTime query = new(Math.Min(time.Flicks, lastFlick));
        int count = _compiled.QueryActive(query, _buffer);
        ReadOnlySpan<CutsceneActiveClip> active = new(_buffer, 0, count);

        CutsceneEvaluationContext context = new(_compiled, _bindings, _snapshot, time, deltaSeconds);
        for (int i = 0; i < _evaluators.Length; i++)
            _evaluators[i].Evaluate(in context, active);
    }
}
