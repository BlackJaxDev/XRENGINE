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
        private bool _inputDeactivating;
        /// <summary>
        /// The UI component focused on by the gamepad or last interacted with by the mouse.
        /// </summary>
        public UIInteractableComponent? FocusedComponent
        {
            get => _focusedComponent;
            set => SetField(ref _focusedComponent, _inputDeactivating ? null : value);
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
                        FocusedComponent = null;
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
            if (!IsActiveInHierarchy || _owningPawn is null || ReferenceEquals(_observedPawn, _owningPawn))
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
            if (_inputDeactivating && !input.Unregister)
                return;
            // The pawn host and this canvas's owner subscription can both request the
            // same mappings. Retain both routes while installing each interface once.
            if (input.Unregister)
            {
                if (ReferenceEquals(_pressedInput, input))
                    ClearPointerPress();
                if (!_registeredInputs.Remove(input))
                    return;
            }
            else if (!_registeredInputs.Add(input))
                return;

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
            ReconcileFocusedComponent();
        }

        private void OnMouseScroll(float diff)
        {
            if (MathF.Abs(diff) <= float.Epsilon)
                return;

            if (RefreshPresentedInput() is not { } publication)
                return;
            try
            {
            if (TopMostInteractable is { } target && !IsPointerTargetEligible(target))
                return;

            UIComponent? hitOwner = TopMostElement ?? TopMostInteractable;
            if (hitOwner is null)
                return;
            UIHitRecord hit = default;
            bool found = false;
            foreach (ref readonly UIHitRecord candidate in publication.Hits)
                if (ReferenceEquals(candidate.RenderInfo.Owner, hitOwner) && IsCurrentHitRecord(candidate))
                {
                    hit = candidate;
                    found = true;
                    break;
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
                if (receiver.Receiver.HandleMouseScroll(diff) || !ReferenceEquals(_inputPublication, publication))
                    return;
            }
            }
            finally { publication.Release(); }
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
        private readonly Dictionary<long, UIHitRecord> _inputRecords = [];

        private void MouseMove(float x, float y)
            => _cursorOutputPosition = new Vector2(x, y);

        private void CollectVisible()
        {
            UIElementIntersections.Clear();
            UIPublication? publication = RefreshPresentedInput();
            if (publication is null)
                return;
            try
            {

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
            foreach (ref readonly UIHitRecord record in publication.Hits)
                if (IsCurrentHitRecord(record) && record.Contains(CursorPositionWorld2D))
                    UIElementIntersections.Add(record.RenderInfo);
            UIRuntimeCounters.Add(UIWorkCounter.HitCandidates, publication.Hits.Length);
            }
            finally { publication.Release(); }
        }

        private UIPublication? RefreshPresentedInput()
        {
            UICanvasComponent? canvas = GetCameraCanvas();
            object? output = OwningPawn?.Viewport ?? (object?)canvas;
            UIPublication? publication = canvas is { IsActiveInHierarchy: true } && output is not null &&
                canvas.TryAcquirePresentedPublication(output, out UIPublication? acquired)
                ? acquired : null;
            try
            {
            bool outputChanged = !ReferenceEquals(canvas, _inputCanvas) || !ReferenceEquals(output, _inputOutput);
            if (outputChanged || !ReferenceEquals(publication, _inputPublication))
            {
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
                    ResolvePresentedVirtualFocus(publication);
                    return publication;
                }
                _inputRecords.Clear();
                if (publication is null)
                {
                    _queriedCanvasLifetime = 0;
                    _queriedHitGeneration = 0;
                }
                if (publication is not null)
                    foreach (ref readonly UIHitRecord record in publication.Hits)
                        _inputRecords.Add(record.MembershipId, record);
                SynchronizeFocusScope(publication);
            }
            ResolvePresentedVirtualFocus(publication);
            return publication;
            }
            catch
            {
                publication?.Release();
                throw;
            }
        }

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

        private void SwapBuffers()
        {
            using IDisposable? sample = RuntimeRenderingHostServices.Current.StartProfileScope("UICanvasInputComponent.SwapBuffers");

            // If any intersected element has BlocksInputBehind, remove items not in its subtree.
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

            ValidateAndSwapIntersections();
            if (_pressedTarget is { } captured)
            {
                if (!HasCurrentPointerPressOwner())
                    ClearPointerPress();
                else if (!_lastIntersectionIdentities.Contains(captured.RenderInfo2D) && captured.NeedsMouseMove)
                    captured.MouseMoved(PresentedLocal(captured, LastCursorPositionWorld2D),
                        PresentedLocal(captured, CursorPositionWorld2D));
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

        private void ValidateAndSwapIntersections()
        {
            foreach (RenderInfo2D item in UIElementIntersections)
                _currentIntersectionIdentities.Add(item);
            long lifecycle = _inputLifecycleRevision;
            for (int index = 0; index < LastUIElementIntersections.Count; index++)
            {
                ValidateIntersection(LastUIElementIntersections[index]);
                if (_inputLifecycleRevision != lifecycle)
                    return;
            }
            for (int index = 0; index < UIElementIntersections.Count; index++)
            {
                RenderInfo2D item = UIElementIntersections[index];
                if (!_lastIntersectionIdentities.Contains(item))
                    ValidateIntersection(item);
                if (_inputLifecycleRevision != lifecycle)
                    return;
            }

            (LastUIElementIntersections, UIElementIntersections) = (UIElementIntersections, LastUIElementIntersections);
            (_lastIntersectionIdentities, _currentIntersectionIdentities) = (_currentIntersectionIdentities, _lastIntersectionIdentities);
            UIElementIntersections.Clear();
            _currentIntersectionIdentities.Clear();
        }

        private void OnGamepadInteractButtonDown()
        {
            if (IsVirtualGamepadDispatch)
                return;
            if (FocusedComponent is { } target && IsPointerTargetEligible(target))
                target.OnInteract();
        }
        private void OnGamepadInteractButtonUp()
        {

        }

        private void OnMouseInteractButtonDown()
        {
            ClearPointerPress();
            if (IsVirtualMouseConsumed)
                return;
            LocalInputInterface? input = GetOwningInput() as LocalInputInterface;
            BaseMouse? mouse = input?.Mouse;
            IRuntimePointerContactSource? source = OwningPawn?.Viewport as IRuntimePointerContactSource;
            object? sourceOwner = source?.PointerContactOwner;
            ulong sourceGeneration = source?.PointerContactGeneration ?? 0UL;
            bool wasTouch = OwningLocalPlayer?.IsTouchMouseDispatch == true;
            UIInteractableComponent? target = FindMouseInteractTarget();
            FocusMouseTarget(target);
            // Focus may legitimately change mappings. Later callback changes must
            // not transfer this press to a replacement input owner or binding.
            ulong registrationRevision = mouse?.InputRegistrationRevision ?? 0UL;
            LeftClickDown?.Invoke(target);

            if (target is not null && TryRecordPointerPress(target, input, mouse, registrationRevision,
                source, sourceOwner, sourceGeneration, wasTouch))
            {
                target.IsCaptured = true;
                if (!HasCurrentPointerPressOwner())
                {
                    ClearPointerPress();
                    return;
                }
                target.PointerPressed(PresentedLocal(target, CursorPositionWorld2D));
                if (HasCurrentPointerPressOwner() && target.InteractOnButtonDown)
                    target.OnInteract();
            }
        }
        private void OnMouseInteractButtonUp()
        {
            UIInteractableComponent? pressed = _pressedTarget;
            bool validOwner = HasCurrentPointerPressOwner();
            if (!validOwner || pressed is null || IsVirtualMouseConsumed || IsTouchMouseCancelled)
            {
                ClearPointerPress();
                return;
            }
            ClearPointerPress(cancel: false);
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
            pressed.PointerReleased(PresentedLocal(pressed, CursorPositionWorld2D), inside);
            if (inside && pressed.InteractionRevision == interactionRevision && IsPointerTargetEligible(pressed) &&
                ReferenceEquals(FocusedComponent, pressed) && !pressed.InteractOnButtonDown)
                OnInteract();
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

            SetField(ref _pressedTarget, target, publishNotifications: false);
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
            if (cancel)
                target?.PointerCancelled();
        }

        private void OnMouseRightButtonDown()
        {
            var target = FindMouseInteractTarget();
            if (target is not null && IsPointerTargetEligible(target))
                RightClick?.Invoke(target);
        }

        // --- Modifier key tracking ---
        private bool _ctrlLeft, _ctrlRight;
        private bool _shiftLeft, _shiftRight;
        private bool _altLeft, _altRight;

        private void OnCtrlLeftStateChanged(bool pressed)  { _ctrlLeft = pressed;  IsCtrlHeld = _ctrlLeft || _ctrlRight; }
        private void OnCtrlRightStateChanged(bool pressed) { _ctrlRight = pressed; IsCtrlHeld = _ctrlLeft || _ctrlRight; }
        private void OnShiftLeftStateChanged(bool pressed)  { _shiftLeft = pressed;  IsShiftHeld = _shiftLeft || _shiftRight; }
        private void OnShiftRightStateChanged(bool pressed) { _shiftRight = pressed; IsShiftHeld = _shiftLeft || _shiftRight; }
        private void OnAltLeftStateChanged(bool pressed)  { _altLeft = pressed;  IsAltHeld = _altLeft || _altRight; }
        private void OnAltRightStateChanged(bool pressed) { _altRight = pressed; IsAltHeld = _altLeft || _altRight; }
        private void OnEscapeStateChanged(bool pressed)
        {
            if (pressed)
                EscapePressed?.Invoke();
        }
        protected virtual void OnGamepadBackButtonDown()
        {
            if (IsVirtualGamepadDispatch)
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
            if (IsVirtualGamepadDispatch || RefreshPresentedInput() is not { } publication)
                return;
            try
            {
            long current = FocusedComponent?.RenderInfo2D.MembershipId ?? 0;
            if (direction is { } navigation && FocusedComponent is { } focused &&
                IsPointerTargetEligible(focused) && focused.TryConsumeFocusDirection(navigation))
                return;
            for (int attempt = 0; attempt < publication.Hits.Length; attempt++)
            {
                int index = direction is { } movement
                    ? UIFocusNavigation.FindDirectionalTarget(publication.Hits, current, movement, publication.ActiveFocusScopeIdentity)
                    : UIFocusNavigation.FindTabTarget(publication.Hits, current, reverse, publication.ActiveFocusScopeIdentity);
                if (index < 0)
                    return;
                UIHitRecord record = publication.Hits[index];
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

        private bool _subscribedToTimer;
        protected override void OnComponentActivated()
        {
            _inputDeactivating = false;
            base.OnComponentActivated();
            LinkOwningPawn();
            GetCameraCanvas()?.CanvasTransform.InvalidateLayout();
            if (!_subscribedToTimer)
            {
                RuntimeRenderingHostServices.Current.SubscribeViewportCollectVisible(CollectVisible);
                RuntimeRenderingHostServices.Current.SubscribeViewportSwapBuffers(SwapBuffers);
                _subscribedToTimer = true;
            }
        }
        protected override void OnComponentDeactivated()
        {
            _inputDeactivating = true;
            _inputLifecycleRevision++;
            ClearPointerPress();
            FocusedComponent = null;
            UnlinkOwningPawn();
            UnregisterCanvasInputs();
            UpdateImeFocusTarget();
            for (int index = 0; index < LastUIElementIntersections.Count; index++)
                if (LastUIElementIntersections[index].Owner is UIInteractableComponent interactable)
                {
                    interactable.IsMouseOver = false;
                    interactable.IsMouseDirectlyOver = false;
                }
            LastUIElementIntersections.Clear();
            UIElementIntersections.Clear();
            _lastIntersectionIdentities.Clear();
            _currentIntersectionIdentities.Clear();
            _inputRecords.Clear();
            ClearPendingVirtualFocus();
            UIPublication? oldInputPublication = _inputPublication;
            _inputPublication = null;
            oldInputPublication?.Release();
            SynchronizeFocusScope(null);
            _queriedCanvasLifetime = 0;
            _queriedHitGeneration = 0;
            TopMostElement = null;
            TopMostInteractable = null;
            base.OnComponentDeactivated();
            if (_subscribedToTimer)
            {
                RuntimeRenderingHostServices.Current.UnsubscribeViewportCollectVisible(CollectVisible);
                RuntimeRenderingHostServices.Current.UnsubscribeViewportSwapBuffers(SwapBuffers);
                _subscribedToTimer = false;
            }
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

        private void ValidateIntersection(RenderInfo2D item)
        {
            if (item.Owner is not UIInteractableComponent inter)
                return;

            // Membership uses immutable reference identity. Painter order is only a list order.
            bool wasIntersecting = _lastIntersectionIdentities.Contains(item);
            bool isIntersecting = _currentIntersectionIdentities.Contains(item);

            if (wasIntersecting)
            {
                //Mouse was over this renderable last update
                if (!isIntersecting)
                {
                    //Lost mouse over
                    inter.IsMouseOver = false;
                    inter.IsMouseDirectlyOver = false;
                }
                else
                {
                    //Had mouse over and still does now — re-evaluate direct-over in case TopMost changed
                    bool shouldBeDirectlyOver = inter == TopMostInteractable && IsPointerTargetEligible(inter);
                    if (inter.IsMouseDirectlyOver != shouldBeDirectlyOver)
                        inter.IsMouseDirectlyOver = shouldBeDirectlyOver;

                    if (inter.NeedsMouseMove && IsPointerTargetEligible(inter))
                        inter.MouseMoved(
                            PresentedLocal(inter, LastCursorPositionWorld2D),
                            PresentedLocal(inter, CursorPositionWorld2D));
                }
            }
            else if (isIntersecting) //Mouse was not over this renderable last update
            {
                //Got mouse over
                inter.IsMouseOver = true;
                inter.IsMouseDirectlyOver = inter == TopMostInteractable && IsPointerTargetEligible(inter);
            }
            else
            {
                //Not over this renderable
                inter.IsMouseOver = false;
                inter.IsMouseDirectlyOver = false;
            }
        }

    }
}
