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
/// The session sends only texture assets to the backend. Other kinds have no backend yet, so <see cref="Create"/> reports
/// their count in one diagnostic, and they stay at the residency that visibility selects.
/// </para>
/// </remarks>
public sealed class CutsceneResidencySession : ICutscenePlaybackGate, IDisposable
{
    private readonly IResidencyBackend _backend;
    private readonly bool _ownsBackend;
    private CutscenePlayer? _player;
    private bool _updatedThisTick;
    private bool _closed;

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

    /// <summary>The planner of the session.</summary>
    public CutsceneResidencyPlanner Planner { get; }

    /// <summary>The backend of the session.</summary>
    public IResidencyBackend Backend => _backend;

    /// <summary>The manifest that the planner uses: the texture assets of the baked or the approximate manifest.</summary>
    public CutsceneResidencyManifest Manifest => Planner.Manifest;

    /// <summary>The state of the baked manifest of the asset at the open.</summary>
    public ECutsceneManifestState ManifestState { get; }

    /// <summary>True if the planner uses the approximate manifest, because the baked manifest is missing or stale.</summary>
    public bool IsApproximate => ManifestState != ECutsceneManifestState.Current;

    /// <summary>True after <see cref="Close"/>.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Selects the manifest of an asset and creates a session. A current baked manifest is used as it is.
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
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(textureBackend);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(diagnostics);

        CutsceneAsset asset = compiled.Asset;
        CutsceneResidencyManifest? manifest = null;
        ECutsceneManifestState state;
        try
        {
            state = CutsceneManifestHash.GetState(asset);
            if (state == ECutsceneManifestState.Current)
            {
                manifest = asset.Manifest!;
                manifest.Validate();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            state = ECutsceneManifestState.Stale;
            manifest = null;
            Add(diagnostics, $"The residency manifest cannot be used: {ex.Message}");
        }

        if (manifest is null)
        {
            manifest = CutsceneApproximateManifest.Build(compiled, bindings, collector);
            string reason = state == ECutsceneManifestState.Missing
                ? "The cutscene has no residency manifest"
                : "The residency manifest is stale, because the cutscene changed after the bake";
            Add(diagnostics, $"{reason}. Residency uses the approximate manifest from the track ranges and the root bounds. Bake the manifest to fix this.");
        }

        int otherKinds = manifest.Assets.Length - manifest.CountKind(EResidencyAssetKind.Texture);
        if (otherKinds > 0)
            Add(diagnostics, $"{otherKinds} mesh or audio assets of the residency manifest have no residency backend. They stay at the residency that visibility selects.");

        return new CutsceneResidencySession(manifest.SelectKind(EResidencyAssetKind.Texture), textureBackend, state);
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

        _player = player;
        player.PlaybackGate = this;
        Planner.Open();
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

    private static void Add(List<CutsceneCompileDiagnostic> diagnostics, string message)
    {
        diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1, message));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene residency: {0}", message);
    }
}
