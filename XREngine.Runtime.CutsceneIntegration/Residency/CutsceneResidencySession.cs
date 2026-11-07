using System.Text;

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
/// The session sends only texture assets to the texture backend. Audio assets go to an audio backend
/// (<see cref="AudioResidencyBackend"/> by default) with its own planner, <see cref="AudioPlanner"/>. The audio planner
/// gets the same playhead updates. It never holds playback, and memory pressure does not release its data. Mesh assets
/// have no backend yet, so the session reports their count in one diagnostic, and they stay at the residency that
/// visibility selects. A manifest diagnostic is reported one time for each reason: a refresh that gives the same reason
/// adds nothing.
/// </para>
/// <para>
/// A refresh keeps the audio backend and its open decoders when the audio inputs did not change. A refresh makes the new
/// audio part before it changes anything, so a throw leaves the session as it was.
/// The default audio backend exists only for a cutscene with audio proxies. Its manifest comes from the audio clips of the
/// timeline (<see cref="AudioResidencyBackend.BuildManifest"/>), because the bake lists each proxy with no bucket entries:
/// sound has no screen coverage, so each clip range needs T2 of its source.
/// </para>
/// <para>
/// Video assets go to a video backend (<see cref="VideoResidencyBackend"/> by default) with its own planner,
/// <see cref="VideoPlanner"/>, in the same way. Its manifest comes from the video clips of the timeline
/// (<see cref="VideoResidencyBackend.BuildManifest"/>). The session gives that backend to the video tracks of the player,
/// so a track copies a decoded frame from the backend cache on a miss of its own cache.
/// </para>
/// <para>
/// A manifest selection calls <see cref="IResidencyBackend.BeginSelection"/> and then resolves each texture of the manifest
/// one time (<see cref="IResidencyBackend.CanResolve"/>). If more than
/// <see cref="CutsceneManifestMatch.MaxUnresolvedTextureFraction"/> of the textures of a current baked manifest are not
/// found, the manifest does not match the scene: the state is <see cref="ECutsceneManifestState.Unmatched"/>, and the session
/// uses the approximate manifest. While the backend reports <see cref="IResidencyBackend.AreAssetsLoading"/>, a missing
/// texture can still load, so the session keeps the baked manifest and selects again one time when the loading ends. The
/// new selection replaces the planner, so it waits while the planner holds playback at a cut. After
/// <see cref="AttachPlayer"/> with another player (a preview recompile), the stored inputs are old, so the selection waits
/// for the next <see cref="Refresh"/> of the host.
/// Textures that are not found are not sent to the backend, and one diagnostic gives their count.
/// </para>
/// <para>
/// After each planner update, the session reads <see cref="IResidencyBackend.IsUnderMemoryPressure"/>. Under pressure, it
/// asks the planner to release the T1 and T2 data of at most <see cref="PressureEvictionsPerUpdate"/> assets outside the
/// request window, farthest from the playhead first, at most one time for each new budget evaluation
/// (<see cref="IResidencyBackend.MemoryPressureFrameId"/>). A flag whose evaluation does not advance releases nothing more.
/// T0 data and the data in the window stay. With no pressure, the check allocates nothing.
/// </para>
/// </remarks>
public sealed class CutsceneResidencySession : ICutscenePlaybackGate, IDisposable
{
    /// <summary>The maximum number of assets that release T1 and T2 data in one update under memory pressure.</summary>
    public const int PressureEvictionsPerUpdate = 4;

    private readonly IResidencyBackend _backend;
    private readonly bool _ownsBackend;
    private Func<CompiledCutscene, IResidencyBackend?> _createAudioBackend = CreateDefaultAudioBackend;
    private IResidencyBackend? _audioBackend;
    private int _reportedUnroutedAudio;
    private string? _audioSignature;
    private string? _audioUnroutedReason;
    private Func<CompiledCutscene, IResidencyBackend?> _createVideoBackend = CreateDefaultVideoBackend;
    private IResidencyBackend? _videoBackend;
    private int _reportedUnroutedVideo;
    private string? _videoSignature;
    private string? _videoUnroutedReason;
    private CutscenePlayer? _player;
    private bool _updatedThisTick;
    private bool _opened;
    private bool _closed;
    private bool _playbackWaitEnabled;
    private double _playbackWaitMaximumSeconds = CutsceneResidencyPlanner.DefaultPlaybackWaitMaximumSeconds;
    private string? _reportedReason;
    private int _reportedOtherKinds;
    private int _reportedUnresolved;
    private bool _selectWhenLoaded;
    private CompiledCutscene? _selectedCompiled;
    private CutsceneBindingTable? _selectedBindings;
    private ICutsceneResidencyAssetCollector? _selectedCollector;
    private List<CutsceneCompileDiagnostic>? _selectedDiagnostics;
    private long _lastPressureFrameId = long.MinValue;

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

