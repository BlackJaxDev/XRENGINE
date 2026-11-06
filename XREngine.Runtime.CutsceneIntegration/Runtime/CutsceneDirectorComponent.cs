using System.ComponentModel;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Rendering;
using XREngine.Scene;
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
    private XRViewport? _targetViewport;
    private bool _tickRegistered;
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
    /// Restores the state of the bound nodes, destroys the instances that the cutscene spawned,
    /// and unregisters the tick. A call on a closed component does nothing.
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
        _player = null;
        _bindings = null;
        if (player is not null)
        {
            player.EventFired -= _relayEvent;
            player.Finished -= _relayFinished;
        }

        // Stop restores the original values with the restore setting of the asset.
        player?.Stop();
        _router?.Detach();
        _router = null;
        bindings?.Dispose();
    }

    /// <inheritdoc/>
    protected override void OnComponentDeactivated()
    {
        Close();
        base.OnComponentDeactivated();
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

        _tickRegistered = true;
        RegisterTick(ETickGroup.Normal, DirectorTickOrder, _tick);
    }

    private void Fail(string message)
        => _diagnostics.Add(new CutsceneCompileDiagnostic(-1, -1, message));

    private void RelayEvent(CutsceneFiredEvent fired)
        => EventFired?.Invoke(fired);

    private void RelayFinished()
        => Finished?.Invoke();

    private void TickDirector()
        => _player?.Tick(RuntimeAnimationHostServices.Current.DilatedUpdateDeltaSeconds);
}
