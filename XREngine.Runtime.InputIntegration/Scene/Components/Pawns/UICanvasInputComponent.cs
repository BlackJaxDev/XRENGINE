using XREngine.Extensions;
using XREngine.Rendering.UI.State;
using System.Collections;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Input.Devices;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components
{
    /// <summary>
    /// Enables and manages input for a UI canvas component.
    /// </summary>
    [XRComponentEditor("XREngine.Editor.ComponentEditors.UICanvasInputComponentEditor")]
    [RequireComponents(typeof(UICanvasComponent))]
    [XRTypeRedirect("XREngine.Components.UICanvasInputComponent")]
    public partial class UICanvasInputComponent : XRComponent, IUICanvasInputSource
    {
        /// <summary>
        /// Returns the canvas component this input component is controlling.
        /// </summary>
        public UICanvasComponent? GetCameraCanvas() => Canvas ?? GetSiblingComponent<UICanvasComponent>(true);

        /// <summary>
        /// The canvas component this input component is controlling.
        /// If null, the component will attempt to find a sibling canvas component.
        /// </summary>
        private UICanvasComponent? _canvas;
        public UICanvasComponent? Canvas
        {
            get => _canvas;
            set => SetField(ref _canvas, value);
        }

        [RuntimeOnly, YamlIgnore]
        private readonly HashSet<InputInterface> _registeredInputs = new(ReferenceEqualityComparer.Instance);
        [RuntimeOnly, YamlIgnore]
        private UIInteractableComponent? _pressedTarget;
        [RuntimeOnly, YamlIgnore]
        private ulong _nextPointerPressEpoch;
        [RuntimeOnly, YamlIgnore]
        private ulong _pressedPointerPressEpoch;
        [RuntimeOnly, YamlIgnore]
        private UIPublication? _pressedPublication;
        [RuntimeOnly, YamlIgnore]
        private UIPointerGeometry _pressedPointerGeometry;
        [RuntimeOnly, YamlIgnore]
        private Matrix4x4 _pressedCanvasToLocal;
        [RuntimeOnly, YamlIgnore]
        private Vector2 _pressedLastCanvasPosition;
        [RuntimeOnly, YamlIgnore]
        private long _pressedInteractionRevision;
        [RuntimeOnly, YamlIgnore]
        private ulong _pressedCanvasLifetime;
        [RuntimeOnly, YamlIgnore]
        private UIElementIdentity _pressedElementIdentity;
        [RuntimeOnly, YamlIgnore]
        private UICanvasComponent? _pressedCanvas;
        [RuntimeOnly, YamlIgnore]
        private InputInterface? _pressedInput;
        [RuntimeOnly, YamlIgnore]
        private BaseMouse? _pressedMouse;
        [RuntimeOnly, YamlIgnore]
        private ulong _pressedMouseRegistrationRevision;
        [RuntimeOnly, YamlIgnore]
        private IRuntimePointerContactSource? _pressedPointerSource;
        [RuntimeOnly, YamlIgnore]
        private object? _pressedPointerOwner;
        [RuntimeOnly, YamlIgnore]
        private ulong _pressedPointerGeneration;
        [RuntimeOnly, YamlIgnore]
        private bool _pressedWasTouch;

        private UIInteractableComponent? _focusedComponent;
        private ulong _focusIntentRevision;
        private bool _inputDeactivating;
        private bool _inputTerminal;
        private bool _inputCleanupActive;
        private bool _inputActivationDeferred;
        /// <summary>
        /// The UI component focused on by the gamepad or last interacted with by the mouse.
        /// </summary>
        public UIInteractableComponent? FocusedComponent
        {
            get => _focusedComponent;
            set
            {
                AdvanceFocusIntent();
                SetField(ref _focusedComponent, _inputDeactivating ? null : value);
            }
        }

        /// <summary>Changes before each explicit focus request, including requests for the current target.</summary>
        [RuntimeOnly, YamlIgnore]
        public ulong FocusIntentRevision => _focusIntentRevision;

        /// <summary>Whether a virtual focus request is waiting for or accepting a presented row.</summary>
        [RuntimeOnly, YamlIgnore]
        public bool HasPendingFocusRequest => _pendingVirtualFocusItemId != 0 || _resolvingVirtualFocus;

        private void AdvanceFocusIntent()
        {
            ulong next = unchecked(_focusIntentRevision + 1);
            SetField(ref _focusIntentRevision, next == 0 ? 1UL : next, publishNotifications: false);
        }

        /// <summary>
        /// True when a Ctrl key (left or right) is currently held.
        /// </summary>
        public bool IsCtrlHeld { get; private set; }

        /// <summary>
        /// True when a Shift key (left or right) is currently held.
        /// </summary>
        public bool IsShiftHeld { get; private set; }

        /// <summary>
        /// True when an Alt key (left or right) is currently held.
        /// </summary>
        public bool IsAltHeld { get; private set; }

        /// <summary>
        /// Fired when the left mouse button is pressed.
        /// The interactable under the cursor is passed (may be null if clicking empty space).
        /// </summary>
        public event Action<UIInteractableComponent?>? LeftClickDown;

        /// <summary>
        /// Fired when the right mouse button is pressed while the cursor is over an interactable component.
        /// The interactable under the cursor is passed as the argument.
        /// </summary>
        public event Action<UIInteractableComponent>? RightClick;

        /// <summary>
        /// Fired when the Escape key is pressed.
        /// </summary>
        public event Action? EscapePressed;
        public event Action<UIInteractableComponent?>? BackPressed;

        private IRuntimeInputControllablePawn? _owningPawn;
        private IRuntimeInputControllablePawn? _observedPawn;
        private InputInterface? _linkedInput;
        /// <summary>
        /// The pawn that has this HUD linked for screen or camera space use.
        /// World space HUDs do not require a pawn - call SetCursorPosition to set the cursor position.
        /// </summary>
        public IRuntimeInputControllablePawn? OwningPawn
        {
            get => _owningPawn;
            set => SetField(ref _owningPawn, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(OwningPawn):
                        UnlinkOwningPawn();
                        break;
                    case nameof(FocusedComponent):
                        ClearPointerPress();
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
                case nameof(OwningPawn):
                    LinkOwningPawn();
                    break;
                case nameof(FocusedComponent):
                    CancelVirtualFocusAfterExternalFocusChange();
                    ReconcileFocusedComponent();
                    break;
            }
        }

        private void FocusedComponentPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, _linkedFocusedComponent))
                return;
            switch (e.PropertyName)
            {
                case nameof(UIInteractableComponent.IsFocused):
                    if (!(_focusedComponent?.IsFocused ?? false))
                        SetField(ref _focusedComponent, null, nameof(FocusedComponent));
                    break;
                case nameof(UIInteractableComponent.IsReadOnly):
                case nameof(UIInteractableComponent.IsEnabled):
                    UpdateImeFocusTarget();
                    break;
                case nameof(UIInteractableComponent.RegisterInputsOnFocus):
                    ReconcileFocusedComponent();
                    break;
            }
        }

        /// <summary>
        /// Unlinks input from the pawn connected to this input component.
        /// </summary>
        private void UnlinkOwningPawn()
        {
            if (_observedPawn is not { } pawn)
                return;

            _observedPawn = null;
            pawn.PropertyChanging -= OwningPawnPropertyChanging;
            pawn.PropertyChanged -= OwningPawnPropertyChanged;
            pawn.LinkedUiInputComponents.Remove(this);
            UnlinkInput();
        }

        /// <summary>
        /// Links input from the pawn connected to this input component.
        /// </summary>
        private void LinkOwningPawn()
        {
            if (_inputDeactivating || _inputTerminal || !IsActiveInHierarchy || _owningPawn is null ||
                ReferenceEquals(_observedPawn, _owningPawn))
                return;

            _observedPawn = _owningPawn;
            _owningPawn.PropertyChanging += OwningPawnPropertyChanging;
            _owningPawn.PropertyChanged += OwningPawnPropertyChanged;
            _owningPawn.LinkedUiInputComponents.Add(this);

            LinkInput();
        }

        private void OwningPawnPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            if (e.PropertyName != nameof(IRuntimeInputControllablePawn.Controller))
                return;
            
            UnlinkInput();
        }

        private void OwningPawnPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(IRuntimeInputControllablePawn.Controller))
                return;
            
            LinkInput();
        }

        private void LinkInput()
        {
            //Link input commands from the owning controller to this hud
            var input = GetOwningInput();
            if (input is null || ReferenceEquals(_linkedInput, input))
                return;

            UnlinkInput();
            _linkedInput = input;
            input.TryUnregisterInput();
            input.InputRegistration += RegisterInput;
            input.TryRegisterInput();
        }

        private void UnlinkInput()
        {
            //Unlink input commands from the owning controller to this hud
            var input = _linkedInput;
            if (input is null)
                return;

            _linkedInput = null;
            input.TryUnregisterInput();
            bool previous = input.Unregister;
            try
            {
                input.Unregister = true;
                RegisterInput(input);
            }
            finally { input.Unregister = previous; }
            input.InputRegistration -= RegisterInput;
            input.TryRegisterInput();
        }

        private InputInterface? GetOwningInput()
            => _owningPawn?.Controller?.InputDevice as InputInterface;

        public void RegisterInput(InputInterface input)
        {
            if ((_inputDeactivating || _inputTerminal) && !input.Unregister)
                return;
            // The pawn host and this canvas's owner subscription can both request the
            // same mappings. Retain both routes while installing each interface once.
            if (input.Unregister)
            {
                if (!_registeredInputs.Remove(input))
                    return;
                List<Exception>? failures = null;
                if (ReferenceEquals(_pressedInput, input))
                    TryInputCleanup(() => ClearPointerPress(), ref failures);
                TryInputCleanup(() => RegisterInputMappings(input), ref failures);
                TryInputCleanup(ReconcileFocusedComponent, ref failures);
                ThrowInputCleanupFailures(failures);
                return;
            }
            else if (!_registeredInputs.Add(input))
                return;

            RegisterInputMappings(input);
            ReconcileFocusedComponent();
        }

        private void RegisterInputMappings(InputInterface input)
        {
            if (input.Unregister)
            {
                UnregisterInputMappings(input);
                return;
            }
            input.RegisterMouseMove(MouseMove, EMouseMoveType.Absolute);
            input.RegisterMouseScroll(OnMouseScroll);
            input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Pressed, OnMouseInteractButtonDown);
            input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Released, OnMouseInteractButtonUp);
            input.RegisterMouseButtonEvent(EMouseButton.RightClick, EButtonInputType.Pressed, OnMouseRightButtonDown);

            input.RegisterKeyStateChange(EKey.ControlLeft, OnCtrlLeftStateChanged);
            input.RegisterKeyStateChange(EKey.ControlRight, OnCtrlRightStateChanged);
            input.RegisterKeyStateChange(EKey.ShiftLeft, OnShiftLeftStateChanged);
            input.RegisterKeyStateChange(EKey.ShiftRight, OnShiftRightStateChanged);
            input.RegisterKeyStateChange(EKey.AltLeft, OnAltLeftStateChanged);
            input.RegisterKeyStateChange(EKey.AltRight, OnAltRightStateChanged);
            input.RegisterKeyStateChange(EKey.Escape, OnEscapeStateChanged);
            input.RegisterKeyStateChange(EKey.Tab, OnTabStateChanged);

            input.RegisterButtonEvent(EGamePadButton.FaceDown, EButtonInputType.Pressed, OnGamepadInteractButtonDown);
            input.RegisterButtonEvent(EGamePadButton.FaceDown, EButtonInputType.Released, OnGamepadInteractButtonUp);

            input.RegisterButtonEvent(EGamePadButton.FaceRight, EButtonInputType.Pressed, OnGamepadBackButtonDown);
            input.RegisterButtonEvent(EGamePadButton.FaceRight, EButtonInputType.Released, OnGamepadBackButtonUp);

            input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickX, OnLeftStickX, false);
            input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickY, OnLeftStickY, false);

            input.RegisterButtonEvent(EGamePadButton.DPadUp, EButtonInputType.Pressed, OnDPadUp);
            input.RegisterButtonEvent(EGamePadButton.DPadDown, EButtonInputType.Pressed, OnDPadDown);
            input.RegisterButtonEvent(EGamePadButton.DPadLeft, EButtonInputType.Pressed, OnDPadLeft);
            input.RegisterButtonEvent(EGamePadButton.DPadRight, EButtonInputType.Pressed, OnDPadRight);
        }

        private void UnregisterInputMappings(InputInterface input)
        {
            List<Exception>? failures = null;
            TryInputCleanup(() => input.RegisterMouseMove(MouseMove, EMouseMoveType.Absolute), ref failures);
            TryInputCleanup(() => input.RegisterMouseScroll(OnMouseScroll), ref failures);
            TryInputCleanup(() => input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Pressed,
                OnMouseInteractButtonDown), ref failures);
            TryInputCleanup(() => input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Released,
                OnMouseInteractButtonUp), ref failures);
            TryInputCleanup(() => input.RegisterMouseButtonEvent(EMouseButton.RightClick, EButtonInputType.Pressed,
                OnMouseRightButtonDown), ref failures);
            TryInputCleanup(() => input.RegisterKeyStateChange(EKey.ControlLeft, OnCtrlLeftStateChanged), ref failures);
            TryInputCleanup(() => input.RegisterKeyStateChange(EKey.ControlRight, OnCtrlRightStateChanged), ref failures);
            TryInputCleanup(() => input.RegisterKeyStateChange(EKey.ShiftLeft, OnShiftLeftStateChanged), ref failures);
            TryInputCleanup(() => input.RegisterKeyStateChange(EKey.ShiftRight, OnShiftRightStateChanged), ref failures);
            TryInputCleanup(() => input.RegisterKeyStateChange(EKey.AltLeft, OnAltLeftStateChanged), ref failures);
            TryInputCleanup(() => input.RegisterKeyStateChange(EKey.AltRight, OnAltRightStateChanged), ref failures);
            TryInputCleanup(() => input.RegisterKeyStateChange(EKey.Escape, OnEscapeStateChanged), ref failures);
            TryInputCleanup(() => input.RegisterKeyStateChange(EKey.Tab, OnTabStateChanged), ref failures);
            TryInputCleanup(() => input.RegisterButtonEvent(EGamePadButton.FaceDown, EButtonInputType.Pressed,
                OnGamepadInteractButtonDown), ref failures);
            TryInputCleanup(() => input.RegisterButtonEvent(EGamePadButton.FaceDown, EButtonInputType.Released,
                OnGamepadInteractButtonUp), ref failures);
            TryInputCleanup(() => input.RegisterButtonEvent(EGamePadButton.FaceRight, EButtonInputType.Pressed,
                OnGamepadBackButtonDown), ref failures);
            TryInputCleanup(() => input.RegisterButtonEvent(EGamePadButton.FaceRight, EButtonInputType.Released,
                OnGamepadBackButtonUp), ref failures);
            TryInputCleanup(() => input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickX, OnLeftStickX, false),
                ref failures);
            TryInputCleanup(() => input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickY, OnLeftStickY, false),
                ref failures);
            TryInputCleanup(() => input.RegisterButtonEvent(EGamePadButton.DPadUp, EButtonInputType.Pressed,
                OnDPadUp), ref failures);
            TryInputCleanup(() => input.RegisterButtonEvent(EGamePadButton.DPadDown, EButtonInputType.Pressed,
                OnDPadDown), ref failures);
            TryInputCleanup(() => input.RegisterButtonEvent(EGamePadButton.DPadLeft, EButtonInputType.Pressed,
                OnDPadLeft), ref failures);
            TryInputCleanup(() => input.RegisterButtonEvent(EGamePadButton.DPadRight, EButtonInputType.Pressed,
                OnDPadRight), ref failures);
            ThrowInputCleanupFailures(failures);
        }

        private void OnMouseScroll(float diff)
        {
            if (_inputDeactivating || _inputTerminal || !float.IsFinite(diff) ||
                MathF.Abs(diff) <= float.Epsilon)
                return;

            if (RefreshPresentedInput() is not { } publication)
                return;
            try
            {
            ulong revision = _inputPublicationRevision;
            UICanvasComponent? canvas = _inputCanvas;
            object? output = _inputOutput;
            if (TopMostInteractable is { } target && !IsPointerTargetEligible(target))
                return;
            if (!IsCurrentScrollRead(publication, revision, canvas, output))
                return;

            UIComponent? hitOwner = TopMostElement ?? TopMostInteractable;
            if (hitOwner is null)
                return;
            UIHitRecord hit = default;
            bool found = false;
            for (int index = 0; index < publication.HitRecordCount; index++)
            {
                UIHitRecord candidate = publication.GetHitRecord(index);
                if (ReferenceEquals(candidate.RenderInfo.Owner, hitOwner) && IsCurrentHitRecord(candidate))
                {
                    hit = candidate;
                    found = true;
                    break;
                }
            }
            if (!found)
                return;

            // Route through the displayed ancestry even when the live tree has moved.
            int previousDepth = int.MaxValue;
            int previousComponent = -1;
            for (int attempt = 0; attempt < publication.ScrollReceivers.Length; attempt++)
            {
                int selected = -1;
                for (int index = 0; index < publication.ScrollReceivers.Length; index++)
                {
                    ref readonly UIScrollRecord route = ref publication.ScrollReceivers[index];
                    if (!route.IsAncestorOf(hit.NodeIndex) || route.Depth > previousDepth ||
                        route.Depth == previousDepth && route.ComponentIndex <= previousComponent)
                        continue;
                    if (selected < 0 || route.Depth > publication.ScrollReceivers[selected].Depth ||
                        route.Depth == publication.ScrollReceivers[selected].Depth &&
                        route.ComponentIndex < publication.ScrollReceivers[selected].ComponentIndex)
                        selected = index;
                }
                if (selected < 0)
                    return;
                UIScrollRecord receiver = publication.ScrollReceivers[selected];
                previousDepth = receiver.Depth;
                previousComponent = receiver.ComponentIndex;
                if (!receiver.Owner.IsActiveInHierarchy || receiver.Owner.Transform is not UITransform transform ||
                    transform.ElementIdentity != receiver.Identity ||
                    !ReferenceEquals(transform.GetCanvasComponent(), GetCameraCanvas()))
                    continue;
                if (!IsCurrentScrollRead(publication, revision, canvas, output) ||
                    DispatchScroll(receiver, publication, diff) ||
                    !IsCurrentScrollRead(publication, revision, canvas, output))
                    return;
            }
            }
            finally { publication.Release(); }
        }

        /// <summary>Stops routing when a callback changes the displayed packet without refreshing input.</summary>
        private bool IsCurrentScrollRead(UIPublication publication, ulong revision,
            UICanvasComponent? canvas, object? output)
        {
            if (_inputPublicationRevision != revision || canvas is null || output is null ||
                !IsCurrentInputRead(publication, canvas, output) ||
                !canvas.TryAcquirePresentedPublication(output, out UIPublication? displayed))
                return false;
            bool same;
            try { same = ReferenceEquals(displayed, publication); }
            finally { displayed!.Release(); }
            return same && _inputPublicationRevision == revision &&
                IsCurrentInputRead(publication, canvas, output);
        }

        /// <summary>
        /// Gives a pointer scroll receiver the cursor position in its displayed coordinates.
        /// A receiver without a current hit record in the packet gets the plain delta.
        /// </summary>
        private bool DispatchScroll(in UIScrollRecord route, UIPublication publication, float diff)
        {
            if (route.Receiver is not IUIPointerScrollReceiver pointerReceiver ||
                route.Owner is not UIInteractableComponent target ||
                !_inputRecords.TryGetValue(target.RenderInfo2D.MembershipId, out UIHitRecord record) ||
                !IsCurrentHitRecord(record) ||
                !publication.TryMapPointer(_cursorOutputPosition, out Vector2 canvasPosition,
                    requireInsideOutput: true))
                return route.Receiver.HandleMouseScroll(diff);
            UIPointerGeometry geometry = record.PointerGeometry;
            return pointerReceiver.HandlePointerScroll(diff,
                Vector2.Transform(canvasPosition, record.CanvasToLocal), in geometry);
        }

        /// <summary>
        /// The location of the mouse cursor in world space.
        /// </summary>
        public Vector2 CursorPositionWorld2D { get; private set; }
        /// <summary>
        /// The location of the mouse cursor in world space on the last update.
        /// </summary>
        public Vector2 LastCursorPositionWorld2D { get; private set; }
        
        /// <summary>
        /// 
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        private Vector2 _cursorOutputPosition;
        private UIPublication? _inputPublication;
        private UICanvasComponent? _inputCanvas;
        private object? _inputOutput;
        private ulong _queriedCanvasLifetime;
        private ulong _queriedHitGeneration;
        private Vector2 _queriedCursorPosition;
        private UIInteractableComponent? _queriedCapture;
        private long _queriedCaptureRevision;
        private ulong _inputPublicationRevision;
        private ulong _collectedInputRevision;
        private bool _intersectionSwapActive;
        private bool _intersectionCollectActive;
        private bool _intersectionDispatchActive;
        private readonly Dictionary<long, UIHitRecord> _inputRecords = [];

        private void MouseMove(float x, float y)
        {
            if (!_inputDeactivating && !_inputTerminal)
                _cursorOutputPosition = new Vector2(x, y);
        }

        private void CollectVisible()
        {
            if (_inputDeactivating || _inputTerminal || _intersectionDispatchActive || _intersectionCollectActive)
                return;
            _intersectionCollectActive = true;
            try { CollectPresentedIntersections(); }
            finally { _intersectionCollectActive = false; }
        }

        private void CollectPresentedIntersections()
        {
            UIElementIntersections.Clear();
            UIPublication? publication = RefreshPresentedInput();
            _collectedInputRevision = _inputPublicationRevision;
            if (publication is null)
                return;
            try
            {

            if (!ReferenceEquals(publication, _inputPublication))
            {
                _collectedInputRevision = ulong.MaxValue;
                return;
            }
            if (!publication.TryMapPointer(_cursorOutputPosition, out Vector2 canvasPosition, _pressedTarget is null))
            {
                // The next swap clears the prior intersections. A later return to
                // the same point must query the packet again instead of reusing them.
                _queriedCanvasLifetime = 0;
                _queriedHitGeneration = 0;
                return;
            }
            CursorPositionWorld2D = canvasPosition;
            if (_queriedCanvasLifetime == publication.CanvasLifetime &&
                _queriedHitGeneration == publication.HitGeneration &&
                _queriedCursorPosition == canvasPosition && ReferenceEquals(_queriedCapture, _pressedTarget) &&
                _queriedCaptureRevision == _pressedInteractionRevision)
            {
                foreach (RenderInfo2D previous in LastUIElementIntersections)
                    if (_inputRecords.TryGetValue(previous.MembershipId, out UIHitRecord record) && IsCurrentHitRecord(record))
                        UIElementIntersections.Add(previous);
                return;
            }
            _queriedCanvasLifetime = publication.CanvasLifetime;
            _queriedHitGeneration = publication.HitGeneration;
            _queriedCursorPosition = canvasPosition;
            _queriedCapture = _pressedTarget;
            _queriedCaptureRevision = _pressedInteractionRevision;
            UIRuntimeCounters.Add(UIWorkCounter.HitQueries);
            for (int page = 0; page < publication.HitRecordPageCount; page++)
                foreach (ref readonly UIHitRecord record in publication.GetHitRecordPage(page))
                if (IsCurrentHitRecord(record) && record.Contains(CursorPositionWorld2D))
                    UIElementIntersections.Add(record.RenderInfo);
            UIRuntimeCounters.Add(UIWorkCounter.HitCandidates, publication.HitRecordCount);
            }
            finally { publication.Release(); }
        }

        private UIPublication? RefreshPresentedInput()
        {
            if (_inputDeactivating || _inputTerminal)
                return null;
            UICanvasComponent? canvas = GetCameraCanvas();
            object? output = OwningPawn?.Viewport ?? (object?)canvas;
            UIPublication? publication = canvas is { IsActiveInHierarchy: true } && output is not null &&
                canvas.TryAcquirePresentedPublication(output, out UIPublication? acquired)
                ? acquired : null;
            bool transferRead = false;
            try
            {
            bool outputChanged = !ReferenceEquals(canvas, _inputCanvas) || !ReferenceEquals(output, _inputOutput);
            if (outputChanged || !ReferenceEquals(publication, _inputPublication))
            {
                _inputPublicationRevision++;
                _inputCanvas = canvas;
                _inputOutput = output;
                if (outputChanged)
                {
                    _queriedCanvasLifetime = 0;
                    _queriedHitGeneration = 0;
                    if (_pendingVirtualFocusCanvas is { } pendingCanvas &&
                        !ReferenceEquals(pendingCanvas, canvas))
                        ClearPendingVirtualFocus();
                    SynchronizeFocusScope(null);
                }
                bool hitRecordsChanged = outputChanged || publication is null || _inputPublication is null ||
                    publication.CanvasLifetime != _inputPublication.CanvasLifetime ||
                    publication.HitGeneration != _inputPublication.HitGeneration;
                UIPublication? previous = _inputPublication;
                publication?.Retain();
                _inputPublication = publication;
                previous?.Release();
                if (!hitRecordsChanged)
                {
                    SynchronizeFocusScope(publication);
                    if (!IsCurrentInputRead(publication, canvas, output))
                        return null;
                    ResolvePresentedVirtualFocus(publication);
                    if (!IsCurrentInputRead(publication, canvas, output))
                        return null;
                    transferRead = true;
                    return publication;
                }
                _inputRecords.Clear();
                if (publication is null)
                {
                    _queriedCanvasLifetime = 0;
                    _queriedHitGeneration = 0;
                }
                if (publication is not null)
                    for (int page = 0; page < publication.HitRecordPageCount; page++)
                foreach (ref readonly UIHitRecord record in publication.GetHitRecordPage(page))
                        _inputRecords.Add(record.MembershipId, record);
                SynchronizeFocusScope(publication);
            }
            if (!IsCurrentInputRead(publication, canvas, output))
                return null;
            ResolvePresentedVirtualFocus(publication);
            if (!IsCurrentInputRead(publication, canvas, output))
                return null;
            transferRead = true;
            return publication;
            }
            finally
            {
                if (!transferRead)
                    publication?.Release();
            }
        }

        private bool IsCurrentInputRead(UIPublication? publication, UICanvasComponent? canvas, object? output)
            => !_inputDeactivating && !_inputTerminal && ReferenceEquals(publication, _inputPublication) &&
                ReferenceEquals(canvas, GetCameraCanvas()) &&
                ReferenceEquals(output, OwningPawn?.Viewport ?? (object?)canvas);

        private bool IsCurrentHitRecord(in UIHitRecord record)
            => record.RenderInfo.Owner is UIComponent component && component.IsActiveInHierarchy &&
                component.UITransform.ElementIdentity == record.Identity &&
                ReferenceEquals(component.UserInterfaceCanvas, GetCameraCanvas()) &&
                record.FocusScopeIdentity == (_inputPublication?.ActiveFocusScopeIdentity ?? default) &&
                (component is not UIInteractableComponent interactable ||
                    interactable.CanInteract && interactable.InteractionRevision == record.InteractionRevision);

        private Vector2 PresentedLocal(UIInteractableComponent target, Vector2 canvasPoint)
            => _inputRecords.TryGetValue(target.RenderInfo2D.MembershipId, out UIHitRecord record)
                ? Vector2.Transform(canvasPoint, record.CanvasToLocal) : Vector2.Zero;

        private UIPointerGeometry PresentedGeometry(UIInteractableComponent target)
            => _inputRecords.TryGetValue(target.RenderInfo2D.MembershipId, out UIHitRecord record)
                ? record.PointerGeometry : default;

        private void DispatchCapturedPointerMove(UIInteractableComponent target)
        {
            if (_pressedPublication is not { } publication ||
                !publication.TryMapPointer(_cursorOutputPosition, out Vector2 canvasPosition,
                    requireInsideOutput: false))
            {
                ClearPointerPress();
                return;
            }
            Vector2 previousCanvasPosition = _pressedLastCanvasPosition;
            Matrix4x4 canvasToLocal = _pressedCanvasToLocal;
            UIPointerGeometry geometry = _pressedPointerGeometry;
            ulong pressEpoch = _pressedPointerPressEpoch;
            _pressedLastCanvasPosition = canvasPosition;
            publication.Retain();
            try
            {
                target.MouseMoved(
                    Vector2.Transform(previousCanvasPosition, canvasToLocal),
                    Vector2.Transform(canvasPosition, canvasToLocal), in geometry);
            }
            catch
            {
                if (_pressedPointerPressEpoch == pressEpoch &&
                    ReferenceEquals(_pressedTarget, target) &&
                    ReferenceEquals(_pressedPublication, publication))
                    ClearPointerPress();
                throw;
            }
            finally { publication.Release(); }
        }

        private void SwapBuffers()
        {
            if (_inputDeactivating || _inputTerminal || _intersectionSwapActive || _intersectionCollectActive)
                return;
            _intersectionSwapActive = true;
            try
            {
                using IDisposable? sample = RuntimeRenderingHostServices.Current.StartProfileScope("UICanvasInputComponent.SwapBuffers");
                UIPublication? current = RefreshPresentedInput();
                current?.Release();
                if (_collectedInputRevision != _inputPublicationRevision)
                    CollectVisible();
                if (_collectedInputRevision != _inputPublicationRevision || _inputDeactivating || _inputTerminal)
                    return;
                _intersectionDispatchActive = true;
                DispatchIntersections();
            }
            catch
            {
                TopMostElement = null;
                TopMostInteractable = null;
                _queriedCanvasLifetime = 0;
                throw;
            }
            finally
            {
                _intersectionDispatchActive = false;
                _intersectionSwapActive = false;
            }
        }

        private void RemoveInvalidIntersections()
        {
            int retained = 0;
            for (int index = 0; index < UIElementIntersections.Count; index++)
            {
                RenderInfo2D item = UIElementIntersections[index];
                if (_inputRecords.TryGetValue(item.MembershipId, out UIHitRecord record) && IsCurrentHitRecord(record))
                    UIElementIntersections[retained++] = item;
            }
            UIElementIntersections.RemoveRange(retained, UIElementIntersections.Count - retained);
        }

        private void DispatchIntersections()
        {
            // If any intersected element has BlocksInputBehind, remove items not in its subtree.
            RemoveInvalidIntersections();
            FilterBlockedInput();

            TopMostElement = null;
            TopMostInteractable = null;

            int bestElementDepth = int.MinValue;
            int bestElementLayer = int.MinValue;
            int bestElementIndex = int.MinValue;
            int bestInteractableDepth = int.MinValue;
            int bestInteractableLayer = int.MinValue;
            int bestInteractableIndex = int.MinValue;

            foreach (var item in UIElementIntersections)
            {
                if (item.Owner is UIComponent uiComponent)
                {
                    UIHitRecord record = _inputRecords[item.MembershipId];
                    int depth = record.Depth;
                    if (IsBetterInputCandidate(depth, record.LayerIndex, record.IndexWithinLayer, bestElementDepth, bestElementLayer, bestElementIndex))
                    {
                        TopMostElement = uiComponent;
                        bestElementDepth = depth;
                        bestElementLayer = record.LayerIndex;
                        bestElementIndex = record.IndexWithinLayer;
                    }
                }

                if (item.Owner is UIInteractableComponent interactable)
                {
                    UIHitRecord record = _inputRecords[item.MembershipId];
                    int depth = record.Depth;
                    if (IsBetterInputCandidate(depth, record.LayerIndex, record.IndexWithinLayer, bestInteractableDepth, bestInteractableLayer, bestInteractableIndex))
                    {
                        TopMostInteractable = interactable;
                        bestInteractableDepth = depth;
                        bestInteractableLayer = record.LayerIndex;
                        bestInteractableIndex = record.IndexWithinLayer;
                    }
                }
            }

            if (!ValidateAndSwapIntersections())
            {
                TopMostElement = null;
                TopMostInteractable = null;
                _queriedCanvasLifetime = 0;
                return;
            }
            if (_pressedTarget is { } captured)
            {
                if (!HasCurrentPointerPressOwner())
                    ClearPointerPress();
                else if (!_lastIntersectionIdentities.Contains(captured.RenderInfo2D) && captured.NeedsMouseMove)
                    DispatchCapturedPointerMove(captured);
            }
            LastCursorPositionWorld2D = CursorPositionWorld2D;
        }

        private static bool IsBetterInputCandidate(int depth, int layerIndex, int indexWithinLayer, int bestDepth, int bestLayerIndex, int bestIndexWithinLayer)
        {
            if (depth != bestDepth)
                return depth > bestDepth;

            if (layerIndex != bestLayerIndex)
                return layerIndex > bestLayerIndex;

            return indexWithinLayer > bestIndexWithinLayer;
        }

        private void FilterBlockedInput()
        {
            UIHitRecord? blocker = null;
            foreach (RenderInfo2D item in UIElementIntersections)
            {
                UIHitRecord record = _inputRecords[item.MembershipId];
                if (record.BlocksInputBehind && (blocker is not { } previous ||
                    IsBetterInputCandidate(record.Depth, record.LayerIndex, record.IndexWithinLayer,
                        previous.Depth, previous.LayerIndex, previous.IndexWithinLayer)))
                    blocker = record;
            }
            if (blocker is not { } front)
                return;
            int retained = 0;
            for (int index = 0; index < UIElementIntersections.Count; index++)
            {
                RenderInfo2D item = UIElementIntersections[index];
                if (_inputRecords[item.MembershipId].IsInSubtreeOf(front))
                    UIElementIntersections[retained++] = item;
            }
            UIElementIntersections.RemoveRange(retained, UIElementIntersections.Count - retained);
        }

        private long _inputLifecycleRevision;

        private bool ValidateAndSwapIntersections()
        {
            _currentIntersectionIdentities.Clear();
            foreach (RenderInfo2D item in UIElementIntersections)
                _currentIntersectionIdentities.Add(item);
            long lifecycle = _inputLifecycleRevision;
            ulong publicationRevision = _inputPublicationRevision;
            int priorCount = LastUIElementIntersections.Count;
            for (int index = 0; index < priorCount; index++)
            {
                RenderInfo2D item = LastUIElementIntersections[index];
                if (!ValidateIntersection(item, true, _currentIntersectionIdentities.Contains(item),
                    lifecycle, publicationRevision))
                    return false;
            }
            for (int index = 0; index < UIElementIntersections.Count; index++)
            {
                RenderInfo2D item = UIElementIntersections[index];
                if (!_lastIntersectionIdentities.Add(item))
                    continue;
                // Record the callback owner before dispatch. A retired generation
                // can then clear every partially entered target on the next query.
                LastUIElementIntersections.Add(item);
                if (!ValidateIntersection(item, false, true, lifecycle, publicationRevision))
                    return false;
            }
            int retained = 0;
            for (int index = 0; index < LastUIElementIntersections.Count; index++)
            {
                RenderInfo2D item = LastUIElementIntersections[index];
                if (_currentIntersectionIdentities.Contains(item))
                    LastUIElementIntersections[retained++] = item;
                else
                    _lastIntersectionIdentities.Remove(item);
            }
            LastUIElementIntersections.RemoveRange(retained, LastUIElementIntersections.Count - retained);
            UIElementIntersections.Clear();
            _currentIntersectionIdentities.Clear();
            return true;
        }

        private bool IsCurrentIntersectionDispatch(long lifecycle, ulong publicationRevision)
            => !_inputDeactivating && !_inputTerminal && _inputLifecycleRevision == lifecycle &&
                _inputPublicationRevision == publicationRevision;

        private void OnGamepadInteractButtonDown()
        {
            if (_inputDeactivating || _inputTerminal || IsVirtualGamepadDispatch)
                return;
            if (FocusedComponent is { } target && IsPointerTargetEligible(target))
                target.OnInteract();
        }
        private void OnGamepadInteractButtonUp()
        {

        }

        private void OnMouseInteractButtonDown()
        {
            if (_inputDeactivating || _inputTerminal)
                return;
            ulong startingEpoch = _nextPointerPressEpoch;
            ClearPointerPress();
            if (_nextPointerPressEpoch != startingEpoch || IsVirtualMouseConsumed)
                return;
            LocalInputInterface? input = GetOwningInput() as LocalInputInterface;
            BaseMouse? mouse = input?.Mouse;
            IRuntimePointerContactSource? source = OwningPawn?.Viewport as IRuntimePointerContactSource;
            object? sourceOwner = source?.PointerContactOwner;
            ulong sourceGeneration = source?.PointerContactGeneration ?? 0UL;
            bool wasTouch = OwningLocalPlayer?.IsTouchMouseDispatch == true;
            UIInteractableComponent? target = FindMouseInteractTarget();
            FocusMouseTarget(target);
            if (_nextPointerPressEpoch != startingEpoch)
                return;
            // Focus may legitimately change mappings. Later callback changes must
            // not transfer this press to a replacement input owner or binding.
            ulong registrationRevision = mouse?.InputRegistrationRevision ?? 0UL;
            LeftClickDown?.Invoke(target);
            if (_nextPointerPressEpoch != startingEpoch)
                return;

            if (target is not null && TryRecordPointerPress(target, input, mouse, registrationRevision,
                source, sourceOwner, sourceGeneration, wasTouch))
            {
                ulong pressEpoch = _pressedPointerPressEpoch;
                target.IsCaptured = true;
                if (_pressedPointerPressEpoch != pressEpoch || !HasCurrentPointerPressOwner())
                {
                    if (_pressedPointerPressEpoch == pressEpoch)
                        ClearPointerPress();
                    return;
                }
                DispatchPressedTarget(target, pressEpoch);
            }
        }

        private void DispatchPressedTarget(UIInteractableComponent target, ulong pressEpoch)
        {
            if (_pressedPointerPressEpoch != pressEpoch || !ReferenceEquals(_pressedTarget, target) ||
                _pressedPublication is not { } pressedPacket)
                return;
            UIPointerGeometry pressedGeometry = _pressedPointerGeometry;
            Matrix4x4 pressedCanvasToLocal = _pressedCanvasToLocal;
            Vector2 pressedCanvasPosition = _pressedLastCanvasPosition;
            pressedPacket.Retain();
            try
            {
                target.PointerPressed(
                    Vector2.Transform(pressedCanvasPosition, pressedCanvasToLocal),
                    in pressedGeometry);
                if (_pressedPointerPressEpoch == pressEpoch &&
                    HasCurrentPointerPressOwner() && target.InteractOnButtonDown)
                    target.OnInteract();
            }
            catch
            {
                if (_pressedPointerPressEpoch == pressEpoch &&
                    ReferenceEquals(_pressedTarget, target) &&
                    ReferenceEquals(_pressedPublication, pressedPacket))
                    ClearPointerPress();
                throw;
            }
            finally { pressedPacket.Release(); }
        }
        private void OnMouseInteractButtonUp()
        {
            if (_inputDeactivating || _inputTerminal)
                return;
            UIInteractableComponent? pressed = _pressedTarget;
            bool validOwner = HasCurrentPointerPressOwner();
            if (!validOwner || pressed is null || IsVirtualMouseConsumed || IsTouchMouseCancelled)
            {
                ClearPointerPress();
                return;
            }
            if (GetOwningInput() is not LocalInputInterface { Mouse: { } pressedMouse } ||
                _pressedPublication is not { } pressedPacket ||
                !pressedPacket.TryMapPointer(pressedMouse.CursorPosition,
                    out Vector2 pressedReleaseCanvas, requireInsideOutput: false))
            {
                ClearPointerPress();
                return;
            }
            UIPointerGeometry pressedGeometry = _pressedPointerGeometry;
            Matrix4x4 pressedCanvasToLocal = _pressedCanvasToLocal;
            pressedPacket.Retain();
            bool releaseDelivered = false;
            try
            {
                ClearPointerPress(cancel: false);
                ulong detachedEpoch = _nextPointerPressEpoch;
                if (GetOwningInput() is LocalInputInterface { Mouse: { } releaseMouse } &&
                    RefreshPresentedInput() is { } releasePacket)
                {
                    try
                    {
                        if (releasePacket.TryMapPointer(releaseMouse.CursorPosition,
                            out Vector2 releasePosition, false))
                            CursorPositionWorld2D = releasePosition;
                    }
                    finally { releasePacket.Release(); }
                }
                UIInteractableComponent? target = FindMouseInteractTarget();
                bool inside = IsPointerTargetEligible(pressed) && ReferenceEquals(target, pressed) &&
                    ReferenceEquals(FocusedComponent, pressed) &&
                    ReferenceEquals(OwningPawn?.Controller?.FocusedInteractable, pressed);
                long interactionRevision = pressed.InteractionRevision;
                pressed.PointerReleased(Vector2.Transform(pressedReleaseCanvas, pressedCanvasToLocal),
                    inside, in pressedGeometry);
                releaseDelivered = true;
                if (inside && _nextPointerPressEpoch == detachedEpoch && _pressedTarget is null &&
                    pressed.InteractionRevision == interactionRevision &&
                    IsPointerTargetEligible(pressed) &&
                    ReferenceEquals(FocusedComponent, pressed) && !pressed.InteractOnButtonDown)
                    OnInteract();
            }
            catch
            {
                if (!releaseDelivered && !ReferenceEquals(_pressedTarget, pressed))
                    pressed.PointerCancelled();
                throw;
            }
            finally { pressedPacket.Release(); }
        }

        private void FocusMouseTarget(UIInteractableComponent? target)
        {
            var controller = OwningPawn?.Controller;
            if (target is not null && controller?.FocusedInteractable is UIInteractableComponent previous &&
                !ReferenceEquals(previous, target))
                previous.IsFocused = false;

            if (!ReferenceEquals(controller, OwningPawn?.Controller))
                return;
            if (target is not null && ReferenceEquals(FocusedComponent, target) &&
                !ReferenceEquals(controller?.FocusedInteractable, target))
                FocusedComponent = null;
            FocusedComponent = target;
        }

        private bool IsPointerTargetEligible(UIInteractableComponent target)
        {
            if (!IsActiveInHierarchy || RefreshPresentedInput() is not { } publication)
                return false;
            try
            {
                return _inputRecords.TryGetValue(target.RenderInfo2D.MembershipId,
                           out UIHitRecord record) &&
                    IsCurrentHitRecord(record) && target.CanInteract &&
                    record.FocusScopeIdentity == publication.ActiveFocusScopeIdentity &&
                    GetCameraCanvas() is { IsActiveInHierarchy: true } canvas &&
                    canvas.CanvasTransform.IsVisibleInHierarchy &&
                    ReferenceEquals(target.UserInterfaceCanvas, canvas);
            }
            finally { publication.Release(); }
        }

        private bool TryRecordPointerPress(UIInteractableComponent target, LocalInputInterface? input,
            BaseMouse? mouse, ulong registrationRevision, IRuntimePointerContactSource? source,
            object? sourceOwner, ulong sourceGeneration, bool wasTouch)
        {
            if (!IsPointerTargetEligible(target) ||
                !_inputRecords.TryGetValue(target.RenderInfo2D.MembershipId, out UIHitRecord pressedRecord) ||
                !IsCurrentHitRecord(pressedRecord) ||
                mouse is null ||
                _inputPublication is not { } pressedPublication ||
                !pressedPublication.TryMapPointer(mouse.CursorPosition, out Vector2 pressedCanvasPosition,
                    requireInsideOutput: false) ||
                !ReferenceEquals(FocusedComponent, target) ||
                !ReferenceEquals(OwningPawn?.Controller?.FocusedInteractable, target) ||
                input is null || mouse is null || !ReferenceEquals(GetOwningInput(), input) ||
                !ReferenceEquals(input.Mouse, mouse) || mouse.InputRegistrationRevision != registrationRevision ||
                !_registeredInputs.Contains(input) ||
                !ReferenceEquals(OwningPawn?.Viewport as IRuntimePointerContactSource, source) ||
                source is not null && (!ReferenceEquals(source.PointerContactOwner, sourceOwner) ||
                    source.PointerContactGeneration != sourceGeneration) ||
                wasTouch != (OwningLocalPlayer?.IsTouchMouseDispatch == true))
                return false;

            pressedPublication.Retain();
            _pressedPublication = pressedPublication;
            SetField(ref _pressedTarget, target, publishNotifications: false);
            _pressedPointerPressEpoch = ++_nextPointerPressEpoch;
            _pressedPointerGeometry = pressedRecord.PointerGeometry;
            _pressedCanvasToLocal = pressedRecord.CanvasToLocal;
            _pressedLastCanvasPosition = pressedCanvasPosition;
            SetField(ref _pressedInteractionRevision, target.InteractionRevision, publishNotifications: false);
            SetField(ref _pressedCanvasLifetime, _inputPublication!.CanvasLifetime, publishNotifications: false);
            SetField(ref _pressedElementIdentity, target.UITransform.ElementIdentity, publishNotifications: false);
            SetField(ref _pressedCanvas, GetCameraCanvas(), publishNotifications: false);
            SetField(ref _pressedInput, input, publishNotifications: false);
            SetField(ref _pressedMouse, mouse, publishNotifications: false);
            SetField(ref _pressedMouseRegistrationRevision, registrationRevision, publishNotifications: false);
            SetField(ref _pressedPointerSource, source, publishNotifications: false);
            SetField(ref _pressedPointerOwner, sourceOwner, publishNotifications: false);
            SetField(ref _pressedPointerGeneration, sourceGeneration, publishNotifications: false);
            SetField(ref _pressedWasTouch, wasTouch, publishNotifications: false);
            return true;
        }

        private bool HasCurrentPointerPressOwner()
        {
            // Dispatch revisions change on every snapshot; registration and source
            // generations instead describe the lifetime of this accepted press.
            if (_pressedTarget is not { } target || !IsPointerTargetEligible(target) ||
                target.InteractionRevision != _pressedInteractionRevision ||
                target.UITransform.ElementIdentity != _pressedElementIdentity ||
                _inputPublication?.CanvasLifetime != _pressedCanvasLifetime ||
                !ReferenceEquals(GetCameraCanvas(), _pressedCanvas) ||
                GetOwningInput() is not LocalInputInterface { Mouse: { } mouse } input ||
                !ReferenceEquals(input, _pressedInput) || !_registeredInputs.Contains(input) ||
                !ReferenceEquals(mouse, _pressedMouse) || mouse.InputRegistrationRevision != _pressedMouseRegistrationRevision ||
                _pressedWasTouch != (OwningLocalPlayer?.IsTouchMouseDispatch == true))
                return false;

            IRuntimePointerContactSource? source = OwningPawn?.Viewport as IRuntimePointerContactSource;
            return ReferenceEquals(source, _pressedPointerSource) &&
                (source is null || ReferenceEquals(source.PointerContactOwner, _pressedPointerOwner) &&
                    source.PointerContactGeneration == _pressedPointerGeneration);
        }

        private void ClearPointerPress(bool cancel = true)
        {
            UIInteractableComponent? target = _pressedTarget;
            SetField(ref _pressedTarget, null, publishNotifications: false);
            _pressedPointerPressEpoch = 0;
            UIPublication? pressedPublication = _pressedPublication;
            _pressedPublication = null;
            _pressedPointerGeometry = default;
            _pressedCanvasToLocal = default;
            _pressedLastCanvasPosition = default;
            SetField(ref _pressedInteractionRevision, 0L, publishNotifications: false);
            SetField(ref _pressedCanvasLifetime, 0UL, publishNotifications: false);
            SetField(ref _pressedElementIdentity, default(UIElementIdentity), publishNotifications: false);
            SetField(ref _pressedCanvas, null, publishNotifications: false);
            SetField(ref _pressedInput, null, publishNotifications: false);
            SetField(ref _pressedMouse, null, publishNotifications: false);
            SetField(ref _pressedMouseRegistrationRevision, 0UL, publishNotifications: false);
            SetField(ref _pressedPointerSource, null, publishNotifications: false);
            SetField(ref _pressedPointerOwner, null, publishNotifications: false);
            SetField(ref _pressedPointerGeneration, 0UL, publishNotifications: false);
            SetField(ref _pressedWasTouch, false, publishNotifications: false);
            pressedPublication?.Release();
            if (cancel)
                target?.PointerCancelled();
        }

        private void OnMouseRightButtonDown()
        {
            if (_inputDeactivating || _inputTerminal)
                return;
            var target = FindMouseInteractTarget();
            if (target is not null && IsPointerTargetEligible(target))
                RightClick?.Invoke(target);
        }

        // --- Modifier key tracking ---
        private bool _ctrlLeft, _ctrlRight;
        private bool _shiftLeft, _shiftRight;
        private bool _altLeft, _altRight;

        private void OnCtrlLeftStateChanged(bool pressed)  { if (!_inputDeactivating && !_inputTerminal) { _ctrlLeft = pressed;  IsCtrlHeld = _ctrlLeft || _ctrlRight; } }
        private void OnCtrlRightStateChanged(bool pressed) { if (!_inputDeactivating && !_inputTerminal) { _ctrlRight = pressed; IsCtrlHeld = _ctrlLeft || _ctrlRight; } }
        private void OnShiftLeftStateChanged(bool pressed)  { if (!_inputDeactivating && !_inputTerminal) { _shiftLeft = pressed;  IsShiftHeld = _shiftLeft || _shiftRight; } }
        private void OnShiftRightStateChanged(bool pressed) { if (!_inputDeactivating && !_inputTerminal) { _shiftRight = pressed; IsShiftHeld = _shiftLeft || _shiftRight; } }
        private void OnAltLeftStateChanged(bool pressed)  { if (!_inputDeactivating && !_inputTerminal) { _altLeft = pressed;  IsAltHeld = _altLeft || _altRight; } }
        private void OnAltRightStateChanged(bool pressed) { if (!_inputDeactivating && !_inputTerminal) { _altRight = pressed; IsAltHeld = _altLeft || _altRight; } }
        private void OnEscapeStateChanged(bool pressed)
        {
            if (pressed && !_inputDeactivating && !_inputTerminal)
                EscapePressed?.Invoke();
        }
        protected virtual void OnGamepadBackButtonDown()
        {
            if (_inputDeactivating || _inputTerminal || IsVirtualGamepadDispatch)
                return;
            UIInteractableComponent? target = FocusedComponent;
            if (target is not null && IsPointerTargetEligible(target))
                BackPressed?.Invoke(target);
            if (target is not null && ReferenceEquals(FocusedComponent, target) && IsPointerTargetEligible(target))
                target.OnBack();
        }
        protected virtual void OnGamepadBackButtonUp()
        {

        }

        protected virtual void OnLeftStickX(float value) { }
        protected virtual void OnLeftStickY(float value) { }

        /// <summary>
        /// Called on either left click or A button.
        /// Default behavior will OnClick the currently focused/highlighted UI component, if anything.
        /// </summary>
        protected virtual void OnInteract()
        {
            if (_inputDeactivating || _inputTerminal)
                return;
            if (FocusedComponent is { } target && IsPointerTargetEligible(target))
                target.OnInteract();
        }
        private void OnTabStateChanged(bool pressed)
        {
            if (pressed)
                MoveFocus(null, IsShiftHeld);
        }

        protected virtual void OnDPadUp() => MoveFocus(Vector2.UnitY);
        protected virtual void OnDPadDown() => MoveFocus(-Vector2.UnitY);
        protected virtual void OnDPadLeft() => MoveFocus(-Vector2.UnitX);
        protected virtual void OnDPadRight() => MoveFocus(Vector2.UnitX);

        private void MoveFocus(Vector2? direction, bool reverse = false)
        {
            if (_inputDeactivating || _inputTerminal || IsVirtualGamepadDispatch ||
                RefreshPresentedInput() is not { } publication)
                return;
            try
            {
            long current = FocusedComponent?.RenderInfo2D.MembershipId ?? 0;
            if (direction is { } navigation && FocusedComponent is { } focused &&
                IsPointerTargetEligible(focused) && focused.TryConsumeFocusDirection(navigation))
                return;
            for (int attempt = 0; attempt < publication.HitRecordCount; attempt++)
            {
                int index = direction is { } movement
                    ? UIFocusNavigation.FindDirectionalTarget(publication, current, movement, publication.ActiveFocusScopeIdentity)
                    : UIFocusNavigation.FindTabTarget(publication, current, reverse, publication.ActiveFocusScopeIdentity);
                if (index < 0)
                    return;
                UIHitRecord record = publication.GetHitRecord(index);
                if (IsCurrentHitRecord(record) && record.RenderInfo.Owner is UIInteractableComponent target)
                {
                    FocusMouseTarget(target);
                    return;
                }
                current = record.MembershipId;
            }
            }
            finally { publication.Release(); }
        }

        private IRuntimeRenderSchedulingServices? _viewportSubscriptionHost;
        private bool _subscribedToCollect;
        private bool _subscribedToSwap;
        protected override void OnComponentActivated()
        {
            if (_inputCleanupActive)
            {
                _inputActivationDeferred = true;
                return;
            }
            if (_inputTerminal || IsDestroyed || IsDestroyQueued)
                return;
            _inputDeactivating = false;
            long lifecycle = _inputLifecycleRevision;
            try
            {
                base.OnComponentActivated();
                if (!OwnsInputActivation(lifecycle))
                    return;
                LinkOwningPawn();
                if (!OwnsInputActivation(lifecycle))
                    return;
                GetCameraCanvas()?.CanvasTransform.InvalidateLayout();
                if (!OwnsInputActivation(lifecycle))
                    return;
                if (_viewportSubscriptionHost is null)
                {
                    IRuntimeRenderSchedulingServices host = RuntimeRenderingHostServices.Current;
                    _viewportSubscriptionHost = host;
                    _subscribedToCollect = true;
                    host.SubscribeViewportCollectVisible(CollectVisible);
                    if (!OwnsInputActivation(lifecycle))
                        return;
                    _subscribedToSwap = true;
                    host.SubscribeViewportSwapBuffers(SwapBuffers);
                }
            }
            catch
            {
                try { TeardownInputOwners(); }
                catch { /* Keep the activation failure as the primary error. */ }
                throw;
            }
        }
        private bool OwnsInputActivation(long lifecycle)
            => !_inputDeactivating && !_inputTerminal && !_inputCleanupActive &&
                !IsDestroyed && !IsDestroyQueued && IsActiveInHierarchy &&
                _inputLifecycleRevision == lifecycle;
        protected override void OnComponentDeactivated()
        {
            List<Exception>? failures = null;
            TryInputCleanup(TeardownInputOwners, ref failures);
            TryInputCleanup(() => base.OnComponentDeactivated(), ref failures);
            if (_inputActivationDeferred)
            {
                _inputActivationDeferred = false;
                if (!_inputTerminal && IsActiveInHierarchy && !IsDestroyed && !IsDestroyQueued)
                    TryInputCleanup(OnComponentActivated, ref failures);
            }
            ThrowInputCleanupFailures(failures);
        }

        protected void OnChildAdded(UIComponent child)
        {
            //child.OwningActor = this;
        }

        //public void Render()
        //{
        //    _scene.DoRender(AbstractRenderer.CurrentCamera, null);
        //}

        [Browsable(false)]
        public bool IsLayoutInvalidated { get; private set; }
        public void InvalidateLayout() => IsLayoutInvalidated = true;

        /// <summary>
        /// This is the topmost UI component that the mouse is directly over.
        /// Typically, this will be the component that will receive input, like a button.
        /// Backgrounds and other non-interactive components will still be intersected with, but they will not be considered the topmost interactable.
        /// </summary>
        public UIInteractableComponent? TopMostInteractable { get; private set; }
        public UIComponent? TopMostElement { get; private set; }

        private List<RenderInfo2D> LastUIElementIntersections = [];
        private List<RenderInfo2D> UIElementIntersections = [];
        private HashSet<RenderInfo2D> _lastIntersectionIdentities = new(ReferenceEqualityComparer.Instance);
        private HashSet<RenderInfo2D> _currentIntersectionIdentities = new(ReferenceEqualityComparer.Instance);

        private bool ValidateIntersection(RenderInfo2D item, bool wasIntersecting,
            bool isIntersecting, long lifecycle, ulong publicationRevision)
        {
            if (item.Owner is not UIInteractableComponent inter)
                return IsCurrentIntersectionDispatch(lifecycle, publicationRevision);
            bool eligible = isIntersecting && IsPointerTargetEligible(inter);
            if (!IsCurrentIntersectionDispatch(lifecycle, publicationRevision))
                return false;
            inter.IsMouseOver = eligible;
            if (!IsCurrentIntersectionDispatch(lifecycle, publicationRevision))
                return false;
            // An enter callback may recycle or deactivate the target without
            // changing the presented packet. Revalidate before the next callback.
            eligible = eligible && IsPointerTargetEligible(inter);
            if (!IsCurrentIntersectionDispatch(lifecycle, publicationRevision))
                return false;
            inter.IsMouseDirectlyOver = eligible && inter == TopMostInteractable;
            if (!IsCurrentIntersectionDispatch(lifecycle, publicationRevision))
                return false;
            if (wasIntersecting && eligible && inter.NeedsMouseMove && IsPointerTargetEligible(inter))
            {
                if (!IsCurrentIntersectionDispatch(lifecycle, publicationRevision))
                    return false;
                if (ReferenceEquals(inter, _pressedTarget))
                    DispatchCapturedPointerMove(inter);
                else
                {
                    UIPointerGeometry geometry = PresentedGeometry(inter);
                    inter.MouseMoved(
                        PresentedLocal(inter, LastCursorPositionWorld2D),
                        PresentedLocal(inter, CursorPositionWorld2D), in geometry);
                }
            }
            return IsCurrentIntersectionDispatch(lifecycle, publicationRevision);
        }

    }
}