    /// <summary>
    /// The planner of the audio assets, or null if the cutscene has no audio backend. <see cref="Refresh"/> replaces it
    /// only when the audio inputs (the audio clips, the proxies, and the audio assets of the manifest) changed.
    /// It never holds playback.
    /// </summary>
    public CutsceneResidencyPlanner? AudioPlanner { get; private set; }

    /// <summary>The backend of <see cref="AudioPlanner"/>, or null. The session owns it.</summary>
    public IResidencyBackend? AudioBackend => _audioBackend;

    /// <summary>
    /// The planner of the video assets, or null if the cutscene has no video backend. <see cref="Refresh"/> replaces it
    /// only when the video inputs (the video clips and the video assets of the manifest) changed. It never holds playback.
    /// </summary>
    public CutsceneResidencyPlanner? VideoPlanner { get; private set; }

    /// <summary>The backend of <see cref="VideoPlanner"/>, or null. The session owns it.</summary>
    public IResidencyBackend? VideoBackend => _videoBackend;

    /// <summary>The manifest that the planner uses: the texture assets of the baked or the approximate manifest.</summary>
    public CutsceneResidencyManifest Manifest => Planner.Manifest;

    /// <summary>The state of the baked manifest of the asset at the last selection.</summary>
    public ECutsceneManifestState ManifestState { get; private set; }

    /// <summary>
    /// True if the planner uses the approximate manifest, because the baked manifest is missing, stale, invalid, or does not
    /// match the scene.
    /// </summary>
    public bool IsApproximate => ManifestState != ECutsceneManifestState.Current;

    /// <summary>
    /// True while the session keeps a baked manifest with textures that are not loaded yet, and waits for the backend to end
    /// its loading. The session then selects the manifest again one time.
    /// </summary>
    public bool IsWaitingForAssets => _selectWhenLoaded;

    /// <summary>True after <see cref="Close"/>.</summary>
    public bool IsClosed => _closed;

    /// <summary>The number of times that <see cref="Refresh"/> replaced the planner.</summary>
    public int RefreshCount { get; private set; }

    /// <summary>The number of assets that released T1 or T2 data because the backend reported memory pressure.</summary>
    public int PressureEvictionCount { get; private set; }

    /// <summary>The number of textures of the selected manifest that the backend did not find at the last selection.</summary>
    public int UnresolvedTextureCount { get; private set; }

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
    /// Selects the manifest of an asset and creates a session. A current, valid baked manifest whose textures the backend
    /// finds is used as it is. A missing, stale, or invalid manifest, or a baked manifest with too many textures that the
    /// backend does not find, gives the approximate manifest and one diagnostic.
    /// Assets of a kind that has no backend give one more diagnostic. The audio assets go to an audio planner.
    /// </summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="bindings">The resolved bindings of the cutscene.</param>
    /// <param name="textureBackend">The texture residency backend. The session owns it.</param>
    /// <param name="collector">The collector for the approximate manifest.</param>
    /// <param name="diagnostics">The list that receives the diagnostics.</param>
    /// <param name="createAudioBackend">
    /// Creates the audio backend of a compile, or gives null for no audio planner. The session owns the result. Null uses
    /// <see cref="CreateDefaultAudioBackend"/>. The session calls it again at a <see cref="Refresh"/> whose audio inputs changed.
    /// </param>
    /// <param name="createVideoBackend">
    /// Creates the video backend of a compile, or gives null for no video planner. The session owns the result. Null uses
    /// <see cref="CreateDefaultVideoBackend"/>. The session calls it again at a <see cref="Refresh"/> whose video inputs changed.
    /// </param>
    public static CutsceneResidencySession Create(
        CompiledCutscene compiled,
        CutsceneBindingTable bindings,
        IResidencyBackend textureBackend,
        ICutsceneResidencyAssetCollector collector,
        List<CutsceneCompileDiagnostic> diagnostics,
        Func<CompiledCutscene, IResidencyBackend?>? createAudioBackend = null,
        Func<CompiledCutscene, IResidencyBackend?>? createVideoBackend = null)
    {
        ArgumentNullException.ThrowIfNull(textureBackend);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Selection selection = Select(compiled, bindings, collector, textureBackend);
        CutsceneResidencySession session = new(selection.TextureManifest, textureBackend, selection.State);
        try
        {
            if (createAudioBackend is not null)
                session._createAudioBackend = createAudioBackend;
            if (createVideoBackend is not null)
                session._createVideoBackend = createVideoBackend;
            session.InstallAudio(session.PrepareAudio(compiled, selection, GetAudioSignature(compiled, selection)));
            session.InstallVideo(session.PrepareVideo(compiled, selection, GetVideoSignature(compiled, selection)));
        }
        catch
        {
            // The caller still owns the texture backend after a failed create, as after a failed selection.
            session.CloseAudio();
            session.CloseVideo();
            throw;
        }
        session.KeepSelectionInputs(compiled, bindings, collector, diagnostics, selection);
        session.Report(selection, diagnostics);
        return session;
    }

