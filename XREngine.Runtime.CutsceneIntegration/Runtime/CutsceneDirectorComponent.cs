using System.ComponentModel;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Cutscenes;

/// <summary>
/// A scene component that plays a <see cref="CutsceneAsset"/>. It is a thin wrapper over <see cref="CutscenePlayer"/>.
/// The component registers a tick only while a cutscene is open. The tick runs after input and before animation.
/// </summary>
[Description("Plays a cutscene timeline asset.")]
public class CutsceneDirectorComponent : XRComponent
{
    /// <summary>The order of the director tick in the normal tick group. It is after input and before animation.</summary>
    public const int DirectorTickOrder = 300000;

    private readonly WorldTick _tick;
    private readonly Action<CutsceneFiredEvent> _relayEvent;
    private readonly Action _relayFinished;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics = [];

    private CutsceneAsset? _asset;
    private CutscenePlayer? _player;
    private CutsceneBindingTable? _bindings;
    private CutsceneCameraRouter? _router;
    private CutsceneVrCameraRouter? _vrRouter;
    private CutsceneResidencySession? _residency;
    private XRViewport? _targetViewport;
    private TransformBase? _vrRigOrigin;
    private bool _tickRegistered;
    private bool _residencyEnabled = true;
    private bool _playbackWaitEnabled;
    private double _playbackWaitMaximumSeconds = CutsceneResidencyPlanner.DefaultPlaybackWaitMaximumSeconds;
    private readonly Dictionary<string, SceneNode> _runtimeSlots = [];

    /// <summary>Creates a director component.</summary>
    public CutsceneDirectorComponent()
    {
        // The delegate is created once, so the frame path never allocates.
        _tick = TickDirector;
        _relayEvent = RelayEvent;
        _relayFinished = RelayFinished;
    }

    /// <summary>
    /// Raised for each event that the open cutscene fires. See <see cref="CutscenePlayer.EventFired"/>.
    /// The subscription stays across a close and a later open.
    /// </summary>
    public event Action<CutsceneFiredEvent>? EventFired;

    /// <summary>
    /// Raised when the open cutscene reaches its end by playback or by a skip. See <see cref="CutscenePlayer.Finished"/>.
    /// Put completion logic here and not in an event at the end of a clip. The subscription stays across a close and a later open.
    /// </summary>
    public event Action? Finished;

    /// <summary>The cutscene to play. A change takes effect at the next open.</summary>
    public CutsceneAsset? Asset
    {
        get => _asset;
        set => SetField(ref _asset, value);
    }

    /// <summary>
    /// The desktop viewport that shows the cutscene camera. A change takes effect at the next open.
    /// If it is null, the director does not route the camera.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public XRViewport? TargetViewport
    {
        get => _targetViewport;
        set => SetField(ref _targetViewport, value);
    }

    /// <summary>
    /// The rig origin that <see cref="ECutsceneVrCameraMode.RigAnchor"/> moves: the transform whose child is the tracked headset.
    /// Use a <see cref="Transform"/> that no other system writes during the cutscene. A change takes effect at the next open.
    /// The close puts back the local position and rotation that it had at the open.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public TransformBase? VrRigOrigin
    {
        get => _vrRigOrigin;
        set => SetField(ref _vrRigOrigin, value);
    }

    /// <summary>
    /// The VR state and the headset fade that the VR camera modes use. If it is null, the director uses
    /// <see cref="RuntimeCutsceneVrHost.Instance"/>. A change takes effect at the next open.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public ICutsceneVrHost? VrHost { get; set; }

    /// <summary>The router of an open cutscene in <see cref="ECutsceneVrCameraMode.RigAnchor"/> mode during a VR session, or null.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public CutsceneVrCameraRouter? VrRouter => _vrRouter;

    /// <summary>
    /// Creates the binding resolver of a director that has no <see cref="BindingResolver"/>.
    /// The application composition registers it once at startup, because this project cannot reach an asset manager.
    /// If it is null or returns null, the open fails with a diagnostic.
    /// </summary>
    public static Func<CutsceneDirectorComponent, ICutsceneBindingResolver?>? DefaultBindingResolverFactory { get; set; }

    /// <summary>
    /// The object that finds and creates the scene nodes that the tracks act on.
    /// If it is null, an open uses <see cref="DefaultBindingResolverFactory"/>.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public ICutsceneBindingResolver? BindingResolver { get; set; }

    /// <summary>
    /// True to run progressive residency while a cutscene is open. The default is true.
    /// A change takes effect at the next open.
    /// </summary>
    public bool ResidencyEnabled
    {
        get => _residencyEnabled;
        set => SetField(ref _residencyEnabled, value);
    }

