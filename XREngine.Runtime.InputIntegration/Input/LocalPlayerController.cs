using XREngine.Components;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Runtime.InputIntegration
{
    //TODO: handle sending controller input packets to the server
    public class LocalPlayerController : PlayerController<LocalInputInterface>
    {
        /// <inheritdoc />
        public override bool IsLocal => true;

        private ELocalPlayerIndex _index = ELocalPlayerIndex.One;
        public ELocalPlayerIndex LocalPlayerIndex
        {
            get => _index;
            internal set => SetField(ref _index, value);
        }

        private readonly WindowSnapshotKeyboard _snapshotKeyboard = new(0);
        private readonly WindowSnapshotMouse _snapshotMouse = new(0);

        private IRuntimeLocalPlayerViewport? _viewport = null;
        [YamlIgnore]
        public IRuntimeLocalPlayerViewport? Viewport
        {
            get => _viewport;
            internal set => SetField(ref _viewport, value);
        }

        private IRuntimeFocusedInteractable? _focusedUIComponent = null;
        /// <summary>
        /// The UI component that currently has focus by this local player.
        /// Use for allowing or denying inputs to other components.
        /// </summary>
        public IRuntimeFocusedInteractable? FocusedUIComponent
        {
            get => _focusedUIComponent;
            internal set => SetField(ref _focusedUIComponent, value);
        }

        private bool _renderUIThroughPipeline = false;
        /// <summary>
        /// When true, screen-space UI is rendered as part of the camera render pipeline DAG
        /// (composited inline with the scene passes). When false, screen-space UI is rendered
        /// as a separate overlay on top of the viewport after the pipeline finishes.
        /// </summary>
        public bool RenderUIThroughPipeline
        {
            get => _renderUIThroughPipeline;
            set => SetField(ref _renderUIThroughPipeline, value);
        }

        public LocalPlayerController(ELocalPlayerIndex index) : base(new LocalInputInterface((int)index))
        {
            _index = index;
            RuntimeVrInputServices.ActionsChanged += OnActionsChanged;
        }
        public LocalPlayerController() : base(new LocalInputInterface(0))
        {
            RuntimeVrInputServices.ActionsChanged += OnActionsChanged;
        }

        // --- IPawnController virtual dispatch overrides ---
        protected override object? GetViewportCore() => _viewport;
        protected override void SetViewportCore(object? value) => Viewport = value as IRuntimeLocalPlayerViewport;
        protected override object? GetFocusedInteractableCore() => _focusedUIComponent;
        protected override void SetFocusedInteractableCore(object? value) => FocusedUIComponent = value as IRuntimeFocusedInteractable;
        protected override ELocalPlayerIndex? GetLocalPlayerIndexCore() => _index;

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
                    if (prev is IRuntimeLocalPlayerViewport previousViewport)
                        previousViewport.RequestMouseCapture(false);

                    UpdateViewportCamera();
                    break;
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
                System.Diagnostics.Debug.WriteLine($"[LocalPlayerController] UpdateViewportCamera: VP={_viewport.GetHashCode()} Pawn={_controlledPawn?.Name ?? "<null>"}");
                _viewport.RefreshControlledPawnCamera(_controlledPawn);
                RefreshViewportInputBinding();
            }
            else
            {
                // Viewport not yet assigned — this is normal during early startup
                // when possession happens before window/viewport creation. The camera
                // will be bound once the Viewport property is set.
                _snapshotMouse.SetCaptureRequest(null);
                Input.UpdateDevices(
                    keyboard: null,
                    mouse: null,
                    gamepad: null,
                    RuntimeVrInputServices.Actions);
            }
        }

        public void RefreshViewportInputBinding()
        {
            if (_viewport is null)
            {
                _snapshotMouse.SetCaptureRequest(null);
                Input.UpdateDevices(
                    keyboard: null,
                    mouse: null,
                    gamepad: null,
                    RuntimeVrInputServices.Actions);
                return;
            }

            ConsumeLatestInputSnapshot();
            bool keyboardAndMousePlayer = _index == ELocalPlayerIndex.One;
            _snapshotMouse.SetCaptureRequest(captured => _viewport?.RequestMouseCapture(captured));

            if (keyboardAndMousePlayer)
                _viewport.RequestMouseCapture(_snapshotMouse.HideCursor);
            else
                _viewport.RequestMouseCapture(false);

            Input.UpdateDevices(
                keyboardAndMousePlayer ? _snapshotKeyboard : null,
                keyboardAndMousePlayer ? _snapshotMouse : null,
                gamepad: null,
                RuntimeVrInputServices.Actions);
        }

        private void ConsumeLatestInputSnapshot()
        {
            if (_viewport is null || _index != ELocalPlayerIndex.One)
                return;

            WindowInputSnapshot snapshot = _viewport.ConsumeInputSnapshot();
            _snapshotKeyboard.ApplySnapshot(snapshot);
            _snapshotMouse.ApplySnapshot(snapshot);
        }

        /// <summary>
        /// Forces viewport/camera/input rebinding.
        /// Useful after snapshot restore when runtime-only wiring must be rebuilt.
        /// </summary>
        public void RefreshViewportCamera()
        {
            System.Diagnostics.Debug.WriteLine($"[LocalPlayerController] RefreshViewportCamera called. VP={(Viewport is null ? "NULL" : Viewport.GetHashCode().ToString())} Pawn={ControlledPawn?.Name ?? "<null>"}");
            UpdateViewportCamera();
        }

        /// <inheritdoc />
        public override void TickPawnInput(float delta, bool isUIInputCaptured)
        {
            if (Input is not LocalInputInterface localInput)
                return;

            ConsumeLatestInputSnapshot();

            if (isUIInputCaptured)
            {
                localInput.ClearMouseScrollBuffer();
                return;
            }

            localInput.TickStates(delta);
        }

        /// <inheritdoc />
        public override void OnPawnCameraChanged()
            => RefreshViewportCamera();

        protected override void RegisterInput(InputInterface input)
        {
            //input.RegisterButtonEvent(EKey.Escape, ButtonInputType.Pressed, OnTogglePause);
            //input.RegisterButtonEvent(GamePadButton.SpecialRight, ButtonInputType.Pressed, OnTogglePause);
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            RuntimeVrInputServices.ActionsChanged -= OnActionsChanged;
            _snapshotMouse.SetCaptureRequest(null);
            Viewport = null;
        }
    }
}
