using System.ComponentModel;
using System.Numerics;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Input;
using XREngine.Networking;

namespace XREngine.Components;

/// <summary>
/// Host-independent controllable scene component. Concrete device, viewport, camera, and UI
/// behavior is supplied through <see cref="RuntimePawnHostServices"/>.
/// </summary>
public class PawnComponent : XRComponent, IRuntimeInputControllablePawn, IRuntimeGameModePawn
{
    protected virtual int InputDispatchTickOrder => (int)ETickOrder.Input;
    protected virtual int InputConsumptionTickOrder => InputDispatchTickOrder + 1;

    public XREvent<PawnComponent>? PrePossessed;
    public XREvent<PawnComponent>? PostPossessed;
    public XREvent<PawnComponent>? PreUnpossessed;
    public XREvent<PawnComponent>? PostUnpossessed;

    /// <inheritdoc />
    public event Action<XRComponent>? RuntimePreUnpossessed;

    private EventList<XRComponent> _optionalInputSets = [];
    public EventList<XRComponent> OptionalInputSets
    {
        get => _optionalInputSets;
        set => SetField(ref _optionalInputSets, value);
    }

    private IPawnController? _controller;
    public IPawnController? Controller
    {
        get => _controller;
        set => SetField(ref _controller, value);
    }

    [Browsable(false)] public object? LocalInput => RuntimePawnHostServices.Current?.GetLocalInput(this);
    [Browsable(false)] public object? Gamepad => RuntimePawnHostServices.Current?.GetGamepad(this);
    [Browsable(false)] public object? Keyboard => RuntimePawnHostServices.Current?.GetKeyboard(this);
    [Browsable(false)] public object? Mouse => RuntimePawnHostServices.Current?.GetMouse(this);
    [Browsable(false)] public Vector2 CursorPositionScreen => RuntimePawnHostServices.Current?.GetCursorPositionScreen(this) ?? Vector2.Zero;
    [Browsable(false)] public Vector2 CursorPositionViewport => RuntimePawnHostServices.Current?.GetCursorPositionViewport(this) ?? Vector2.Zero;
    [Browsable(false)] public Vector2 CursorPositionInternalCoordinates => RuntimePawnHostServices.Current?.GetCursorPositionInternalCoordinates(this) ?? Vector2.Zero;
    [Browsable(false)] public Segment CursorPositionWorld => RuntimePawnHostServices.Current?.GetCursorPositionWorld(this) ?? new Segment(Vector3.Zero, Vector3.Zero);
    [Browsable(false)] public object? Viewport => Controller?.Viewport;

    private XRComponent? _camera;
    [Description("Dictates the component controlling the view of this pawn's controller.")]
    public XRComponent? CameraComponent
    {
        get => _camera;
        set => SetField(ref _camera, value);
    }

    object? IRuntimeInputControllablePawn.RuntimeCameraComponent => ResolveRuntimeCamera();

    private XRComponent? _userInterfaceInput;
    [Description("The UI input component used to route input to user interface canvases.")]
    public XRComponent? UserInterfaceInput
    {
        get => _userInterfaceInput;
        set => SetField(ref _userInterfaceInput, value);
    }

    public EventList<XRComponent> LinkedUICanvasInputs { get; } = [];

    protected virtual void PostPossess() => PostPossessed?.Invoke(this);
    protected virtual void PrePossess() => PrePossessed?.Invoke(this);
    protected virtual void PostUnpossess() => PostUnpossessed?.Invoke(this);
    protected virtual void PreUnpossess()
    {
        RuntimePreUnpossessed?.Invoke(this);
        PreUnpossessed?.Invoke(this);
    }

    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool change = base.OnPropertyChanging(propName, field, @new);
        if (!change || propName != nameof(Controller))
            return change;

        PreUnpossess();
        if (Controller is { IsLocal: true })
            UnregisterTick(ETickGroup.Normal, InputDispatchTickOrder, TickInput);
        PostUnpossess();
        return true;
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        if (propName == nameof(CameraComponent))
        {
            Controller?.OnPawnCameraChanged();
            return;
        }

        if (propName != nameof(Controller))
            return;

        PrePossess();
        if (Controller is { IsLocal: true })
        {
            Debug.Out($"[PawnComponent] Possessed by local controller, registering tick. InputDevice={Controller.InputDevice?.GetType().Name}");
            RegisterTick(ETickGroup.Normal, InputDispatchTickOrder, TickInput);
        }
        PostPossess();
    }

    private void TickInput()
    {
        IRuntimeInputServices inputServices = RuntimeInputServices.Current;
        Controller?.TickPawnInput(inputServices.UpdateDeltaSeconds, inputServices.IsUIInputCaptured);
    }

    /// <summary>Registers pawn-specific bindings using a host-defined input interface.</summary>
    public virtual void RegisterInput(object inputInterface) { }

    void IRuntimeInputControllablePawn.RegisterControllerInput(object inputInterface)
        => RuntimePawnHostServices.Current?.RegisterControllerInput(this, inputInterface);

    public void RegisterOptionalInputs(object inputInterface)
        => RuntimePawnHostServices.Current?.RegisterOptionalInputs(this, inputInterface);

    public void EnqueuePossessionByLocalPlayer(ELocalPlayerIndex player)
        => RuntimePlayerControllerServices.Current?.GetOrCreateLocalPlayer(player).EnqueuePossession(this);

    public void PossessByLocalPlayer(ELocalPlayerIndex player)
    {
        IPawnController? controller = RuntimePlayerControllerServices.Current?.GetOrCreateLocalPlayer(player);
        if (controller is not null)
            controller.ControlledPawnComponent = this;
    }

    internal XRComponent? ResolveRuntimeCamera()
        => RuntimePawnHostServices.Current?.ResolveCamera(this, CameraComponent) ?? CameraComponent;

    public virtual IPawnInputSnapshot? CaptureNetworkInputState() => null;
}