    /// <summary>
    /// True to hold forward playback at a camera cut until the next shot is resident, for at most
    /// <see cref="PlaybackWaitMaximumSeconds"/>. The clock and the audio hold. A jump and a scrub never hold.
    /// The default is false. A change applies to an open cutscene at once.
    /// </summary>
    public bool PlaybackWaitEnabled
    {
        get => _playbackWaitEnabled;
        set
        {
            if (SetField(ref _playbackWaitEnabled, value) && _residency is { } residency)
                residency.PlaybackWaitEnabled = value;
        }
    }

    /// <summary>The maximum time in seconds of one playback wait. It must be greater than zero. The default is 1 second.</summary>
    public double PlaybackWaitMaximumSeconds
    {
        get => _playbackWaitMaximumSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            if (SetField(ref _playbackWaitMaximumSeconds, value) && _residency is { } residency)
                residency.PlaybackWaitMaximumSeconds = value;
        }
    }

    /// <summary>
    /// Creates the texture residency backend of a director that has no <see cref="ResidencyBackendFactory"/>.
    /// The application composition registers it at startup, because this project cannot reach the asset manager.
    /// If residency is on and no factory gives a backend, the open adds a diagnostic and residency is off.
    /// </summary>
    public static Func<CutsceneDirectorComponent, IResidencyBackend?>? DefaultResidencyBackendFactory { get; set; }

    /// <summary>
    /// Creates the texture residency backend at each open. The session owns the backend and disposes it at the close.
    /// If it is null, an open uses <see cref="DefaultResidencyBackendFactory"/>.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public Func<IResidencyBackend?>? ResidencyBackendFactory { get; set; }

    /// <summary>
    /// The collector that finds the assets of the bound nodes for the approximate manifest.
    /// If it is null, the open uses <see cref="CutsceneRenderableAssetCollector"/>.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public ICutsceneResidencyAssetCollector? ResidencyAssetCollector { get; set; }

    /// <summary>The residency session of the open cutscene, or null when residency is off or no cutscene is open.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public CutsceneResidencySession? Residency => _residency;

    /// <summary>The active player, or null when no cutscene is open.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public CutscenePlayer? Player => _player;

    /// <summary>True when a cutscene is open.</summary>
    [Browsable(false)]
    [YamlIgnore]
    public bool IsOpen => _player is not null;

    /// <summary>
    /// The problems that the last open found: compile errors, binding errors, and open failures.
    /// Problems that appear later in playback are in <see cref="CutscenePlayer.Diagnostics"/>.
    /// </summary>
    [Browsable(false)]
    [YamlIgnore]
    public IReadOnlyList<CutsceneCompileDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Sets the node of a runtime slot. Call this before an open, because an animation track binds its clips
    /// at the open. On an open cutscene, this also changes the slot for the tracks that read the slot each frame.
    /// The value stays across a close and a later open, until a call with null clears it or the component is destroyed.
    /// </summary>
    /// <param name="slotName">The slot name.</param>
    /// <param name="node">The node to use, or null to clear the slot.</param>
    public void SetRuntimeSlot(string slotName, SceneNode? node)
    {
        ArgumentException.ThrowIfNullOrEmpty(slotName);
        if (node is null)
            _runtimeSlots.Remove(slotName);
        else
            _runtimeSlots[slotName] = node;
        _bindings?.SetRuntimeSlot(slotName, node);
    }

    /// <summary>
    /// Compiles the asset, resolves the bindings, creates the player, and registers the tick.
    /// If a cutscene is already open, this method closes it first.
    /// An asset that cannot play does not throw. The method reports the reason in <see cref="Diagnostics"/>
    /// and leaves the component closed.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the open. A canceled open leaves the component closed.</param>
    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Open(cancellationToken);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            return Task.FromCanceled(cancellationToken);
        }
    }

    /// <summary>
    /// Runs one director frame now: the player tick and the residency update. The registered tick calls it with the
    /// dilated frame time. A tool that seeks the player and needs the result at once can call it with zero.
    /// A call on a closed component does nothing.
    /// </summary>
    /// <param name="deltaSeconds">The time since the last frame, in seconds.</param>
    public void AdvanceFrame(double deltaSeconds)
    {
        if (_player is not { } player)
            return;

        // A handler in the tick can close the director, so the session is read before the tick.
        CutsceneResidencySession? residency = _residency;
        residency?.BeginTick();
        player.Tick(deltaSeconds);
        residency?.CompleteTick(player, deltaSeconds);

        // A handler in the tick can close the director, so the router is read after the tick.
        _vrRouter?.Update();
    }

    /// <summary>
    /// Closes the residency session, restores the state of the bound nodes, destroys the instances that the cutscene
    /// spawned, and unregisters the tick. A call on a closed component does nothing.
    /// </summary>
    public void Close()
    {
        if (_tickRegistered)
        {
            _tickRegistered = false;
            UnregisterTick(ETickGroup.Normal, DirectorTickOrder, _tick);
        }

        CutscenePlayer? player = _player;
        CutsceneBindingTable? bindings = _bindings;
        CutsceneResidencySession? residency = _residency;
        _player = null;
        _bindings = null;
        _residency = null;
        if (player is not null)
        {
            player.EventFired -= _relayEvent;
            player.Finished -= _relayFinished;
        }

        // The rig goes back before the stop, so the discontinuity of the stop does not start a fade.
        CutsceneVrCameraRouter? vrRouter = _vrRouter;
        _vrRouter = null;
        vrRouter?.Detach();

        // The planner closes before the restore, so that no completion arrives for a closed cutscene.
        // A close failure must not leave the bound nodes with cutscene values.
        try
        {
            residency?.Close();
        }
        finally
        {
            // Stop restores the original values with the restore setting of the asset.
            player?.Stop();
            _router?.Detach();
            _router = null;
            bindings?.Dispose();
        }
    }

    /// <inheritdoc/>
    protected override void OnComponentDeactivated()
    {
        Close();
        base.OnComponentDeactivated();
    }

    /// <inheritdoc/>
    protected override void OnEndPlay()
    {
        // The world or the scene ends play, so the residency planner and the budget profile must not stay.
        Close();
        base.OnEndPlay();
    }

    /// <inheritdoc/>
    protected override void OnDestroying()
    {
        Close();
        _runtimeSlots.Clear();
        base.OnDestroying();
    }

    private void Open(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Close();
        _diagnostics.Clear();

        CutsceneAsset? asset = _asset;
        if (asset is null)
        {
            Fail("The director has no cutscene asset.");
            return;
        }

        ICutsceneBindingResolver? resolver = BindingResolver ?? DefaultBindingResolverFactory?.Invoke(this);
        if (resolver is null)
        {
            Fail("The director has no binding resolver, and the application registered no default binding resolver factory.");
            return;
        }

        // A VR camera mode that cannot run fails the open before any change. The director never uses another mode.
        ICutsceneVrHost vrHost = VrHost ?? RuntimeCutsceneVrHost.Instance;
        bool rigAnchor = vrHost.IsInVR && asset.VrCameraMode == ECutsceneVrCameraMode.RigAnchor;
        if (CutsceneVrCameraRouter.Validate(asset.VrCameraMode, vrHost, _targetViewport, _vrRigOrigin) is { } vrFailure)
        {
            Fail(vrFailure);
            return;
        }

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, _diagnostics);
        CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, resolver, _runtimeSlots);
        if (cancellationToken.IsCancellationRequested)
        {
            bindings.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        _diagnostics.AddRange(bindings.Diagnostics);
        _bindings = bindings;
        _player = new CutscenePlayer(compiled, bindings, new CutsceneStateSnapshot(bindings));
        _player.EventFired += _relayEvent;
        _player.Finished += _relayFinished;

        if (_targetViewport is { } viewport)
        {
            _router = new CutsceneCameraRouter(_player);
            _router.Attach(viewport);
        }

        if (CutsceneVrCameraRouter.TryGetDesktopVisibilityNote(vrHost, _targetViewport, out string? desktopNote))
            _diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1, desktopNote));

        // Validate accepted the rig origin, so it is a Transform here.
        if (rigAnchor && _vrRigOrigin is Transform rigOrigin)
        {
            _vrRouter = new CutsceneVrCameraRouter(_player, vrHost, rigOrigin, asset.RigCutFadeSeconds, _diagnostics);
            _vrRouter.Attach();
        }

        OpenResidency(compiled, bindings, _player);

        _tickRegistered = true;
        RegisterTick(ETickGroup.Normal, DirectorTickOrder, _tick);
    }

    private void OpenResidency(CompiledCutscene compiled, CutsceneBindingTable bindings, CutscenePlayer player)
    {
        if (!_residencyEnabled)
            return;

        // A host without residency, for example a headless host, registers that state. An open there is not a problem.
        if (ResidencyBackendFactory is null && DefaultResidencyBackendFactory is null && CutsceneResidencyRegistration.IsUnavailableOnHost)
            return;

        // A residency failure is a diagnostic. The cutscene still opens and plays without residency.
        Func<IResidencyBackend?> createBackend = ResidencyBackendFactory ?? (() => DefaultResidencyBackendFactory?.Invoke(this));
        _residency = CutsceneResidencySession.TryOpen(
            compiled,
            bindings,
            createBackend,
            ResidencyAssetCollector ?? CutsceneRenderableAssetCollector.Instance,
            player,
            _playbackWaitEnabled,
            _playbackWaitMaximumSeconds,
            _diagnostics);
    }

    private void Fail(string message)
        => _diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1, message));

    private void RelayEvent(CutsceneFiredEvent fired)
        => EventFired?.Invoke(fired);

    private void RelayFinished()
        => Finished?.Invoke();

    private void TickDirector()
        => AdvanceFrame(RuntimeAnimationHostServices.Current.DilatedUpdateDeltaSeconds);
}
