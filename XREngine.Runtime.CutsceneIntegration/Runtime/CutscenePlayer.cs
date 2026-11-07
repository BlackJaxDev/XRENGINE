using XREngine.Components;
using XREngine.Rendering.VideoStreaming.Interfaces;

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
    private readonly AudioTrackEvaluator _audio;
    private readonly VideoTrackEvaluator _video;
    private readonly SimulationControlEvaluator _simulation;
    private readonly CutsceneEventDispatcher _events;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics = [];

    private bool _hasPendingSeek;
    private CutsceneTime _pendingTarget;
    private ESeekIntent _pendingIntent;
    private ECutsceneTransportState _stateBeforeScrub;
    private CameraComponent? _reportedCamera;
    private bool _heldByGate;
    private bool _stopJumpPending;

    // The intent of the last discontinuity since the simulations were updated. A stop raises one outside the tick,
    // so the simulations apply it in the next tick.
    private ESeekIntent? _simulationDiscontinuity;

    // True when no step has handled the events at the current position yet: after the open, a stop, or a move backward.
    private bool _playStartInclusive = true;
    private bool _finishedRaised;

    // The highest position up to which playback has handled the events. A play step never fires an event at or below it,
    // so a clock correction cannot fire an event twice. A seek, a hard resync, and a stop move it to the new position.
    private CutsceneTime _playHighWater;

    /// <summary>Creates a player.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="bindings">The table that holds the bound nodes.</param>
    /// <param name="snapshot">The store of the original values of the bound nodes.</param>
    /// <param name="openAudioDecoder">
    /// Opens the streaming decoder of an audio clip file. If it is null, <see cref="AudioTrackEvaluator.OpenDefaultDecoder"/> is used.
    /// </param>
    /// <param name="createVideoSession">
    /// Creates the media session of a video clip. It runs on a background task. If it is null,
    /// <see cref="VideoTrackEvaluator.CreateDefaultSession"/> is used.
    /// </param>
    public CutscenePlayer(
        CompiledCutscene compiled,
        CutsceneBindingTable bindings,
        CutsceneStateSnapshot snapshot,
        Func<string, Data.IStreamingAudioDecoder>? openAudioDecoder = null,
        Func<IMediaStreamSession>? createVideoSession = null)
        : this(compiled, bindings, snapshot, openAudioDecoder, TimeProvider.System, null, createVideoSession)
    {
    }

    /// <summary>Creates a player with a wall clock for the audio restart time. A test gives a fake clock.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="bindings">The table that holds the bound nodes.</param>
    /// <param name="snapshot">The store of the original values of the bound nodes.</param>
    /// <param name="openAudioDecoder">Opens the streaming decoder of an audio clip file, or null for the default decoders.</param>
    /// <param name="time">The wall clock that measures the time of the audio restarts.</param>
    internal CutscenePlayer(
        CompiledCutscene compiled,
        CutsceneBindingTable bindings,
        CutsceneStateSnapshot snapshot,
        Func<string, Data.IStreamingAudioDecoder>? openAudioDecoder,
        TimeProvider time)
        : this(compiled, bindings, snapshot, openAudioDecoder, time, null, null)
    {
    }

    /// <summary>Creates a player with a wall clock and a scheduler for the background audio seeks. A test runs the scheduler by hand.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="bindings">The table that holds the bound nodes.</param>
    /// <param name="snapshot">The store of the original values of the bound nodes.</param>
    /// <param name="openAudioDecoder">Opens the streaming decoder of an audio clip file, or null for the default decoders.</param>
    /// <param name="time">The wall clock that measures the time of the audio restarts.</param>
    /// <param name="audioSeekScheduler">The scheduler of the background seeks of the full audio decoders, or null for the default.</param>
    /// <param name="createVideoSession">Creates the media session of a video clip, or null for the default.</param>
    internal CutscenePlayer(
        CompiledCutscene compiled,
        CutsceneBindingTable bindings,
        CutsceneStateSnapshot snapshot,
        Func<string, Data.IStreamingAudioDecoder>? openAudioDecoder,
        TimeProvider time,
        TaskScheduler? audioSeekScheduler,
        Func<IMediaStreamSession>? createVideoSession = null)
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
        _audio = new AudioTrackEvaluator(compiled, _clock, _diagnostics, openAudioDecoder, time, audioSeekScheduler);
        _video = new VideoTrackEvaluator(compiled, _diagnostics, createVideoSession, time);
        _evaluators =
        [
            new TransformTrackEvaluator(bindings.SlotCount, _diagnostics),
            _animation,
            new ActivationTrackEvaluator(bindings.SlotCount, _diagnostics),
            _cameraCut,
            new CameraPropertyTrackEvaluator(bindings.SlotCount, _diagnostics),
            new PropertyTrackEvaluator(compiled, snapshot, _diagnostics),
            _audio,
            _video,
        ];
        _simulation = new SimulationControlEvaluator(compiled, bindings, _diagnostics);
        _events = new CutsceneEventDispatcher(compiled.Asset, _diagnostics);

        // Normal playback with audio follows the audio device, so the clock starts in audio mode.
        if (_audio.HasClockTrack)
            _clock.Mode = ECutsceneClockMode.AudioDisciplined;
    }

    /// <summary>
    /// True while the clock uses wall time because an audio track cannot read the playback clock of its transport.
    /// The player changed the clock to <see cref="ECutsceneClockMode.Wall"/> and added a diagnostic. The clock goes back to
    /// <see cref="ECutsceneClockMode.AudioDisciplined"/> when the playback clock is available again.
    /// </summary>
    public bool IsAudioClockFallback => _audio.IsClockFallbackActive;

    /// <summary>
    /// True while the audio tracks do not stream: in a pause, a scrub, a stop, and at any rate other than one.
    /// A track with an audio proxy can still play scrub grains when the playhead moves.
    /// </summary>
    public bool IsAudioMuted => _audio.IsMuted;

    /// <summary>
    /// The heard audio position minus the clock position before the correction, from the last play step that read the audio clock.
    /// A positive value means that the audio is ahead of the clock. Use it to measure the drift between the picture and the sound.
    /// </summary>
    public CutsceneTime AudioClockDifference { get; private set; }

    /// <summary>The number of play steps that read a heard audio position, since the player was created.</summary>
    public long AudioClockReadCount { get; private set; }

    /// <summary>The number of hard resyncs to the audio position, since the player was created.</summary>
    public int AudioHardResyncCount { get; private set; }

    /// <summary>The output latency in nanoseconds that the transport reported at the last clock reading, or zero.</summary>
    public long AudioClockLatencyNanoseconds => _audio.ClockLatencyNanoseconds;

    /// <summary>
    /// Raised for each event that fires. Forward playback fires each event that it crosses, once. A seek fires
    /// events only by <see cref="EventTrack.SeekPolicy"/>. A scrub and reverse playback fire no event.
    /// The events of a tick fire after the evaluation, so a handler sees the state of the new position.
    /// </summary>
    public event Action<CutsceneFiredEvent>? EventFired
    {
        add => _events.Fired += value;
        remove => _events.Fired -= value;
    }

    /// <summary>
    /// Raised once when the position reaches the end of the cutscene by forward playback or by a jump seek, for example a skip.
    /// The player raises it from its state and not from an event, so a skip that jumps over the last events still raises it.
    /// A scrub and a stop do not raise it. A later move away from the end lets it be raised again.
    /// </summary>
    public event Action? Finished;

    /// <summary>
    /// True for an editor preview. A preview fires only the event tracks that have <see cref="EventTrack.FireInPreview"/> set.
    /// </summary>
    public bool IsPreview
    {
        get => _events.IsPreview;
        set => _events.IsPreview = value;
    }

    /// <summary>
    /// Raised once in a frame in which the picture jumps: a seek changed the position, the selected camera changed,
    /// or <see cref="Stop"/> moved the position or the camera. A seek to the same position with the same camera raises nothing.
    /// A hard resync to the audio with no camera change also raises it, with <see cref="ECutsceneDiscontinuityKind.ClockResync"/>.
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

    /// <summary>
    /// The gate that can hold a play step before the evaluation, or null. The residency session sets it for the playback wait.
    /// </summary>
    public ICutscenePlaybackGate? PlaybackGate { get; set; }

    /// <summary>
    /// The intent of the move that the last <see cref="Tick"/> made other than a play step, or null.
    /// It is the intent of a resolved seek, or <see cref="ESeekIntent.Jump"/> for a <see cref="Stop"/> since the tick before.
    /// A hard resync to the audio is a correction of normal play, so it gives null. The player passes this value to
    /// <see cref="PlaybackGate"/> in a play step, and the residency session reads it after each tick.
    /// </summary>
    public ESeekIntent? LastTickSeek { get; private set; }

    /// <summary>
    /// True when a seek to any position shows resident data at once: T0 of the whole closure is resident and the programs of
    /// the T0 materials are ready. <see cref="CutsceneOpenSequence"/> sets it at the end of the open. A seek after that time
    /// loads no T0 data, because T0 stays resident until the close.
    /// </summary>
    /// <remarks>
    /// Program readiness is checked at the material level (the uber variant build with the default prewarmer). The backend
    /// program link and a Vulkan pipeline compile are not seen, so on Vulkan a T0 draw can still be skipped for some frames
    /// after this flag is true. The flag is false for a player that no open sequence opened, for a host with no residency
    /// session (T0 residency is then not known), and for the new player of a preview recompile.
    /// </remarks>
    public bool ReadyForInstantSeek { get; internal set; }

    /// <summary>True while <see cref="PlaybackGate"/> holds playback. The audio sources stay stopped while it is true.</summary>
    public bool IsHeldByGate => _heldByGate;

    /// <summary>The evaluator that applies the policies of the simulation control tracks.</summary>
    internal SimulationControlEvaluator Simulation => _simulation;

    /// <summary>The evaluator of the video tracks. The residency session gives it the video residency backend.</summary>
    internal VideoTrackEvaluator Video => _video;

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

    /// <summary>Holds the position. The audio sources stop at once, also if no tick follows.</summary>
    public void Pause()
    {
        if (State == ECutsceneTransportState.Scrubbing)
            _stateBeforeScrub = ECutsceneTransportState.Paused;
        else
            State = ECutsceneTransportState.Paused;
        SilenceAudioIfInaudible();
    }

    // A host can skip the ticks of a player that does not play, so the sources stop here and not at the next tick.
    private void SilenceAudioIfInaudible()
    {
        if (State != ECutsceneTransportState.Playing || Math.Abs(_clock.Rate - 1.0) > 1e-9)
            _audio.SetTransport(playing: false, _clock.Rate);
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
        // A tick that raised the handler that called this method must not continue after the stop.
        _events.AdvanceTransportVersion();
        State = ECutsceneTransportState.Stopped;
        _hasPendingSeek = false;
        _heldByGate = false;
        _stopJumpPending = true;
        CutsceneTime from = _clock.Position;
        _clock.SetPosition(CutsceneTime.Zero);
        _playHighWater = CutsceneTime.Zero;
        _playStartInclusive = true;
        _finishedRaised = false;
        _animation.RestoreForStop(restoreEverySlot || _compiled.Asset.ForceRestoreAll, _snapshot);
        for (int i = 0; i < _evaluators.Length; i++)
            _evaluators[i].Reset();
        _simulation.Release();
        if (restoreEverySlot)
            _snapshot.RestoreAll();
        else
            _snapshot.Restore(_compiled.Asset.ForceRestoreAll, default);

        // The camera returns to the previous one and the position jumps to zero, so history must reset once.
        bool cameraChanged = PublishActiveCamera();
        if (cameraChanged || from != CutsceneTime.Zero)
        {
            _simulationDiscontinuity = ESeekIntent.Jump;
            Discontinuity?.Invoke(new CutsceneDiscontinuity(from, CutsceneTime.Zero, ESeekIntent.Jump));
        }
    }

    /// <summary>Sets the playback rate. A negative value plays backward. A rate other than one stops the audio sources at once.</summary>
    /// <param name="rate">The new rate.</param>
    public void SetRate(double rate)
    {
        _clock.Rate = rate;
        SilenceAudioIfInaudible();
    }

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
        _events.AdvanceTransportVersion();
    }

    /// <summary>
    /// Sets the position at once, with no evaluation and no side effect. Use it for a player that replaces another
    /// player at the same position, for example after a recompile. The event and finish state of the replaced player
    /// carries over, so no event fires twice and none is lost. A later <see cref="Seek"/> or play step evaluates.
    /// </summary>
    /// <param name="position">The position. The player limits it to the range from zero to the duration.</param>
    /// <param name="eventsAtPositionPending">The <see cref="EventsAtPositionPending"/> value of the replaced player.</param>
    /// <param name="finishedRaised">The <see cref="FinishedRaised"/> value of the replaced player.</param>
    public void RestorePosition(CutsceneTime position, bool eventsAtPositionPending, bool finishedRaised)
    {
        CutsceneTime duration = _compiled.Asset.Duration;
        CutsceneTime target = position.Clamp(CutsceneTime.Zero, duration);
        _clock.SetPosition(target);
        _playHighWater = target;
        _playStartInclusive = eventsAtPositionPending;
        _finishedRaised = finishedRaised && target >= duration;
    }

    /// <summary>
    /// True if the events at the current position have not fired yet. This is true after the open, after a stop,
    /// and after a backward move. Forward playback from the position then fires them.
    /// </summary>
    public bool EventsAtPositionPending => _playStartInclusive;

    /// <summary>True if <see cref="Finished"/> was raised for the current stay at the end.</summary>
    public bool FinishedRaised => _finishedRaised;

    /// <summary>Starts a scrub. The clock does not move the position until <see cref="EndScrub"/>. The audio sources stop at once.</summary>
    public void BeginScrub()
    {
        if (State == ECutsceneTransportState.Scrubbing)
            return;
        _stateBeforeScrub = State;
        State = ECutsceneTransportState.Scrubbing;
        SilenceAudioIfInaudible();
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
    /// The clock does not count the time that delayed the start of the clock audio track in the last tick, for example
    /// a long decoder seek, because the sound starts only after it. That time is measured as wall time. With a time dilation
    /// other than one, the delta and the removed time have different scales; the audio clock then disagrees with the delta anyway.
    /// </summary>
    /// <remarks>
    /// At the end of each tick, the simulation control tracks apply their policies for the discontinuity of the tick, or for
    /// a discontinuity of a <see cref="Stop"/> since the last tick. So a simulation reset occurs only in a tick.
    /// </remarks>
    /// <param name="deltaSeconds">The time since the last tick, in seconds. The restart exclusion treats it as wall time.</param>
    public void Tick(double deltaSeconds)
    {
        TickTransport(deltaSeconds);

        ESeekIntent? discontinuity = _simulationDiscontinuity;
        _simulationDiscontinuity = null;
        _simulation.ApplyTick(discontinuity, State, _clock.Rate);
        _video.Update();
    }

    /// <summary>
    /// Services the video tracks with no evaluation: shows the frames that background seeks decoded, and starts the exact
    /// decode after a drag stops. <see cref="Tick"/> calls it. A host that does not tick a paused player calls it in each frame,
    /// so that a seek result shows. It never waits for a decode.
    /// </summary>
    public void UpdateVideo()
        => _video.Update();

    private void TickTransport(double deltaSeconds)
    {
        CutsceneTime duration = _compiled.Asset.Duration;

        // The time that delayed the clock track in the last tick, for example a long decoder seek, is in this delta. The sound
        // starts only after its restart, so the clock does not count that time. A seek in this tick sets the position, so it drops the value.
        double restartSeconds = _audio.ConsumeRestartSeconds();

        LastTickSeek = _stopJumpPending ? ESeekIntent.Jump : null;
        _stopJumpPending = false;

        // A seek or a state other than play ends a gate hold. The gate decides again at the next play step.
        if (_hasPendingSeek || State != ECutsceneTransportState.Playing)
            _heldByGate = false;

        // Audio plays only in forward playback at rate one. Other states stop the sources before any evaluation.
        // A scrub seek is silent also without BeginScrub, and a gate hold keeps the sources stopped.
        bool scrubSeek = _hasPendingSeek && _pendingIntent == ESeekIntent.Scrub;
        bool playing = State == ECutsceneTransportState.Playing && !scrubSeek;
        _audio.SetTransport(playing && !_heldByGate, _clock.Rate);

        // Scrub grains play when the playhead moves with no stream, but not for a jump seek (a skip while paused is silent),
        // during a gate hold, or in a fast reverse play.
        bool jumpSeek = _hasPendingSeek && _pendingIntent == ESeekIntent.Jump;
        _audio.SetScrubGrains(!jumpSeek && !_heldByGate && !(playing && _clock.Rate <= -AudioTrackEvaluator.SilentReverseRate));

        // Video decodes forward in forward playback, shows keyframes while the playhead is dragged or plays backward, and
        // decodes the exact frame otherwise.
        _video.SetTransport(playing && !_heldByGate && _clock.Rate > 0.0, scrubSeek || (playing && _clock.Rate < 0.0));

        if (_hasPendingSeek)
        {
            _hasPendingSeek = false;
            LastTickSeek = _pendingIntent;
            int version = _events.TransportVersion;
            CutsceneTime from = _clock.Position;
            CutsceneTime target = _pendingTarget.Clamp(CutsceneTime.Zero, duration);
            _clock.SetPosition(target);
            _playHighWater = target;
            if (target != from)
            {
                _audio.MarkDiscontinuity();
                _video.MarkDiscontinuity();
            }
            EvaluateAt(target, 0.0);

            // A seek that changes the camera still raises only one discontinuity.
            bool seekCameraChanged = PublishActiveCamera();
            if (target != from || seekCameraChanged)
            {
                _simulationDiscontinuity = _pendingIntent;
                Discontinuity?.Invoke(new CutsceneDiscontinuity(from, target, _pendingIntent));
            }

            // A handler can stop or seek the player. The rest of this tick then belongs to the old transport.
            bool scrub = _pendingIntent == ESeekIntent.Scrub || State == ECutsceneTransportState.Scrubbing;
            if (_events.TransportVersion != version || !_events.DispatchSeek(from, target, _playStartInclusive, scrub))
                return;
            TrackDirection(from, target);
            UpdateFinished(target, duration, mayRaise: !scrub);
            return;
        }

        if (State != ECutsceneTransportState.Playing)
            return;

        int playVersion = _events.TransportVersion;
        CutsceneTime before = _clock.Position;
        double clockSeconds = Math.Max(0.0, deltaSeconds - restartSeconds);
        _clock.Advance(clockSeconds);

        // The audio correction comes before the evaluation and the event dispatch, so both use the final position of the step.
        // The first reading after a restart moves the clock in one step. The high-water mark keeps a backward step from firing an event again.
        if (_audio.TryReadHeardPosition(clockSeconds, out CutsceneTime heard, out bool firstAfterRestart))
        {
            AudioClockDifference = heard - _clock.Position;
            AudioClockReadCount++;
            _clock.Discipline(heard, firstAfterRestart);
        }
        bool hardResync = _clock.ConsumeHardResync();
        if (hardResync)
        {
            AudioHardResyncCount++;
            _video.MarkDiscontinuity();
        }

        CutsceneTime position = _clock.Position.Clamp(CutsceneTime.Zero, duration);
        if (PlaybackGate is { } gate)
            position = ApplyGate(gate, before, position, deltaSeconds);
        _clock.SetPosition(position);
        EvaluateAt(position, deltaSeconds);

        // A cut during playback and a hard resync to the audio are jumps of the picture, so each raises one discontinuity.
        // A resync with no cut is a small correction inside one shot: it resets only the temporal render history, and the
        // simulations and auto exposure continue. A cut in the same tick makes it a full discontinuity.
        bool playCameraChanged = PublishActiveCamera();
        if (playCameraChanged || hardResync)
        {
            if (playCameraChanged)
                _simulationDiscontinuity = ESeekIntent.Jump;
            ECutsceneDiscontinuityKind kind = playCameraChanged ? ECutsceneDiscontinuityKind.Full : ECutsceneDiscontinuityKind.ClockResync;
            Discontinuity?.Invoke(new CutsceneDiscontinuity(before, position, ESeekIntent.Jump, kind));
        }

        bool completed;
        if (hardResync)
        {
            // Events across a resync follow the seek policy of each track, as for a jump seek.
            // The mark moves first, so a handler that stops or seeks the player sets its own value.
            _playHighWater = position;
            completed = _events.TransportVersion == playVersion
                && _events.DispatchSeek(before, position, _playStartInclusive, scrub: false);
        }
        else
        {
            // Reverse playback fires no event. Events at or below the high-water mark fired already.
            CutsceneTime from = before;
            bool inclusive = _playStartInclusive;
            if (from < _playHighWater)
            {
                from = _playHighWater;
                inclusive = false;
            }
            if (position > _playHighWater)
                _playHighWater = position;
            completed = _events.TransportVersion == playVersion && _events.DispatchPlay(from, position, inclusive);
        }
        if (!completed)
            return;
        TrackDirection(before, position);

        // The state is final before the finish handlers run, so a handler can stop or restart the player.
        double rate = _clock.Rate;
        if ((rate > 0.0 && position >= duration) || (rate < 0.0 && position <= CutsceneTime.Zero))
            State = ECutsceneTransportState.Paused;
        UpdateFinished(position, duration, mayRaise: rate > 0.0);
    }

    /// <summary>
    /// Asks the gate for the position of a play step before the evaluation, so that a held frame never shows the state
    /// after the hold position. The gate gets <see cref="LastTickSeek"/>, so a <see cref="Stop"/> before this play step is a jump.
    /// A new hold stops the audio sources at once, and a release starts them again before the evaluation of the same tick.
    /// </summary>
    private CutsceneTime ApplyGate(ICutscenePlaybackGate gate, CutsceneTime before, CutsceneTime position, double deltaSeconds)
    {
        double rate = _clock.Rate;
        CutsceneTime allowed = gate.LimitPlayStep(before, position, rate, LastTickSeek, deltaSeconds);
        CutsceneTime low = before < position ? before : position;
        CutsceneTime high = before < position ? position : before;
        allowed = allowed.Clamp(low, high);

        bool held = allowed != position;
        if (held != _heldByGate)
        {
            _audio.SetTransport(playing: !held, rate);
            _audio.SetScrubGrains(!held);
            _video.SetTransport(!held && rate > 0.0, !held && rate < 0.0);
        }
        _heldByGate = held;
        return allowed;
    }

    // A forward move handled the events up to its end. A backward move lands before events that have not fired in this pass.
    private void TrackDirection(CutsceneTime from, CutsceneTime to)
    {
        if (to > from)
            _playStartInclusive = false;
        else if (to < from)
            _playStartInclusive = true;
    }

    private void UpdateFinished(CutsceneTime position, CutsceneTime duration, bool mayRaise)
    {
        if (position < duration)
        {
            _finishedRaised = false;
            return;
        }
        if (_finishedRaised || !mayRaise)
            return;

        _finishedRaised = true;
        Finished?.Invoke();
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
