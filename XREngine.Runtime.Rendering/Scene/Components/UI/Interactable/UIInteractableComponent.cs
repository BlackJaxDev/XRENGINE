using System.Numerics;
using System.Threading;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Input.Devices;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI.State;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    public delegate void DelMouseMove(float x, float y, UIInteractableComponent comp);
    /// <summary>
    /// Bounded UI component that can be interacted with by the player.
    /// </summary>
    [RequiresTransform(typeof(UIBoundableTransform))]
    public abstract partial class UIInteractableComponent : UIComponent, IRenderable, IRuntimeFocusedInteractable
    {
        public UIInteractableComponent()
        {
            //Use render tree purely for culling volume testing, not for rendering.
            RenderInfo3D = RenderInfo3D.New(this);
            RenderInfo2D = RenderInfo2D.New(this);
            RenderedObjects = [RenderInfo2D, RenderInfo3D];
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);

            if (transform is not UIBoundableTransform tfm)
                return;

            tfm.UpdateRenderInfoBounds(RenderInfo2D, RenderInfo3D);
        }
        protected override void UITransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.UITransformPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(UIBoundableTransform.ActualSize):
                    BoundableTransform.UpdateRenderInfoBounds(RenderInfo2D, RenderInfo3D);
                    break;
            }
        }

        public RenderInfo3D RenderInfo3D { get; }
        public RenderInfo2D RenderInfo2D { get; }

        public UIBoundableTransform BoundableTransform => TransformAs<UIBoundableTransform>(true)!;

        /// <summary>Identifies this control within its current canvas attachment.</summary>
        public UIElementIdentity AccessibilityIdentity => UITransform.ElementIdentity;

        /// <summary>Control semantics available to platform accessibility services.</summary>
        public virtual EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.None;

        private string? _accessibilityLabel;
        /// <summary>Optional name used in place of the component or node name.</summary>
        public string? AccessibilityLabel
        {
            get => _accessibilityLabel;
            set => SetField(ref _accessibilityLabel, value);
        }

        /// <summary>The name presented by a platform accessibility service.</summary>
        public virtual string AccessibilityName => AccessibilityLabel is { Length: > 0 } label
            ? label : Name ?? SceneNode?.Name ?? "Engine control";

        /// <summary>The current value exposed to an accessibility adapter.</summary>
        public virtual string? AccessibilityValue => null;

        private string? _accessibilityDescription;
        /// <summary>Additional help text exposed to an accessibility adapter.</summary>
        public string? AccessibilityDescription
        {
            get => _accessibilityDescription;
            set => SetField(ref _accessibilityDescription, value);
        }

        /// <summary>Whether the control should be omitted from accessibility traversal.</summary>
        public bool AccessibilityHidden
        {
            get => _accessibilityHidden;
            set => SetField(ref _accessibilityHidden, value);
        }
        private bool _accessibilityHidden;

        /// <summary>Activates a control through its native accessibility proxy.</summary>
        public virtual bool AccessibilityActivate() => false;

        /// <summary>Adjusts a value through a platform accessibility action.</summary>
        public virtual bool AccessibilityAdjust(int direction) => false;

        /// <summary>Consumes a directional focus action when it edits this control.</summary>
        public virtual bool TryConsumeFocusDirection(Vector2 direction) => false;

        private bool _isTabStop = true;
        /// <summary>Whether sequential focus navigation includes this control.</summary>
        public bool IsTabStop
        {
            get => _isTabStop;
            set => SetField(ref _isTabStop, value);
        }

        private int _tabIndex;
        /// <summary>Lower values precede higher values in sequential focus navigation.</summary>
        public int TabIndex
        {
            get => _tabIndex;
            set => SetField(ref _tabIndex, value);
        }

        /// <summary>Whether the control accepts user actions.</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetField(ref _isEnabled, value);
        }
        private bool _isEnabled = true;

        /// <summary>Whether the control permits a value change.</summary>
        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => SetField(ref _isReadOnly, value);
        }
        private bool _isReadOnly;

        /// <summary>Whether the control has an invalid edit.</summary>
        public bool IsInvalid
        {
            get => _isInvalid;
            set => SetField(ref _isInvalid, value);
        }
        private bool _isInvalid;

        /// <summary>Whether this control represents the selected option.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }
        private bool _isSelected;

        /// <summary>Whether a pointer is holding this control.</summary>
        public bool IsPressed
        {
            get => _isPressed;
            private set => SetField(ref _isPressed, value);
        }
        private bool _isPressed;

        /// <summary>Whether the input host has captured the pointer for this control.</summary>
        public bool IsCaptured
        {
            get => _isCaptured;
            set => SetField(ref _isCaptured, value);
        }
        private bool _isCaptured;

        /// <summary>Whether the control can accept an action in its current hierarchy.</summary>
        public bool CanInteract => IsEnabled && IsActiveInHierarchy &&
            UITransform.IsVisibleInHierarchy &&
            UserInterfaceCanvas is { IsActiveInHierarchy: true } canvas &&
            canvas.CanvasTransform.IsVisibleInHierarchy;

        public event Action<UIInteractableComponent>? GotFocus;
        public event Action<UIInteractableComponent>? LostFocus;
        public event DelMouseMove? MouseMove;
        public event Action<UIInteractableComponent>? MouseOverlapEnter;
        public event Action<UIInteractableComponent>? MouseOverlapLeave;
        public event Action<UIInteractableComponent>? MouseDirectOverlapEnter;
        public event Action<UIInteractableComponent>? MouseDirectOverlapLeave;
        public event Action<UIInteractableComponent>? GamepadNavigateEnter;
        public event Action<UIInteractableComponent>? GamepadNavigateLeave;

        private bool _registerInputsOnFocus = true;
        /// <summary>
        /// When true, this component will register inputs when it gains focus and unregister inputs when it loses focus.
        /// </summary>
        public bool RegisterInputsOnFocus
        {
            get => _registerInputsOnFocus;
            set => SetField(ref _registerInputsOnFocus, value);
        }

        private bool _isFocused = false;
        /// <summary>
        /// Set when this component has focus.
        /// Focus is set after the mouse clicks on this component or when gamepad navigates to it.
        /// </summary>
        public bool IsFocused
        {
            get => _isFocused;
            set
            {
                AdvanceFocusWriteIntent();
                Func<bool>? previousAdmission = _activeFocusWriteAdmission;
                UIFocusWriteReceipt? previousAcquisition = _activeFocusAcquisition;
                UIFocusWriteReceipt? previousAbort = _activeFocusAbort;
                _activeFocusWriteAdmission = null;
                _activeFocusAcquisition = null;
                _activeFocusAbort = null;
                try { SetField(ref _isFocused, value); }
                finally
                {
                    if (_activeFocusWriteAdmission is null)
                        _activeFocusWriteAdmission = previousAdmission;
                    _activeFocusAcquisition = previousAcquisition;
                    _activeFocusAbort = previousAbort;
                }
            }
        }

        private bool _isMouseOver = false;
        /// <summary>
        /// Set when the mouse is over this component.
        /// </summary>
        public bool IsMouseOver
        {
            get => _isMouseOver;
            set => SetField(ref _isMouseOver, value);
        }

        private bool _isMouseDirectlyOver = false;
        /// <summary>
        /// Set when the mouse is directly over this component.
        /// "Directly over" means this component is the top-most component under the mouse.
        /// </summary>
        public bool IsMouseDirectlyOver
        {
            get => _isMouseDirectlyOver;
            set => SetField(ref _isMouseDirectlyOver, value);
        }

        public UIInteractableComponent? GamepadUpComponent { get; set; }
        public UIInteractableComponent? GamepadDownComponent { get; set; }
        public UIInteractableComponent? GamepadLeftComponent { get; set; }
        public UIInteractableComponent? GamepadRightComponent { get; set; }
        public RenderInfo[] RenderedObjects { get; }

        private long _interactionRevision = 1;
        /// <summary>Changes when an in-flight pointer action must be discarded.</summary>
        public long InteractionRevision => Interlocked.Read(ref _interactionRevision);

        private bool _needsMouseMove = false;
        /// <summary>
        /// Set to true if this component needs mouse move events.
        /// </summary>
        public bool NeedsMouseMove
        {
            get => _needsMouseMove;
            set => SetField(ref _needsMouseMove, value);
        }

        private bool _interactOnButtonDown = false;
        public bool InteractOnButtonDown
        {
            get => _interactOnButtonDown;
            set => SetField(ref _interactOnButtonDown, value);
        }

        protected virtual void OnMouseMove(float x, float y)
            => MouseMove?.Invoke(x, y, this);
        protected virtual void OnMouseOverlapEnter()
            => MouseOverlapEnter?.Invoke(this);
        protected virtual void OnMouseOverlapLeave()
            => MouseOverlapLeave?.Invoke(this);
        protected virtual void OnMouseDirectOverlapEnter()
            => MouseDirectOverlapEnter?.Invoke(this);
        protected virtual void OnMouseDirectOverlapLeave()
            => MouseDirectOverlapLeave?.Invoke(this);
        protected virtual void OnGamepadNavigateEnter()
            => GamepadNavigateEnter?.Invoke(this);
        protected virtual void OnGamepadNavigateLeave()
            => GamepadNavigateLeave?.Invoke(this);

        /// <summary>
        /// Called when this component gains focus.
        /// Focus is gained after the mouse clicks on this component or the gamepad navigates to it.
        /// </summary>
        protected virtual void OnGotFocus()
        {
            //if (RegisterInputsOnFocus)
            //{
            //    var input = OwningUserInterface?.LocalPlayerController?.Input;
            //    if (input != null)
            //    {
            //        input.Unregister = false;
            //        RegisterInputs(input);
            //    }
            //}

            GotFocus?.Invoke(this);
        }

        /// <summary>
        /// Called when this component loses focus.
        /// Focus is lost when the mouse clicks off this component or the gamepad navigates away from it.
        /// </summary>
        protected virtual void OnLostFocus()
        {
            if (RegisterInputsOnFocus)
            {
                //var input = OwningUserInterface?.LocalPlayerController?.Input;
                //if (input != null)
                //{
                //    input.Unregister = true;
                //    RegisterInputs(input);
                //    input.Unregister = false;
                //}
            }

            LostFocus?.Invoke(this);
        }

        public event Action<UIInteractableComponent>? BackAction;

        /// <summary>
        /// Typically mapped to the "B" button on a gamepad to go back or cancel an action, etc.
        /// </summary>
        public void OnBack()
        {
            BackAction?.Invoke(this);
        }

        public event Action<UIInteractableComponent>? InteractAction;
        
        /// <summary>
        /// Typically mapped to the "A" button on a gamepad to click buttons, etc.
        /// </summary>
        public void OnInteract()
        {
            if (!CanInteract)
                return;
            InteractAction?.Invoke(this);
        }

        protected override UIChangeDomain GetUIChangeDomain(string? propertyName)
            => propertyName switch
            {
                nameof(IsTabStop) or nameof(TabIndex) => UIChangeDomain.HitTest,
                nameof(IsEnabled) => UIChangeDomain.Visual | UIChangeDomain.HitTest,
                _ => base.GetUIChangeDomain(propertyName),
            };

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            Func<bool>? focusAdmission = propName == nameof(IsFocused) ? FocusWriteAdmission : null;
            if (!IsFocusWriteCurrent(focusAdmission))
                return;
            base.OnPropertyChanged(propName, prev, field);
            if (!IsFocusWriteCurrent(focusAdmission))
                return;
            switch (propName)
            {
                case nameof(IsMouseOver):
                    if (IsMouseOver)
                        OnMouseOverlapEnter();
                    break;
                case nameof(IsMouseDirectlyOver):
                    if (IsMouseDirectlyOver)
                        OnMouseDirectOverlapEnter();
                    break;
                case nameof(IsFocused):
                    if (IsFocused)
                        OnGotFocus();
                    break;
                case nameof(IsEnabled):
                    if (!IsEnabled)
                    {
                        Interlocked.Increment(ref _interactionRevision);
                        PointerCancelled();
                        IsFocused = false;
                    }
                    RecordControlStateChange(UIChangeDomain.Visual | UIChangeDomain.HitTest);
                    break;
                case nameof(IsReadOnly):
                    if (IsReadOnly)
                    {
                        Interlocked.Increment(ref _interactionRevision);
                        PointerCancelled();
                    }
                    RecordControlStateChange(UIChangeDomain.Visual);
                    break;
                case nameof(IsInvalid):
                case nameof(IsSelected):
                case nameof(IsPressed):
                case nameof(IsCaptured):
                    RecordControlStateChange(UIChangeDomain.Visual);
                    break;
                case nameof(IsTabStop):
                case nameof(TabIndex):
                    break;
            }
        }

        private void RecordControlStateChange(UIChangeDomain domains)
        {
            if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
                transform.RecordUIChange(domains);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            Func<bool>? focusAdmission = propName == nameof(IsFocused) ? FocusWriteAdmission : null;
            if (!IsFocusWriteCurrent(focusAdmission))
                return false;
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (!IsFocusWriteCurrent(focusAdmission))
                return false;
            if (change)
            {
                switch (propName)
                {
                    case nameof(IsMouseOver):
                        if (IsMouseOver)
                            OnMouseOverlapLeave();
                        break;
                    case nameof(IsMouseDirectlyOver):
                        if (IsMouseDirectlyOver)
                            OnMouseDirectOverlapLeave();
                        break;
                    case nameof(IsFocused):
                        if (IsFocused)
                        {
                            if (_activeFocusAbort is { } abort)
                            {
                                abort.AbortLossStarted = true;
                                try { OnCapturedFocusReleased(abort); }
                                catch (Exception exception) { abort.CleanupFailure ??= exception; }
                            }
                            else
                                OnLostFocus();
                        }
                        break;
                }
            }
            return change && IsFocusWriteCurrent(focusAdmission);
        }

        public virtual void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal)
        {
            OnMouseMove(posLocal.X, posLocal.Y);
        }

        /// <summary>Moves a pointer with geometry from its presented hit.</summary>
        public virtual void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal, in UIPointerGeometry geometry)
            => MouseMoved(lastPosLocal, posLocal);

        /// <summary>Begins an accepted pointer action in control-local coordinates.</summary>
        public virtual void PointerPressed(Vector2 localPoint)
        {
            if (CanInteract)
                IsPressed = true;
        }

        /// <summary>Begins a pointer action with geometry from its presented hit.</summary>
        public virtual void PointerPressed(Vector2 localPoint, in UIPointerGeometry geometry)
            => PointerPressed(localPoint);

        /// <summary>Ends an accepted pointer action in control-local coordinates.</summary>
        public virtual void PointerReleased(Vector2 localPoint, bool inside)
        {
            IsPressed = false;
            IsCaptured = false;
        }

        /// <summary>Ends a pointer action with geometry from its pressed hit.</summary>
        public virtual void PointerReleased(Vector2 localPoint, bool inside, in UIPointerGeometry geometry)
            => PointerReleased(localPoint, inside);

        /// <summary>Stops a pointer action without activation.</summary>
        public virtual void PointerCancelled()
        {
            IsPressed = false;
            IsCaptured = false;
        }

        protected override void OnComponentDeactivated()
        {
            Interlocked.Increment(ref _interactionRevision);
            PointerCancelled();
            IsFocused = false;
            IsMouseOver = false;
            IsMouseDirectlyOver = false;
            base.OnComponentDeactivated();
        }

        public virtual void RegisterInput(IInputRegistration input)
        {

        }

        /// <summary>Clears transient interaction when a row is bound to another item.</summary>
        public virtual void CancelForRecycle()
        {
            Interlocked.Increment(ref _interactionRevision);
            PointerCancelled();
            IsFocused = false;
            IsMouseOver = false;
            IsMouseDirectlyOver = false;
        }
    }
}
