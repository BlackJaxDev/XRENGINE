using System.Numerics;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.Components
{
    /// <summary>
    /// A pawn is an actor that can be controlled by either a local player, remote player, or AI.
    /// This serves as only way for a player to perceive and interact with the game world.
    /// </summary>
    public class PawnComponent : XRComponent
    {
        public XREvent<PawnComponent>? PrePossessed;
        public XREvent<PawnComponent>? PostPossessed;
        public XREvent<PawnComponent>? PreUnpossessed;
        public XREvent<PawnComponent>? PostUnpossessed;

        private EventList<OptionalInputSetComponent> _optionalInputSets = [];
        public EventList<OptionalInputSetComponent> OptionalInputSets
        {
            get => _optionalInputSets;
            set => SetField(ref _optionalInputSets, value);
        }

        private PawnController? _controller;
        /// <summary>
        /// The interface that is managing and providing input to this pawn.
        /// </summary>
        public PawnController? Controller
        {
            get => _controller;
            set => SetField(ref _controller, value);
        }

        public LocalInputInterface? LocalInput => (Controller as LocalPlayerController)?.Input;
        /// <summary>
        /// The local gamepad input device for this pawn.
        /// </summary>
        public BaseGamePad? Gamepad => LocalInput?.Gamepad;
        /// <summary>
        /// The local keyboard input device for this pawn.
        /// </summary>
        public BaseKeyboard? Keyboard => LocalInput?.Keyboard;
        /// <summary>
        /// The local mouse input device for this pawn.
        /// </summary>
        public BaseMouse? Mouse => LocalInput?.Mouse;
        /// <summary>
        /// The position of the cursor on the window in screen coordinates.
        /// </summary>
        public Vector2 CursorPositionScreen => Mouse?.CursorPosition ?? Vector2.Zero;
        /// <summary>
        /// The position of the cursor on the window in viewport-relative coordinates.
        /// </summary>
        public Vector2 CursorPositionViewport => Viewport?.ScreenToViewportCoordinate(CursorPositionScreen) ?? Vector2.Zero;
        /// <summary>
        /// The position of the cursor on the window in viewport-relative internal coordinates.
        /// </summary>
        public Vector2 CursorPositionInternalCoordinates => Viewport?.ViewportToInternalCoordinate(CursorPositionViewport) ?? Vector2.Zero;
        /// <summary>
        /// The position of the cursor in the world, represented as a ray from the camera's NearZ to FarZ.
        /// </summary>
        public Segment CursorPositionWorld => Viewport?.GetWorldSegment(CursorPositionViewport) ?? new Segment(Vector3.Zero, Vector3.Zero);

        protected virtual void PostPossess()
            => PostPossessed?.Invoke(this);
        protected virtual void PrePossess()
            => PrePossessed?.Invoke(this);
        protected virtual void PostUnpossess()
            => PostUnpossessed?.Invoke(this);
        protected virtual void PreUnpossess()
            => PreUnpossessed?.Invoke(this);

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Controller):
                        //Call before unregistering anything
                        PreUnpossess();
                        if (Controller?.ControlledPawn == this)
                            Controller.ControlledPawn = null;
                        if (Controller is LocalPlayerController localPlayerController)
                            UnregisterTick(ETickGroup.Normal, ETickOrder.Input, TickInput);
                        //Call after unregistering controller
                        PostUnpossess();
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Controller):
                    //Call before possessing the new pawn
                    PrePossess();
                    if (Controller is not null)
                        Controller.ControlledPawn = this;
                    if (Controller is LocalPlayerController localPlayerController)
                        RegisterTick(ETickGroup.Normal, ETickOrder.Input, TickInput);
                    //Call after possessing the new pawn
                    PostPossess();
                    break;
            }
        }

        private void TickInput()
        {
            if (Controller is not LocalPlayerController localPlayerController ||
                localPlayerController.Input is not LocalInputInterface localInput)
                return;
            
            localInput.TickStates(Engine.Delta);
        }

        /// <summary>
        /// Casts the controller to a server player controller.
        /// </summary>
        public RemotePlayerController? ServerPlayerController => Controller as RemotePlayerController;

        /// <summary>
        /// Casts the controller to a local player controller.
        /// </summary>
        public LocalPlayerController? LocalPlayerController => Controller as LocalPlayerController;

        /// <summary>
        /// Casts the controller to a generic player controller.
        /// </summary>
        public PlayerControllerBase? PlayerController => Controller as PlayerControllerBase;

        /// <summary>
        /// The viewport of the local player controller that is controlling this pawn.
        /// </summary>
        public XRViewport? Viewport => LocalPlayerController?.Viewport;

        private CameraComponent? _camera;
        /// <summary>
        /// Dictates the component controlling the view of this pawn's controller.
        /// </summary>
        public CameraComponent? CameraComponent
        {
            get => _camera;
            set => SetField(ref _camera, value);
        }

        private UICanvasInputComponent? _userInterfaceInput = null;
        public UICanvasInputComponent? UserInterfaceInput
        {
            get => _userInterfaceInput;
            set => SetField(ref _userInterfaceInput, value);
        }

        public EventList<UICanvasInputComponent> LinkedUICanvasInputs { get; } = [];

        public virtual void RegisterInput(InputInterface input) { }

        private IEnumerable<OptionalInputSetComponent>? _registeredOptionalSets = null;
        public void RegisterOptionalInputs(InputInterface input)
        {
            IEnumerable<OptionalInputSetComponent> allSets;
            if (input.Unregister)
            {
                if (_registeredOptionalSets is not null)
                {
                    allSets = _registeredOptionalSets;
                    foreach (var x in allSets)
                    {
                        x.PropertyChanged -= OptionalInputSetComponent_ActiveChanged;
                        if (x.IsActive)
                            x.RegisterInput(input);
                    }

                    SceneNode.Components.CollectionChanged -= UpdateOptionalInputs;
                    OptionalInputSets.CollectionChanged -= UpdateOptionalInputs;
                }
                else
                    return;
            }
            else
            {
                allSets = GetSiblingComponents<OptionalInputSetComponent>().Concat(OptionalInputSets).Distinct();
                foreach (var x in allSets)
                {
                    x.PropertyChanged += OptionalInputSetComponent_ActiveChanged;
                    if (x.IsActive)
                        x.RegisterInput(input);
                }
                
                _registeredOptionalSets = allSets;
                SceneNode.Components.CollectionChanged += UpdateOptionalInputs;
                OptionalInputSets.CollectionChanged += UpdateOptionalInputs;
            }
        }

        private void OptionalInputSetComponent_ActiveChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(IsActive))
                return;

            var input = LocalInput;
            if (input is null)
                return;

            if (sender is not OptionalInputSetComponent x)
                return;
            
            if (x.IsActive)
                x.RegisterInput(input);
            else
            {
                input.Unregister = true;
                x.RegisterInput(input);
                input.Unregister = false;
            }
        }

        private void UpdateOptionalInputs(object sender, TCollectionChangedEventArgs<OptionalInputSetComponent> e)
        {
            if (LocalInput is null)
                return;

            LocalInput.Unregister = true;
            RegisterOptionalInputs(LocalInput);

            LocalInput.Unregister = false;
            RegisterOptionalInputs(LocalInput);
        }

        private void UpdateOptionalInputs(object sender, TCollectionChangedEventArgs<XRComponent> e)
        {
            if (LocalInput is null)
                return;

            LocalInput.Unregister = true;
            RegisterOptionalInputs(LocalInput);

            LocalInput.Unregister = false;
            RegisterOptionalInputs(LocalInput);
        }

        /// <summary>
        /// Enqueues this pawn for possession by the local player with the given index.
        /// If the currently possessed pawn is null, possesses this pawn immediately.
        /// </summary>
        /// <param name="player"></param>
        public void EnqueuePossessionByLocalPlayer(ELocalPlayerIndex player)
            => Engine.State.GetOrCreateLocalPlayer(player).EnqueuePosession(this);
        /// <summary>
        /// Sets the controlled pawn of the local player with the given index to this pawn.
        /// </summary>
        /// <param name="player"></param>
        public void PossessByLocalPlayer(ELocalPlayerIndex player)
            => Engine.State.GetOrCreateLocalPlayer(player).ControlledPawn = this;

        public CameraComponent? GetCamera()
            => CameraComponent is not null ? CameraComponent : GetSiblingComponent<CameraComponent>();
    }
}
