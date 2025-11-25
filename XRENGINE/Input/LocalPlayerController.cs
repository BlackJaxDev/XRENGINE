using XREngine.Components;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Input
{
    //TODO: handle sending controller input packets to the server
    public class LocalPlayerController : PlayerController<LocalInputInterface>
    {
        private ELocalPlayerIndex _index = ELocalPlayerIndex.One;
        public ELocalPlayerIndex LocalPlayerIndex
        {
            get => _index;
            internal set => SetField(ref _index, value);
        }

        private XRViewport? _viewport = null;
        public XRViewport? Viewport
        {
            get => _viewport;
            internal set => SetField(ref _viewport, value);
        }

        private UIInteractableComponent? _focusedUIComponent = null;
        /// <summary>
        /// The UI component that currently has focus by this local player.
        /// Use for allowing or denying inputs to other components.
        /// </summary>
        public UIInteractableComponent? FocusedUIComponent
        {
            get => _focusedUIComponent;
            internal set => SetField(ref _focusedUIComponent, value);
        }

        public LocalPlayerController(ELocalPlayerIndex index) : base(new LocalInputInterface((int)index))
        {
            _index = index;
            Engine.VRState.ActionsChanged += OnActionsChanged;
        }
        public LocalPlayerController() : base(new LocalInputInterface(0))
        {
            Engine.VRState.ActionsChanged += OnActionsChanged;
        }

        private void OnActionsChanged(Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>> dictionary)
            => UpdateViewportCamera();

        protected override bool OnPropertyChanging<T2>(string? propName, T2 field, T2 @new)
        {
            return base.OnPropertyChanging(propName, field, @new);
        }
        protected override void OnPropertyChanged<T2>(string? propName, T2 prev, T2 field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Viewport):
                case nameof(ControlledPawn):
                case nameof(Input):
                    UpdateViewportCamera();
                    break;
                case nameof(LocalPlayerIndex):
                    Input.LocalPlayerIndex = (int)_index;
                    break;
            }
        }

        /// <summary>
        /// Updates the viewport with the HUD and/or camera from the controlled pawn.
        /// Called when the viewport, controlled pawn, input interface, or VR action set changes.
        /// </summary>
        private void UpdateViewportCamera()
        {
            if (_viewport is not null)
            {
                _viewport.CameraComponent = _controlledPawn?.GetCamera();
                Input.UpdateDevices(_viewport.Window?.Input, Engine.VRState.Actions);
            }
            else
                Input.UpdateDevices(null, Engine.VRState.Actions);
        }

        protected override void RegisterInput(InputInterface input)
        {
            //input.RegisterButtonEvent(EKey.Escape, ButtonInputType.Pressed, OnTogglePause);
            //input.RegisterButtonEvent(GamePadButton.SpecialRight, ButtonInputType.Pressed, OnTogglePause);
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            Viewport = null;
        }

        protected override void RegisterInputEvents(PawnComponent c)
        {
            base.RegisterInputEvents(c);
        }
        protected override void UnregisterInputEvents(PawnComponent c)
        {
            base.UnregisterInputEvents(c);
        }
    }
}
