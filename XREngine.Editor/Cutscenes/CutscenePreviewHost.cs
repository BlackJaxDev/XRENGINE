using XREngine.Cutscenes;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Editor.Cutscenes;

/// <summary>
/// Plays a cutscene in the edit-mode world, so that an author can see it without play mode.
/// Each call must come from the update thread. <see cref="EditorImGuiUI.EnqueueSceneEdit"/> moves a panel request to that thread.
/// </summary>
/// <remarks>
/// <para>
/// The transform flush in edit mode needs no special request. Edit mode is not a stopped world:
/// <c>RuntimeWorldHost.BeginEditModeAsync</c> turns off physics and the game mode and then calls <c>BeginPlayAsync</c>.
/// That call leaves the world in the playing state and connects <c>LinkTimeCallbacks</c>, so
/// <c>EngineTimer.PostUpdateFrame</c> runs <c>ProcessDirtyTransforms</c> in each frame.
/// A transform that this host writes during <c>UpdateFrame</c> is therefore flushed later in the same frame.
/// </para>
/// <para>
/// The preview always restores every recorded value at <see cref="End"/>, whatever the finish behavior of a track is.
/// The host never marks a scene as changed, and it stops the undo system from recording its writes.
/// An author can still move a bound node with a gizmo during a preview. That undo entry holds a preview value as its
/// previous value, so <see cref="End"/> removes the transform changes of bound nodes that were recorded since <see cref="Begin"/>.
/// </para>
/// <para>
/// A host with a residency backend factory runs a <see cref="CutsceneResidencySession"/> for each compile of the preview.
/// The planner uses the baked manifest while it is current, and the approximate manifest otherwise. The playback wait is
/// off by default, because an author expects the preview to follow the clock.
/// </para>
/// </remarks>
public sealed class CutscenePreviewHost
{
    private readonly ICutsceneBindingResolver _resolver;
    private readonly XRViewport? _viewport;
    private readonly List<CutsceneCompileDiagnostic> _compileDiagnostics = [];
    private readonly HashSet<XRBase> _boundTransforms = new(ReferenceEqualityComparer.Instance);
    private readonly Action<CutsceneFiredEvent> _relayEvent;
    private readonly Func<IResidencyBackend?>? _residencyBackendFactory;
    private readonly ICutsceneResidencyAssetCollector _residencyCollector;

    private long _undoMark;
    private CutsceneAsset? _asset;
    private CutscenePlayer? _player;
    private CutsceneBindingTable? _bindings;
    private CutsceneCameraRouter? _router;
    private CutsceneResidencySession? _residency;
    private bool _playbackWaitEnabled;
    private double _playbackWaitMaximumSeconds = CutsceneResidencyPlanner.DefaultPlaybackWaitMaximumSeconds;
    private volatile bool _recompileRequested;
    private bool _hasPendingSeek;
    private CutsceneTime _pendingTarget;
    private ESeekIntent _pendingIntent;
    private bool _hasLoopRange;
    private CutsceneTime _loopStart;
    private CutsceneTime _loopEnd;

    /// <summary>Creates a host.</summary>
    /// <param name="resolver">The object that finds the bound scene nodes.</param>
    /// <param name="viewport">The viewport that shows the cutscene camera, or null to not route the camera.</param>
    /// <param name="residencyBackendFactory">
    /// Creates the texture residency backend at each compile, or null to run the preview without residency.
    /// The residency session owns each backend and disposes it.
    /// </param>
    /// <param name="residencyCollector">The collector for the approximate manifest, or null for <see cref="CutsceneRenderableAssetCollector"/>.</param>
    public CutscenePreviewHost(
        ICutsceneBindingResolver resolver,
        XRViewport? viewport = null,
        Func<IResidencyBackend?>? residencyBackendFactory = null,
        ICutsceneResidencyAssetCollector? residencyCollector = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _viewport = viewport;
        _relayEvent = RelayEvent;
        _residencyBackendFactory = residencyBackendFactory;
        _residencyCollector = residencyCollector ?? CutsceneRenderableAssetCollector.Instance;
    }

    /// <summary>The residency session of the active preview, or null when the preview runs without residency.</summary>
    public CutsceneResidencySession? Residency => _residency;

