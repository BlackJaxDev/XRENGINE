namespace XREngine.Cutscenes;

/// <summary>
/// Connects a <see cref="CutsceneResidencyPlanner"/> and its backend to a <see cref="CutscenePlayer"/> for the life of one
/// open cutscene. The director and the editor preview own one session each.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: <see cref="Create"/> selects the manifest and makes the planner. The constructor attaches the planner to
/// the backend. <see cref="Open"/> requests T0 for each asset and sets the session as the playback gate of the player.
/// Each frame, call <see cref="BeginTick"/>, then <see cref="CutscenePlayer.Tick"/>, then <see cref="CompleteTick"/>.
/// A play step updates the planner through the gate, before the player evaluates. Any other tick updates it in
/// <see cref="CompleteTick"/> with the seek intent of the tick. <see cref="Close"/> closes the planner and then disposes
/// the backend.
/// </para>
/// <para>
/// A host that replaces the player and the compiled timeline, for example an editor preview after an edit, keeps the
/// session and the backend. It calls <see cref="AttachPlayer"/> at once and <see cref="Refresh"/> later, when the edits
/// stop. A refresh swaps only the manifest and the planner state; the backend and its budget profile stay.
/// </para>
/// <para>
/// The session sends only texture assets to the backend. Other kinds have no backend yet, so the session reports their
/// count in one diagnostic, and they stay at the residency that visibility selects. A manifest diagnostic is reported one
/// time for each manifest state: a refresh that gives the same state adds nothing.
/// </para>
/// </remarks>
public sealed class CutsceneResidencySession : ICutscenePlaybackGate, IDisposable
{
    private readonly IResidencyBackend _backend;
    private readonly bool _ownsBackend;
    private CutscenePlayer? _player;
    private bool _updatedThisTick;
    private bool _opened;
    private bool _closed;
    private bool _playbackWaitEnabled;
    private double _playbackWaitMaximumSeconds = CutsceneResidencyPlanner.DefaultPlaybackWaitMaximumSeconds;
    private ECutsceneManifestState? _reportedState;
    private int _reportedOtherKinds;

    /// <summary>Creates a session for a manifest of texture assets. The constructor attaches the planner to the backend.</summary>
    /// <param name="textureManifest">The manifest of the assets that the backend loads.</param>
    /// <param name="backend">The texture residency backend.</param>
    /// <param name="manifestState">The state of the manifest of the asset.</param>
    /// <param name="ownsBackend">True to dispose the backend at <see cref="Close"/> if it is disposable.</param>
    public CutsceneResidencySession(CutsceneResidencyManifest textureManifest, IResidencyBackend backend, ECutsceneManifestState manifestState, bool ownsBackend = true)
    {
        ArgumentNullException.ThrowIfNull(textureManifest);
        ArgumentNullException.ThrowIfNull(backend);

        _backend = backend;
        _ownsBackend = ownsBackend;
        ManifestState = manifestState;
        Planner = new CutsceneResidencyPlanner(textureManifest, backend);
        backend.AttachPlanner(Planner);
    }

    /// <summary>The planner of the session. <see cref="Refresh"/> replaces it.</summary>
    public CutsceneResidencyPlanner Planner { get; private set; }

    /// <summary>The backend of the session.</summary>
    public IResidencyBackend Backend => _backend;

    /// <summary>The manifest that the planner uses: the texture assets of the baked or the approximate manifest.</summary>
    public CutsceneResidencyManifest Manifest => Planner.Manifest;

    /// <summary>The state of the baked manifest of the asset at the last selection.</summary>
    public ECutsceneManifestState ManifestState { get; private set; }

    /// <summary>True if the planner uses the approximate manifest, because the baked manifest is missing, stale, or invalid.</summary>
    public bool IsApproximate => ManifestState != ECutsceneManifestState.Current;

    /// <summary>True after <see cref="Close"/>.</summary>
    public bool IsClosed => _closed;

    /// <summary>The number of times that <see cref="Refresh"/> replaced the planner.</summary>
    public int RefreshCount { get; private set; }