    /// <summary>
    /// The default audio backend: an <see cref="AudioResidencyBackend"/> for a cutscene with audio proxies, or null for a
    /// cutscene with none.
    /// </summary>
    /// <param name="compiled">The compiled cutscene.</param>
    public static IResidencyBackend? CreateDefaultAudioBackend(CompiledCutscene compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        return compiled.Asset.AudioProxies is { Count: > 0 } ? new AudioResidencyBackend(compiled) : null;
    }

    /// <summary>
    /// The default video backend: a <see cref="VideoResidencyBackend"/> for a cutscene with video clips when a media
    /// backend is registered (<see cref="Rendering.VideoStreaming.HlsReferenceRuntime.Backend"/>), or null otherwise.
    /// With no media backend, the video tracks report their open failure.
    /// </summary>
    /// <param name="compiled">The compiled cutscene.</param>
    public static IResidencyBackend? CreateDefaultVideoBackend(CompiledCutscene compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        return Rendering.VideoStreaming.HlsReferenceRuntime.Backend is not null && VideoResidencyBackend.HasVideoClips(compiled.Asset)
            ? new VideoResidencyBackend(compiled)
            : null;
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
    /// <param name="createAudioBackend">Creates the audio backend, or null for <see cref="CreateDefaultAudioBackend"/>.</param>
    /// <param name="createVideoBackend">Creates the video backend, or null for <see cref="CreateDefaultVideoBackend"/>.</param>
    /// <returns>The open session, or null if residency is off for this open.</returns>
    public static CutsceneResidencySession? TryOpen(
        CompiledCutscene compiled,
        CutsceneBindingTable bindings,
        Func<IResidencyBackend?> createBackend,
        ICutsceneResidencyAssetCollector collector,
        CutscenePlayer player,
        bool playbackWaitEnabled,
        double playbackWaitMaximumSeconds,
        List<CutsceneCompileDiagnostic> diagnostics,
        Func<CompiledCutscene, IResidencyBackend?>? createAudioBackend = null,
        Func<CompiledCutscene, IResidencyBackend?>? createVideoBackend = null)
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

            session = Create(compiled, bindings, backend, collector, diagnostics, createAudioBackend, createVideoBackend);
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
    /// session opens the new planner. A diagnostic is added only if the reason of the manifest selection changed.
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

        Selection selection = Select(compiled, bindings, collector, _backend);
        CutsceneResidencyPlanner planner = new(selection.TextureManifest, _backend)
        {
            PlaybackWaitEnabled = _playbackWaitEnabled,
            PlaybackWaitMaximumSeconds = _playbackWaitMaximumSeconds,
        };

        // The audio part is made before any change, so a throw leaves the session as it was. With the same audio inputs,
        // the audio backend and its open decoders stay.
        string audioSignature = GetAudioSignature(compiled, selection);
        bool keepAudio = string.Equals(audioSignature, _audioSignature, StringComparison.Ordinal);
        PlannerParts audio = keepAudio ? default : PrepareAudio(compiled, selection, audioSignature);
        string videoSignature = GetVideoSignature(compiled, selection);
        bool keepVideo = string.Equals(videoSignature, _videoSignature, StringComparison.Ordinal);
        PlannerParts video;
        try
        {
            video = keepVideo ? default : PrepareVideo(compiled, selection, videoSignature);
        }
        catch
        {
            if (!keepAudio)
                (audio.Backend as IDisposable)?.Dispose();
            throw;
        }

        try
        {
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
            KeepSelectionInputs(compiled, bindings, collector, diagnostics, selection);
            if (_opened)
                planner.Open();
        }
        catch
        {
            // The prepared parts were not installed, so nothing else releases them.
            if (!keepAudio)
                (audio.Backend as IDisposable)?.Dispose();
            if (!keepVideo)
                (video.Backend as IDisposable)?.Dispose();
            throw;
        }

        if (!keepAudio)
            InstallAudio(audio);
        if (!keepVideo)
            InstallVideo(video);
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
        AudioPlanner?.Open();
        VideoPlanner?.Open();
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

        if (_player is { } previous && !ReferenceEquals(previous, player))
        {
            if (ReferenceEquals(previous.PlaybackGate, this))
                previous.PlaybackGate = null;
            if (ReferenceEquals(previous.Video.Residency, _videoBackend))
                previous.Video.Residency = null;

            // A new player comes with a new compile, and the host can dispose the old binding table. The selection after
            // a load must not use them, so it waits for the next refresh of the host, which gives the new inputs.
            ClearSelectionInputs();
        }
        _player = player;
        player.PlaybackGate = this;
        player.Video.Residency = _videoBackend as VideoResidencyBackend;
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

        try
        {
            Planner.Close();
        }
        finally
        {
            try
            {
                CloseAudio();
                CloseVideo();
            }
            finally
            {
                _player = null;
                if (_ownsBackend && _backend is IDisposable disposable)
                    disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// Makes the audio backend and the audio planner of a compile. It changes no field of the session, so a throw leaves the
    /// session as it was. With no backend or no audio asset, the result holds only the reason for the diagnostic.
    /// </summary>
    private PlannerParts PrepareAudio(CompiledCutscene compiled, Selection selection, string signature)
    {
        IResidencyBackend? backend = _createAudioBackend(compiled);
        if (backend is null)
            return new PlannerParts(null, null, signature, "no audio residency backend was made: the cutscene has no audio proxies, or the audio backend factory gave none");

        try
        {
            CutsceneResidencyManifest manifest = backend is AudioResidencyBackend audio
                ? audio.BuildManifest()
                : selection.FullManifest.SelectKind(EResidencyAssetKind.Audio);
            if (manifest.Assets.Length == 0)
            {
                (backend as IDisposable)?.Dispose();
                return new PlannerParts(null, null, signature, "no audio proxy of the cutscene matches an audio clip");
            }
            return new PlannerParts(backend, new CutsceneResidencyPlanner(manifest, backend), signature, null);
        }
        catch
        {
            (backend as IDisposable)?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Replaces the audio backend and the audio planner. The old planner closes first, so its tickets release, and then the
    /// old backend is disposed. An open session opens the new planner.
    /// </summary>
    private void InstallAudio(PlannerParts parts)
    {
        CloseAudio();
        _audioSignature = parts.Signature;
        _audioUnroutedReason = parts.UnroutedReason;
        if (parts.Planner is not { } planner)
            return;

        _audioBackend = parts.Backend;
        AudioPlanner = planner;
        parts.Backend!.AttachPlanner(planner);
        if (_opened)
            planner.Open();
    }

    /// <summary>
    /// Makes the video backend and the video planner of a compile. It changes no field of the session, so a throw leaves the
    /// session as it was. With no backend or no video asset, the result holds only the reason for the diagnostic.
    /// </summary>
    private PlannerParts PrepareVideo(CompiledCutscene compiled, Selection selection, string signature)
    {
        IResidencyBackend? backend = _createVideoBackend(compiled);
        if (backend is null)
            return new PlannerParts(null, null, signature, "no video residency backend was made: the cutscene has no video clips, no media backend is registered, or the video backend factory gave none");

        try
        {
            CutsceneResidencyManifest manifest = backend is VideoResidencyBackend video
                ? video.BuildManifest()
                : selection.FullManifest.SelectKind(EResidencyAssetKind.Video);
            if (manifest.Assets.Length == 0)
            {
                (backend as IDisposable)?.Dispose();
                return new PlannerParts(null, null, signature, "the cutscene has no video clip with a source file");
            }
            return new PlannerParts(backend, new CutsceneResidencyPlanner(manifest, backend), signature, null);
        }
        catch
        {
            (backend as IDisposable)?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Replaces the video backend and the video planner, and gives the new backend to the player. The old planner closes
    /// first, so its tickets release, and then the old backend is disposed. An open session opens the new planner.
    /// </summary>
    private void InstallVideo(PlannerParts parts)
    {
        CloseVideo();
        _videoSignature = parts.Signature;
        _videoUnroutedReason = parts.UnroutedReason;
        if (parts.Planner is not { } planner)
            return;

        _videoBackend = parts.Backend;
        VideoPlanner = planner;
        parts.Backend!.AttachPlanner(planner);
        if (_player is { } player)
            player.Video.Residency = _videoBackend as VideoResidencyBackend;
        if (_opened)
            planner.Open();
    }

    /// <summary>
    /// Gets the inputs of the video part of a compile as one string: the video clips of the asset, and the video assets of
    /// the selected manifest. A refresh with the same string keeps the video backend and its decoded frames.
    /// </summary>
    private static string GetVideoSignature(CompiledCutscene compiled, Selection selection)
    {
        StringBuilder text = new(VideoResidencyBackend.GetInputSignature(compiled.Asset));
        CutsceneResidencyManifest manifest = selection.FullManifest;
        for (int i = 0; i < manifest.Assets.Length; i++)
            if (manifest.AssetKinds[i] == EResidencyAssetKind.Video)
                text.Append("|a:").Append(manifest.Assets[i].ToString("N"));
        return text.ToString();
    }

    private void CloseVideo()
    {
        _videoSignature = null;
        CutsceneResidencyPlanner? planner = VideoPlanner;
        IResidencyBackend? backend = _videoBackend;
        VideoPlanner = null;
        _videoBackend = null;
        if (_player is { } player && backend is not null && ReferenceEquals(player.Video.Residency, backend))
            player.Video.Residency = null;
        try
        {
            planner?.Close();
        }
        finally
        {
            (backend as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Gets the inputs of the audio part of a compile as one string: the audio clips and proxies of the asset, and the audio
    /// assets of the selected manifest. A refresh with the same string keeps the audio backend.
    /// </summary>
    private static string GetAudioSignature(CompiledCutscene compiled, Selection selection)
    {
        StringBuilder text = new(AudioResidencyBackend.GetInputSignature(compiled.Asset));
        CutsceneResidencyManifest manifest = selection.FullManifest;
        for (int i = 0; i < manifest.Assets.Length; i++)
            if (manifest.AssetKinds[i] == EResidencyAssetKind.Audio)
                text.Append("|a:").Append(manifest.Assets[i].ToString("N"));
        return text.ToString();
    }

    private void CloseAudio()
    {
        _audioSignature = null;
        CutsceneResidencyPlanner? planner = AudioPlanner;
        IResidencyBackend? backend = _audioBackend;
        AudioPlanner = null;
        _audioBackend = null;
        try
        {
            planner?.Close();
        }
        finally
        {
            (backend as IDisposable)?.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
        => Close();

    private void Update(CutsceneTime position, double rate, ESeekIntent? seek, double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
            deltaSeconds = 0.0;

        // A new selection closes the planner, which would end a hold at a cut and let the frame show a shot that is not
        // resident. The selection waits until the hold ends.
        if (_selectWhenLoaded && _selectedCompiled is not null && !Planner.IsHoldingPlayback && !_backend.AreAssetsLoading)
            SelectAgainAfterLoading();

        _backend.SetPlayhead(position, rate);
        Planner.Update(position, rate, seek, deltaSeconds);
        if (AudioPlanner is { } audioPlanner)
        {
            _audioBackend!.SetPlayhead(position, rate);
            audioPlanner.Update(position, rate, seek, deltaSeconds);
        }
        if (VideoPlanner is { } videoPlanner)
        {
            _videoBackend!.SetPlayhead(position, rate);
            videoPlanner.Update(position, rate, seek, deltaSeconds);
        }

        // Only the data outside the window leaves. The streaming budget fit already lowers the textures in the window,
        // and the next plan would request their levels again. A release demotes only after the demotion cooldown, so the
        // flag stays set for some evaluations: release at most one batch for each new evaluation. A flag whose evaluation
        // did not advance, for example when no viewport collects, releases nothing more.
        if (_backend.IsUnderMemoryPressure)
        {
            long frameId = _backend.MemoryPressureFrameId;
            if (frameId != _lastPressureFrameId)
            {
                _lastPressureFrameId = frameId;
                PressureEvictionCount += Planner.Evict(PressureEvictionsPerUpdate, outsideWindowOnly: true);
            }
        }
    }

    private void KeepSelectionInputs(
        CompiledCutscene compiled,
        CutsceneBindingTable bindings,
        ICutsceneResidencyAssetCollector collector,
        List<CutsceneCompileDiagnostic> diagnostics,
        Selection selection)
    {
        _selectedCompiled = compiled;
        _selectedBindings = bindings;
        _selectedCollector = collector;
        _selectedDiagnostics = diagnostics;
        _selectWhenLoaded = selection.SelectWhenLoaded;
    }

    private void ClearSelectionInputs()
    {
        _selectedCompiled = null;
        _selectedBindings = null;
        _selectedCollector = null;
        _selectedDiagnostics = null;
    }

    /// <summary>Selects the manifest one more time after the backend ended its loading. A failure gives one diagnostic.</summary>
    private void SelectAgainAfterLoading()
    {
        _selectWhenLoaded = false;
        if (_selectedCompiled is null || _selectedBindings is null || _selectedCollector is null || _selectedDiagnostics is null)
            return;

        try
        {
            Refresh(_selectedCompiled, _selectedBindings, _selectedCollector, _selectedDiagnostics);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _selectWhenLoaded = false;
            Add(_selectedDiagnostics, $"The residency manifest was not selected again after the asset loading ended. The current manifest stays: {ex.Message}");
        }
    }

    private void Report(Selection selection, List<CutsceneCompileDiagnostic> diagnostics)
    {
        // The reason has no counts, so a refresh with the same reason adds nothing, and a new reason with the same state
        // (for example a key mismatch and then an edit) adds one diagnostic.
        if (selection.ReasonKey is null)
            _reportedReason = null;
        else if (!string.Equals(_reportedReason, selection.ReasonKey, StringComparison.Ordinal))
        {
            _reportedReason = selection.ReasonKey;
            Add(diagnostics, selection.Message!);
        }

        if (selection.MeshAssets > 0 && selection.MeshAssets != _reportedOtherKinds)
            Add(diagnostics, $"{selection.MeshAssets} mesh assets of the residency manifest have no residency backend. They stay at the residency that visibility selects.");
        _reportedOtherKinds = selection.MeshAssets;

        // The audio assets of the manifest go to the audio planner. With no audio planner, nothing loads their full decoders
        // ahead of the playhead; the track still opens them at playback.
        int unroutedAudio = AudioPlanner is null ? selection.AudioAssets : 0;
        if (unroutedAudio > 0 && unroutedAudio != _reportedUnroutedAudio)
            Add(diagnostics, $"{unroutedAudio} audio assets of the residency manifest have no audio residency backend, because {_audioUnroutedReason ?? "the audio part was not made"}. The audio tracks open their files only when they play.");
        _reportedUnroutedAudio = unroutedAudio;

        int unroutedVideo = VideoPlanner is null ? selection.VideoAssets : 0;
        if (unroutedVideo > 0 && unroutedVideo != _reportedUnroutedVideo)
            Add(diagnostics, $"{unroutedVideo} video assets of the residency manifest have no video residency backend, because {_videoUnroutedReason ?? "the video part was not made"}. The video tracks decode only when they show.");
        _reportedUnroutedVideo = unroutedVideo;

        UnresolvedTextureCount = selection.Unresolved;
        if (selection.Unresolved > 0 && selection.Unresolved != _reportedUnresolved)
            Add(diagnostics, $"{selection.Unresolved} of {selection.Textures} texture assets of the residency manifest are not loaded textures now. Residency skips them, and they stay at the residency that visibility selects.");
        _reportedUnresolved = selection.Unresolved;
    }

    private static Selection Select(CompiledCutscene compiled, CutsceneBindingTable bindings, ICutsceneResidencyAssetCollector collector, IResidencyBackend backend)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(collector);

        backend.BeginSelection();
        CutsceneAsset asset = compiled.Asset;
        CutsceneResidencyManifest? manifest = null;
        ECutsceneManifestState state;
        string? reason = null;
        string? reasonKey = null;
        try
        {
            state = CutsceneManifestHash.GetState(asset);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            state = ECutsceneManifestState.Invalid;
            reason = $"The content hash of the cutscene could not be computed: {ex.Message}";
            reasonKey = "invalid-hash";
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
                reasonKey = "invalid-data";
            }
        }

        // The content hash does not see the scene. A move of the assets, or assets outside the asset folders, can change the
        // asset keys, so the session checks that the baked textures are loaded. During a load they can still come.
        bool[]? resolved = null;
        int textures = 0;
        int unresolved = 0;
        bool selectWhenLoaded = false;
        bool loading = backend.AreAssetsLoading;
        if (manifest is not null)
        {
            resolved = ResolveTextures(manifest, backend, out textures, out unresolved);
            selectWhenLoaded = unresolved > 0 && loading;
            if (!loading && CutsceneManifestMatch.IsUnmatched(textures, unresolved))
            {
                state = ECutsceneManifestState.Unmatched;
                reasonKey = "unmatched";
                reason = $"{unresolved} of {textures} baked textures of the residency manifest are not loaded textures of the scene, so the baked manifest does not match the scene. Residency uses the approximate manifest from the track ranges and the root bounds. If the scene assets moved, or are outside the game and engine asset folders, bake the manifest again.";
                manifest = null;
                resolved = null;
            }
        }

        string? message = null;
        if (manifest is null)
        {
            manifest = CutsceneApproximateManifest.Build(compiled, bindings, collector);
            if (state == ECutsceneManifestState.Unmatched)
                message = reason;
            else
            {
                reasonKey ??= state == ECutsceneManifestState.Missing ? "missing" : "stale";
                reason ??= state == ECutsceneManifestState.Missing
                    ? "The cutscene has no residency manifest"
                    : "The residency manifest is stale, because the cutscene changed after the bake";
                message = $"{reason.TrimEnd('.')}. Residency uses the approximate manifest from the track ranges and the root bounds. Bake the manifest to fix this.";
            }

            resolved = ResolveTextures(manifest, backend, out textures, out unresolved);
        }

        int meshAssets = manifest.CountKind(EResidencyAssetKind.Mesh);
        int audioAssets = manifest.CountKind(EResidencyAssetKind.Audio);
        int videoAssets = manifest.CountKind(EResidencyAssetKind.Video);
        Predicate<Guid>? include = null;
        if (unresolved > 0)
        {
            HashSet<Guid> missing = new(unresolved);
            for (int i = 0; i < resolved!.Length; i++)
                if (!resolved[i])
                    missing.Add(manifest.Assets[i]);
            include = assetId => !missing.Contains(assetId);
        }

        return new Selection(
            manifest.SelectKind(EResidencyAssetKind.Texture, include),
            manifest,
            state,
            message,
            reasonKey,
            meshAssets,
            audioAssets,
            videoAssets,
            textures,
            unresolved,
            selectWhenLoaded);
    }

    /// <summary>Resolves each texture of a manifest one time.</summary>
    private static bool[] ResolveTextures(CutsceneResidencyManifest manifest, IResidencyBackend backend, out int textures, out int unresolved)
    {
        bool[] resolved = new bool[manifest.Assets.Length];
        CutsceneManifestMatch.Resolve(manifest, backend.CanResolve, resolved, out textures, out unresolved);
        return resolved;
    }

    private static void Add(List<CutsceneCompileDiagnostic> diagnostics, string message)
    {
        diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1, message));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene residency: {0}", message);
    }

    private readonly record struct PlannerParts(IResidencyBackend? Backend, CutsceneResidencyPlanner? Planner, string Signature, string? UnroutedReason);

    private readonly record struct Selection(
        CutsceneResidencyManifest TextureManifest,
        CutsceneResidencyManifest FullManifest,
        ECutsceneManifestState State,
        string? Message,
        string? ReasonKey,
        int MeshAssets,
        int AudioAssets,
        int VideoAssets,
        int Textures,
        int Unresolved,
        bool SelectWhenLoaded);
}