    /// <summary>
    /// True to hold forward play at a camera cut until the next shot is resident, for at most
    /// <see cref="PlaybackWaitMaximumSeconds"/>. The default is false for the preview. A change applies at once.
    /// </summary>
    public bool PlaybackWaitEnabled
    {
        get => _playbackWaitEnabled;
        set
        {
            _playbackWaitEnabled = value;
            if (_residency is { } residency)
                residency.Planner.PlaybackWaitEnabled = value;
        }
    }

    /// <summary>The maximum time in seconds of one playback wait. It must be greater than zero. The default is 1 second.</summary>
    public double PlaybackWaitMaximumSeconds
    {
        get => _playbackWaitMaximumSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _playbackWaitMaximumSeconds = value;
            if (_residency is { } residency)
                residency.Planner.PlaybackWaitMaximumSeconds = value;
        }
    }

    /// <summary>The asset of the active preview, or null when no preview is active.</summary>
    public CutsceneAsset? Asset => _asset;

    /// <summary>True when a preview is active.</summary>
    public bool IsActive => _player is not null;

    /// <summary>The number of times that this host compiled an asset. It shows when an edit took effect.</summary>
    public int CompileCount { get; private set; }

    /// <summary>
    /// Raised for each event that fires in the preview. Gameplay script must not run while an author drags the playhead,
    /// so the preview fires only the event tracks that have <see cref="EventTrack.FireInPreview"/> set.
    /// The subscription stays across a recompile and a new <see cref="Begin"/>.
    /// </summary>
    public event Action<CutsceneFiredEvent>? EventFired;

    /// <summary>The current position, or zero when no preview is active.</summary>
    public CutsceneTime Position => _player?.Position ?? CutsceneTime.Zero;

    /// <summary>The transport state. It is stopped when no preview is active.</summary>
    public ECutsceneTransportState State => _player?.State ?? ECutsceneTransportState.Stopped;

    /// <summary>The playback rate. A negative value plays backward.</summary>
    public double Rate => _player?.Rate ?? 1.0;

    /// <summary>The player of the preview, or null before <see cref="Begin"/>. A recompile replaces it.</summary>
    public CutscenePlayer? Player => _player;

    /// <summary>True when a loop range is set.</summary>
    public bool HasLoopRange => _hasLoopRange;

    /// <summary>The start of the loop range.</summary>
    public CutsceneTime LoopStart => _loopStart;

    /// <summary>The end of the loop range.</summary>
    public CutsceneTime LoopEnd => _loopEnd;

    /// <summary>
    /// Starts a preview at position zero, in the paused state. The state snapshot records each original value
    /// before the first write. If a preview is active, this method ends it first.
    /// An asset that cannot play does not throw. The problems are in <see cref="CopyDiagnostics"/>.
    /// </summary>
    /// <param name="asset">The cutscene to preview.</param>
    public void Begin(CutsceneAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        End();

        using Undo.RecordingSuppressionScope suppression = Undo.SuppressRecording();
        _undoMark = Undo.MarkHistory();
        _asset = asset;
        Open(asset);
        _player!.Pause();

        // The first tick shows the state at position zero.
        Seek(CutsceneTime.Zero, ESeekIntent.Jump);
    }

    /// <summary>Asks for a new position. The next <see cref="Tick"/> moves it. The last seek before a tick wins.</summary>
    /// <param name="target">The new position.</param>
    /// <param name="intent">Tells if more seeks are likely to follow.</param>
    public void Seek(CutsceneTime target, ESeekIntent intent)
    {
        if (_player is null)
            return;

        _pendingTarget = target;
        _pendingIntent = intent;
        _hasPendingSeek = true;
    }

    /// <summary>Starts or resumes playback.</summary>
    public void Play()
        => _player?.Play();

    /// <summary>Holds the position.</summary>
    public void Pause()
        => _player?.Pause();

    /// <summary>Sets the playback rate. A negative value plays backward.</summary>
    /// <param name="rate">The new rate.</param>
    public void SetRate(double rate)
        => _player?.SetRate(rate);

    /// <summary>Starts a scrub. The clock does not move the position until <see cref="EndScrub"/>.</summary>
    public void BeginScrub()
        => _player?.BeginScrub();

    /// <summary>Ends a scrub and goes back to the state from before the scrub.</summary>
    public void EndScrub()
        => _player?.EndScrub();

    /// <summary>Makes playback stay in a time range. A range without length clears the loop.</summary>
    /// <param name="start">The start of the range.</param>
    /// <param name="end">The end of the range.</param>
    public void SetLoopRange(CutsceneTime start, CutsceneTime end)
    {
        _hasLoopRange = end > start;
        _loopStart = start;
        _loopEnd = end;
    }

    /// <summary>Clears the loop range.</summary>
    public void ClearLoopRange()
        => _hasLoopRange = false;

    /// <summary>
    /// Tells the host that the asset changed. The next <see cref="Tick"/> recompiles it, which is a safe frame boundary.
    /// Any thread can call this method.
    /// </summary>
    public void NotifyAssetEdited()
    {
        if (_player is not null)
            _recompileRequested = true;
    }

    /// <summary>Applies a pending edit and a pending seek, then advances and evaluates the cutscene once.</summary>
    /// <param name="deltaSeconds">The wall time since the last tick, in seconds.</param>
    public void Tick(double deltaSeconds)
    {
        if (_player is null)
            return;

        // A paused preview writes nothing. It also keeps the undo system active for the edits of the author.
        // The planner still updates, so that the dwell gate of a scrub opens when the playhead stays in place.
        if (!_recompileRequested && !_hasPendingSeek && _player.State != ECutsceneTransportState.Playing)
        {
            _residency?.UpdateIdle(_player, deltaSeconds);
            return;
        }

        using Undo.RecordingSuppressionScope suppression = Undo.SuppressRecording();

        if (_recompileRequested)
            Recompile();

        CutscenePlayer player = _player;
        bool seeking = _hasPendingSeek;
        if (seeking)
        {
            _hasPendingSeek = false;
            player.Seek(_pendingTarget, _pendingIntent);
        }

        bool wasPlaying = player.State == ECutsceneTransportState.Playing;
        if (wasPlaying && _hasLoopRange && !seeking)
            deltaSeconds = LimitStepToLoopRange(player, deltaSeconds);

        // A loop wrap is a jump seek, so the next tick reports it to the planner as a jump.
        CutsceneResidencySession? residency = _residency;
        residency?.BeginTick();
        player.Tick(deltaSeconds);
        residency?.CompleteTick(player, deltaSeconds);

        if (wasPlaying && _hasLoopRange)
            WrapInsideLoopRange(player);
    }

    /// <summary>
    /// Ends the preview, gives the camera back to the viewport, destroys spawned instances, and restores every recorded value.
    /// A call without an active preview does nothing.
    /// </summary>
    public void End()
    {
        if (_player is null)
            return;

        using Undo.RecordingSuppressionScope suppression = Undo.SuppressRecording();
        try
        {
            Release();
        }
        finally
        {
            _asset = null;
            _recompileRequested = false;
            _hasPendingSeek = false;
            _hasLoopRange = false;
            Undo.RemovePropertyChangesSince(_undoMark, _boundTransforms);
            _boundTransforms.Clear();
        }
    }

    /// <summary>Gets the node that a binding slot holds now.</summary>
    /// <param name="slot">The binding slot.</param>
    /// <returns>The node, or null if the slot has no live node.</returns>
    public SceneNode? GetBoundNode(int slot)
        => _bindings is { } bindings && slot >= 0 && slot < bindings.SlotCount ? bindings.GetNode(slot) : null;

    /// <summary>Copies the current problems of the preview to a list.</summary>
    /// <param name="destination">The list that receives the problems. The method does not clear it.</param>
    public void CopyDiagnostics(List<CutsceneCompileDiagnostic> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.AddRange(_compileDiagnostics);
        if (_bindings is { } bindings)
            destination.AddRange(bindings.Diagnostics);
        if (_player is { } player)
            destination.AddRange(player.Diagnostics);
    }

    private void Open(CutsceneAsset asset)
    {
        _compileDiagnostics.Clear();
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, _compileDiagnostics);
        CompileCount++;

        _bindings = CutsceneBindingTable.Resolve(compiled, _resolver);
        for (int slot = 0; slot < _bindings.SlotCount; slot++)
            if (_bindings.GetNode(slot)?.Transform is { } transform)
                _boundTransforms.Add(transform);

        _player = new CutscenePlayer(compiled, _bindings, new CutsceneStateSnapshot(_bindings)) { IsPreview = true };
        _player.EventFired += _relayEvent;
        if (_viewport is not null)
        {
            _router = new CutsceneCameraRouter(_player);
            _router.Attach(_viewport);
        }

        OpenResidency(compiled, _bindings, _player);
    }

    private void OpenResidency(CompiledCutscene compiled, CutsceneBindingTable bindings, CutscenePlayer player)
    {
        if (_residencyBackendFactory is null)
            return;

        IResidencyBackend? backend = _residencyBackendFactory();
        if (backend is null)
        {
            _compileDiagnostics.Add(new CutsceneCompileDiagnostic(-1, -1, "The residency backend factory gave no backend. The preview runs without residency."));
            return;
        }

        CutsceneResidencySession residency;
        try
        {
            residency = CutsceneResidencySession.Create(compiled, bindings, backend, _residencyCollector, _compileDiagnostics);
        }
        catch
        {
            (backend as IDisposable)?.Dispose();
            throw;
        }

        residency.Planner.PlaybackWaitEnabled = _playbackWaitEnabled;
        residency.Planner.PlaybackWaitMaximumSeconds = _playbackWaitMaximumSeconds;
        _residency = residency;
        residency.Open(player);
    }

    private void Release()
    {
        CutscenePlayer? player = _player;
        CutsceneBindingTable? bindings = _bindings;
        CutsceneCameraRouter? router = _router;
        CutsceneResidencySession? residency = _residency;
        _player = null;
        _bindings = null;
        _router = null;
        _residency = null;
        if (player is not null)
            player.EventFired -= _relayEvent;

        // A restore failure must not leave the camera claim or the spawned instances behind.
        // The planner closes first, so that no completion arrives for a closed preview.
        try
        {
            try
            {
                residency?.Close();
            }
            finally
            {
                player?.StopAndRestoreAll();
            }
        }
        finally
        {
            try
            {
                router?.Detach();
            }
            finally
            {
                bindings?.Dispose();
            }
        }
    }

    /// <summary>
    /// Replaces the compiled data. The old player first restores every original value, so the new snapshot records true originals.
    /// The transport state, the rate, and the position stay. A pending seek takes priority over the old position.
    /// </summary>
    private void Recompile()
    {
        _recompileRequested = false;
        CutscenePlayer previous = _player!;
        ECutsceneTransportState state = previous.State;
        double rate = previous.Rate;
        CutsceneTime position = previous.Position;
        bool eventsAtPositionPending = previous.EventsAtPositionPending;
        bool finishedRaised = previous.FinishedRaised;

        Release();
        Open(_asset!);

        // The new player continues at the old position. A recompile is an edit and not a move in time,
        // so the events up to the position must not fire again.
        CutscenePlayer player = _player!;
        player.RestorePosition(position, eventsAtPositionPending, finishedRaised);
        player.SetRate(rate);
        switch (state)
        {
            case ECutsceneTransportState.Playing:
                player.Play();
                break;
            case ECutsceneTransportState.Scrubbing:
                player.Pause();
                player.BeginScrub();
                break;
            default:
                player.Pause();
                break;
        }

        if (!_hasPendingSeek)
        {
            _pendingTarget = position;
            _pendingIntent = ESeekIntent.Jump;
            _hasPendingSeek = true;
        }
    }

    /// <summary>
    /// Shortens a play step so that it stops at the edge of the loop range. A longer step would fire the events
    /// after the loop end on each pass, before the wrap moves the position back.
    /// </summary>
    private double LimitStepToLoopRange(CutscenePlayer player, double deltaSeconds)
    {
        double rate = player.Rate;
        CutsceneTime position = player.Position;
        if (rate > 0.0 && position < _loopEnd)
            return Math.Min(deltaSeconds, (_loopEnd - position).ToSeconds() / rate);
        if (rate < 0.0 && position > _loopStart)
            return Math.Min(deltaSeconds, (position - _loopStart).ToSeconds() / -rate);
        return deltaSeconds;
    }

    private void RelayEvent(CutsceneFiredEvent fired)
        => EventFired?.Invoke(fired);

    private void WrapInsideLoopRange(CutscenePlayer player)
    {
        double rate = player.Rate;
        CutsceneTime position = player.Position;
        if (rate > 0.0 && position >= _loopEnd)
            Seek(_loopStart, ESeekIntent.Jump);
        else if (rate < 0.0 && position <= _loopStart)
            Seek(_loopEnd, ESeekIntent.Jump);
        else
            return;

        // The player pauses when it reaches the end of the cutscene. A loop continues from there.
        player.Play();
    }
}
