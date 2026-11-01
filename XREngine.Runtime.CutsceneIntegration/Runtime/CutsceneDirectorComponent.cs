using System.ComponentModel;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Rendering;
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
    private readonly List<CutsceneCompileDiagnostic> _diagnostics = [];

    private CutsceneAsset? _asset;
    private CutscenePlayer? _player;
    private CutsceneBindingTable? _bindings;
    private CutsceneCameraRouter? _router;
    private XRViewport? _targetViewport;
    private bool _tickRegistered;

    /// <summary>Creates a director component.</summary>
    public CutsceneDirectorComponent()
    {
        // The delegate is created once, so the frame path never allocates.
        _tick = TickDirector;
    }

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
    /// The object that finds and creates the scene nodes that the tracks act on.
    /// The host sets it. A cutscene cannot open without it.
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

        ICutsceneBindingResolver? resolver = BindingResolver;
        if (resolver is null)
        {
            Fail("The director has no binding resolver.");
            return;
        }

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, _diagnostics);
        CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, resolver);
        if (cancellationToken.IsCancellationRequested)
        {
            bindings.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        _diagnostics.AddRange(bindings.Diagnostics);
        _bindings = bindings;
        _player = new CutscenePlayer(compiled, bindings, new CutsceneStateSnapshot(bindings));

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

    private void TickDirector()
        => _player?.Tick(RuntimeAnimationHostServices.Current.DilatedUpdateDeltaSeconds);
}
