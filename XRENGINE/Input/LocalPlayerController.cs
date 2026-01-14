using XREngine.Components;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using YamlDotNet.Serialization;

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
        [YamlIgnore]
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
                var pawn = _controlledPawn;
                var pawnCamera = pawn?.GetCamera();

                Debug.Out($"[LocalPlayerController] UpdateViewportCamera: VP={_viewport.GetHashCode()} Pawn={pawn?.Name ?? "<null>"} PawnCamera={(pawnCamera is null ? "NULL" : pawnCamera.Name ?? pawnCamera.GetHashCode().ToString())}");

                _viewport.CameraComponent = pawnCamera;
                
                // Extended diagnostic: capture camera's XRCamera object and its transform details
                var xrCam = _viewport.CameraComponent?.Camera;
                var camTfm = xrCam?.Transform;
                Debug.Out($"[LocalPlayerController] After setting CameraComponent: VP.CameraComponent={((_viewport.CameraComponent is null) ? "NULL" : _viewport.CameraComponent.Name ?? _viewport.CameraComponent.GetHashCode().ToString())} VP.ActiveCamera={((_viewport.ActiveCamera is null) ? "NULL" : _viewport.ActiveCamera.GetHashCode().ToString())} XRCam={xrCam?.GetHashCode().ToString() ?? "NULL"} CamTfm={camTfm?.GetHashCode().ToString() ?? "NULL"} CamWorldPos={camTfm?.WorldTranslation.ToString() ?? "NULL"} CamRenderPos={camTfm?.RenderTranslation.ToString() ?? "NULL"}");
                
                Input.UpdateDevices(_viewport.Window?.Input, Engine.VRState.Actions);
            }
            else
            {
                Debug.Out($"[LocalPlayerController] UpdateViewportCamera: viewport is NULL, cannot bind camera. Pawn={_controlledPawn?.Name ?? "<null>"}");
                Input.UpdateDevices(null, Engine.VRState.Actions);
            }
        }

        /// <summary>
        /// Forces viewport/camera/input rebinding.
        /// Useful after snapshot restore when runtime-only wiring must be rebuilt.
        /// </summary>
        public void RefreshViewportCamera()
        {
            Debug.Out($"[LocalPlayerController] RefreshViewportCamera called. VP={(Viewport is null ? "NULL" : Viewport.GetHashCode().ToString())} Pawn={ControlledPawn?.Name ?? "<null>"}");
            UpdateViewportCamera();
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