    /// <summary>The playback wait setting. It applies to the current planner and to each planner of a later refresh.</summary>
    public bool PlaybackWaitEnabled
    {
        get => _playbackWaitEnabled;
        set
        {
            _playbackWaitEnabled = value;
            Planner.PlaybackWaitEnabled = value;
        }
    }

    /// <summary>The maximum playback wait in seconds. It must be greater than zero. It applies to each planner of the session.</summary>
    public double PlaybackWaitMaximumSeconds
    {
        get => _playbackWaitMaximumSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _playbackWaitMaximumSeconds = value;
            Planner.PlaybackWaitMaximumSeconds = value;
        }
    }

    /// <summary>
    /// Selects the manifest of an asset and creates a session. A current, valid baked manifest is used as it is.
    /// A missing, stale, or invalid manifest gives the approximate manifest and one diagnostic.
    /// Assets of a kind that has no backend give one more diagnostic.
    /// </summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="bindings">The resolved bindings of the cutscene.</param>
    /// <param name="textureBackend">The texture residency backend. The session owns it.</param>
    /// <param name="collector">The collector for the approximate manifest.</param>
    /// <param name="diagnostics">The list that receives the diagnostics.</param>
    public static CutsceneResidencySession Create(
        CompiledCutscene compiled,
        CutsceneBindingTable bindings,
        IResidencyBackend textureBackend,
        ICutsceneResidencyAssetCollector collector,
        List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(textureBackend);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Selection selection = Select(compiled, bindings, collector);
        CutsceneResidencySession session = new(selection.TextureManifest, textureBackend, selection.State);
        session.Report(selection, diagnostics);
        return session;
    }

    /// <summary>
    /// Creates and opens a session, and never throws for a residency failure. A null backend or an exception from the
    /// backend factory, the manifest selection, or the open gives one diagnostic, and the result is null: the cutscene
    /// plays without residency. A failure disposes the backend and leaves no gate on the player.
    /// </summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="bindings">The resolved bindings of the cutscene.</param>
    /// <param name="createBackend">Creates the texture residency backend. The session owns it.</param>
    /// <param name="collector">The collector for the approximate manifest.</param>
    /// <param name="player">The player of the open cutscene.</param>
    /// <param name="playbackWaitEnabled">The playback wait setting.</param>
    /// <param name="playbackWaitMaximumSeconds">The maximum playback wait in seconds.</param>
    /// <param name="diagnostics">The list that receives the diagnostics.</param>
    /// <returns>The open session, or null if residency is off for this open.</returns>
    public static CutsceneResidencySession? TryOpen(
        CompiledCutscene compiled,
        CutsceneBindingTable bindings,
        Func<IResidencyBackend?> createBackend,
        ICutsceneResidencyAssetCollector collector,
        CutscenePlayer player,
        bool playbackWaitEnabled,
        double playbackWaitMaximumSeconds,
        List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(createBackend);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(diagnostics);

        IResidencyBackend? backend = null;
        CutsceneResidencySession? session = null;
        try
        {
            backend = createBackend();
            if (backend is null)
            {
                Add(diagnostics, "Residency is on, but no residency backend factory gave a backend. Residency is off for this open.");
                return null;
            }

            session = Create(compiled, bindings, backend, collector, diagnostics);
            session.PlaybackWaitEnabled = playbackWaitEnabled;
            session.PlaybackWaitMaximumSeconds = playbackWaitMaximumSeconds;
            session.Open(player);
            return session;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                if (session is not null)
                    session.Close();
                else
                    (backend as IDisposable)?.Dispose();
            }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
            {
                Debug.LogException(cleanup, "The residency session did not close cleanly after a failed open.");
            }

            if (ReferenceEquals(player.PlaybackGate, session) && session is not null)
                player.PlaybackGate = null;
            Add(diagnostics, $"Residency failed to open, so the cutscene plays without residency: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Selects the manifest again after the timeline changed, and replaces the planner. The backend, its budget profile,
    /// the player gate, and the playback wait settings stay. The old planner closes first, so its tickets release. An open
    /// session opens the new planner. A diagnostic is added only if the manifest state changed.
    /// </summary>
    /// <param name="compiled">The new compiled cutscene.</param>
    /// <param name="bindings">The resolved bindings of the new compile.</param>
    /// <param name="collector">The collector for the approximate manifest.</param>
    /// <param name="diagnostics">The list that receives the diagnostics.</param>
    public void Refresh(CompiledCutscene compiled, CutsceneBindingTable bindings, ICutsceneResidencyAssetCollector collector, List<CutsceneCompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (_closed)
            throw new InvalidOperationException("A closed residency session cannot refresh.");

        Selection selection = Select(compiled, bindings, collector);
        CutsceneResidencyPlanner planner = new(selection.TextureManifest, _backend)
        {
            PlaybackWaitEnabled = _playbackWaitEnabled,
            PlaybackWaitMaximumSeconds = _playbackWaitMaximumSeconds,
        };

        CutsceneResidencyPlanner previous = Planner;
        planner.LookAhead = previous.LookAhead;
        planner.LookBehind = previous.LookBehind;
        planner.DwellSeconds = previous.DwellSeconds;
        planner.DwellMoveThreshold = previous.DwellMoveThreshold;
        previous.Close();

        Planner = planner;
        ManifestState = selection.State;
        RefreshCount++;
        _backend.AttachPlanner(planner);
        if (_opened)
            planner.Open();
        Report(selection, diagnostics);
    }

    /// <summary>
    /// Requests T0 for each asset and sets this session as the playback gate of the player.
    /// The playback wait settings of the planner apply from the next play step.
    /// </summary>
    /// <param name="player">The player of the open cutscene.</param>
    public void Open(CutscenePlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_closed)
            throw new InvalidOperationException("A closed residency session cannot open again.");
        if (_opened)
            throw new InvalidOperationException("The residency session is already open.");

        AttachPlayer(player);
        _opened = true;
        Planner.Open();
    }

    /// <summary>
    /// Moves the playback gate to another player, for example the player of a new compile. The old player loses the gate.
    /// </summary>
    /// <param name="player">The new player.</param>
    public void AttachPlayer(CutscenePlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_closed)
            return;

        if (_player is { } previous && !ReferenceEquals(previous, player) && ReferenceEquals(previous.PlaybackGate, this))
            previous.PlaybackGate = null;
        _player = player;
        player.PlaybackGate = this;
    }

    /// <summary>Marks the start of a frame. Call it before <see cref="CutscenePlayer.Tick"/>.</summary>
    public void BeginTick()
        => _updatedThisTick = false;

    /// <summary>
    /// Updates the planner after <see cref="CutscenePlayer.Tick"/>, unless the play step of the tick updated it through the gate.
    /// A paused or scrubbing player reports rate zero. The seek intent comes from <see cref="CutscenePlayer.LastTickSeek"/>.
    /// </summary>
    /// <param name="player">The player that ticked.</param>
    /// <param name="deltaSeconds">The wall time of the frame, in seconds.</param>
    public void CompleteTick(CutscenePlayer player, double deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_closed || _updatedThisTick)
            return;

        double rate = player.State == ECutsceneTransportState.Playing ? player.Rate : 0.0;
        Update(player.Position, rate, player.LastTickSeek, deltaSeconds);
    }

    /// <summary>
    /// Updates the planner in a frame in which the host does not tick the player, for example an editor preview that is
    /// paused. The update has no seek, so the dwell gate of a scrub can open.
    /// </summary>
    /// <param name="player">The player that did not tick.</param>
    /// <param name="deltaSeconds">The wall time of the frame, in seconds.</param>
    public void UpdateIdle(CutscenePlayer player, double deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_closed)
            return;

        double rate = player.State == ECutsceneTransportState.Playing ? player.Rate : 0.0;
        Update(player.Position, rate, null, deltaSeconds);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// While the planner holds playback, the result is the last flick before the camera cut, so the frame never shows the
    /// new shot. A step that does not reach the cut is not changed.
    /// </remarks>
    public CutsceneTime LimitPlayStep(CutsceneTime from, CutsceneTime to, double rate, ESeekIntent? seek, double deltaSeconds)
    {
        if (_closed)
            return to;

        _updatedThisTick = true;
        Update(to, rate, seek, deltaSeconds);
        if (!Planner.IsHoldingPlayback)
            return to;

        long beforeCut = Planner.PlaybackHoldTime.Flicks - 1;
        return new CutsceneTime(Math.Max(from.Flicks, Math.Min(to.Flicks, beforeCut)));
    }

    /// <summary>
    /// Closes the planner, which cancels all work and releases all levels, removes the playback gate from the player,
    /// and disposes the backend if the session owns it. A second call does nothing.
    /// </summary>
    public void Close()
    {
        if (_closed)
            return;
        _closed = true;

        if (_player is { } player && ReferenceEquals(player.PlaybackGate, this))
            player.PlaybackGate = null;
        _player = null;

        try
        {
            Planner.Close();
        }
        finally
        {
            if (_ownsBackend && _backend is IDisposable disposable)
                disposable.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
        => Close();

    private void Update(CutsceneTime position, double rate, ESeekIntent? seek, double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
            deltaSeconds = 0.0;

        _backend.SetPlayhead(position, rate);
        Planner.Update(position, rate, seek, deltaSeconds);
    }

    private void Report(Selection selection, List<CutsceneCompileDiagnostic> diagnostics)
    {
        if (selection.State == ECutsceneManifestState.Current)
            _reportedState = ECutsceneManifestState.Current;
        else if (_reportedState != selection.State)
        {
            _reportedState = selection.State;
            Add(diagnostics, selection.Message!);
        }

        if (selection.OtherKinds > 0 && selection.OtherKinds != _reportedOtherKinds)
            Add(diagnostics, $"{selection.OtherKinds} mesh or audio assets of the residency manifest have no residency backend. They stay at the residency that visibility selects.");
        _reportedOtherKinds = selection.OtherKinds;
    }

    private static Selection Select(CompiledCutscene compiled, CutsceneBindingTable bindings, ICutsceneResidencyAssetCollector collector)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(collector);

        CutsceneAsset asset = compiled.Asset;
        CutsceneResidencyManifest? manifest = null;
        ECutsceneManifestState state;
        string? reason = null;
        try
        {
            state = CutsceneManifestHash.GetState(asset);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            state = ECutsceneManifestState.Invalid;
            reason = $"The content hash of the cutscene could not be computed: {ex.Message}";
        }

        if (state == ECutsceneManifestState.Current)
        {
            try
            {
                asset.Manifest!.Validate();
                manifest = asset.Manifest;
            }
            catch (ArgumentException ex)
            {
                state = ECutsceneManifestState.Invalid;
                reason = $"The residency manifest is not valid: {ex.Message}";
            }
        }

        string? message = null;
        if (manifest is null)
        {
            manifest = CutsceneApproximateManifest.Build(compiled, bindings, collector);
            reason ??= state == ECutsceneManifestState.Missing
                ? "The cutscene has no residency manifest"
                : "The residency manifest is stale, because the cutscene changed after the bake";
            message = $"{reason.TrimEnd('.')}. Residency uses the approximate manifest from the track ranges and the root bounds. Bake the manifest to fix this.";
        }

        int otherKinds = manifest.Assets.Length - manifest.CountKind(EResidencyAssetKind.Texture);
        return new Selection(manifest.SelectKind(EResidencyAssetKind.Texture), state, message, otherKinds);
    }

    private static void Add(List<CutsceneCompileDiagnostic> diagnostics, string message)
    {
        diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1, message));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene residency: {0}", message);
    }

    private readonly record struct Selection(CutsceneResidencyManifest TextureManifest, ECutsceneManifestState State, string? Message, int OtherKinds);
}
