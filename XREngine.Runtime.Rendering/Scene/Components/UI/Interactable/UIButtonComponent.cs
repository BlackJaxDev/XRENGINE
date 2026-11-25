using XREngine.Rendering.UI.State;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Input;
using XREngine.Input.Devices;

namespace XREngine.Rendering.UI
{
    public class UIButtonComponent : UIInteractableComponent
    {
        public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.Button;
        public override string AccessibilityName => AccessibilityLabel is { Length: > 0 } label ? label :
            TextComponent?.Text is { Length: > 0 } text ? text : base.AccessibilityName;
        public override bool AccessibilityActivate()
        {
            if (!CanInteract)
                return false;
            OnInteract();
            return true;
        }
        private string _backgroundColorUniformName = "MatColor";
        private ColorF4 _defaultBackgroundColor = ColorF4.Transparent;
        private ColorF4 _highlightBackgroundColor = ColorF4.DarkGray;
        private ColorF4 _defaultTextColor = ColorF4.Gray;
        private ColorF4 _highlightTextColor = ColorF4.White;
        private UIMaterialComponent? _focusRingMaterial;
        private ColorF4 _focusRingColor = ColorF4.White;

        /// <summary>Border material that displays the keyboard focus ring.</summary>
        public UIMaterialComponent? FocusRingMaterial
        {
            get => _focusRingMaterial;
            set
            {
                if (ReferenceEquals(_focusRingMaterial, value))
                    return;
                if (_focusRingMaterial is { IsDestroyed: false } previous)
                    previous.BatchBorderColor = ColorF4.Transparent;
                SetField(ref _focusRingMaterial, value);
                UpdateFocusRing();
            }
        }

        public ColorF4 FocusRingColor
        {
            get => _focusRingColor;
            set
            {
                if (SetField(ref _focusRingColor, value))
                    UpdateFocusRing();
            }
        }

        private void UpdateFocusRing()
        {
            if (_focusRingMaterial is { IsDestroyed: false } ring)
                ring.BatchBorderColor = IsFocused && CanInteract ? FocusRingColor : ColorF4.Transparent;
        }

        protected override void OnMouseDirectOverlapEnter()
            => UpdateHighlightState(mouseDirectlyOver: true, gamepadSelected: IsGamepadSelected);
        protected override void OnMouseDirectOverlapLeave()
            => UpdateHighlightState(mouseDirectlyOver: false, gamepadSelected: IsGamepadSelected);
        protected override void OnGamepadNavigateEnter()
            => UpdateHighlightState(mouseDirectlyOver: IsMouseDirectlyOver, gamepadSelected: true);
        protected override void OnGamepadNavigateLeave()
            => UpdateHighlightState(mouseDirectlyOver: IsMouseDirectlyOver, gamepadSelected: false);

        protected override void OnGotFocus()
        {
            base.OnGotFocus();
            Highlight();
            UpdateFocusRing();
        }

        protected override void OnLostFocus()
        {
            base.OnLostFocus();
            if (IsPressed)
                IsPressed = false;
            UpdateHighlightState(IsMouseDirectlyOver, IsGamepadSelected);
        }

        /// <summary>
        /// Shows the highlight while the pointer is directly over the button, the gamepad selects it,
        /// or it has keyboard focus. Applies the style preset for the resulting state when one is set.
        /// </summary>
        private void UpdateHighlightState(bool mouseDirectlyOver, bool gamepadSelected)
        {
            bool shouldHighlight = mouseDirectlyOver || gamepadSelected || IsFocused;
            if (shouldHighlight)
            {
                if (!IsHighlighted)
                    Highlight(gamepadSelected);
                else if (_style is not null)
                    ApplyStyleForCurrentState(immediate: false, gamepadSelected: gamepadSelected, focused: IsFocused);
            }
            else
            {
                if (IsHighlighted)
                    Unhighlight(gamepadSelected);
                else if (_style is not null)
                    ApplyStyleForCurrentState(immediate: false, gamepadSelected: gamepadSelected, focused: IsFocused);
            }
        }

        public void RegisterClickActions(params Action<UIInteractableComponent>[] actions)
        {
            foreach (var action in actions)
                InteractAction += action;
        }
        public void UnregisterClickActions(params Action<UIInteractableComponent>[] actions)
        {
            foreach (var action in actions)
                InteractAction -= action;
        }

        public override void RegisterInput(IInputRegistration input)
        {
            base.RegisterInput(input);
            input.RegisterKeyStateChange(EKey.Enter, ActivateKey);
            input.RegisterKeyStateChange(EKey.Space, ActivateKey);
        }

        private void ActivateKey(bool pressed)
        {
            if (pressed)
                OnInteract();
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (_style is not null)
            {
                ApplyStyleForCurrentState(immediate: true);
                UpdateFocusRing();
                return;
            }

            var component = BackgroundMaterialComponent;
            component?.SetQuadColor(BackgroundColorUniformName, DefaultBackgroundColor);

            var text = TextComponent;
            if (text is not null)
                text.Color = DefaultTextColor;
            UpdateFocusRing();
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UpdateFocusRing();
        }

        public override void CancelForRecycle()
        {
            FocusRingMaterial = null;
            base.CancelForRecycle();
        }

        public UIMaterialComponent? BackgroundMaterialComponent => GetSiblingComponent<UIMaterialComponent>();
        public UITextComponent? TextComponent => SceneNode?.FirstChild?.GetComponent<UITextComponent>();

        private UIStyle? _style;
        /// <summary>
        /// Optional CSS-inspired style preset driving smooth hover, pressed, and default transitions.
        /// When null, the button falls back to direct <see cref="DefaultBackgroundColor"/> and
        /// <see cref="HighlightBackgroundColor"/> assignment.
        /// </summary>
        public UIStyle? Style
        {
            get => _style;
            set
            {
                if (SetField(ref _style, value) && value is not null)
                    ApplyStyleForCurrentState(immediate: true);
            }
        }

        /// <summary>
        /// Sets the pressed state for a non-pointer press, such as the gamepad confirm button.
        /// A configured <see cref="Style"/> starts its pressed or released transition.
        /// </summary>
        public void SetPressed(bool pressed)
        {
            if (IsPressed != pressed)
                IsPressed = pressed;
        }

        public ColorF4 DefaultBackgroundColor
        {
            get => _defaultBackgroundColor;
            set => SetField(ref _defaultBackgroundColor, value);
        }
        public ColorF4 HighlightBackgroundColor
        {
            get => _highlightBackgroundColor;
            set => SetField(ref _highlightBackgroundColor, value);
        }
        public string BackgroundColorUniformName
        {
            get => _backgroundColorUniformName;
            set => SetField(ref _backgroundColorUniformName, value);
        }
        public ColorF4 DefaultTextColor
        {
            get => _defaultTextColor;
            set => SetField(ref _defaultTextColor, value);
        }
        public ColorF4 HighlightTextColor
        {
            get => _highlightTextColor;
            set => SetField(ref _highlightTextColor, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(IsFocused):
                    UpdateFocusRing();
                    break;
                case nameof(IsPressed):
                    if (_style is not null)
                        ApplyStyleForCurrentState(immediate: false);
                    break;
                case nameof(BackgroundColorUniformName):
                    {
                        if (!IsHighlighted)
                            SetBackgroundDefault();
                        break;
                    }

                case nameof(DefaultBackgroundColor):
                    {
                        if (!IsHighlighted)
                            SetBackgroundDefault();
                        break;
                    }

                case nameof(HighlightBackgroundColor):
                    {
                        if (IsHighlighted)
                            SetBackgroundHighlighted();
                        break;
                    }

                case nameof(DefaultTextColor):
                    {
                        if (!IsHighlighted)
                            SetTextDefault();
                        break;
                    }

                case nameof(HighlightTextColor):
                    {
                        if (IsHighlighted)
                            SetTextHighlighted();
                        break;
                    }
                case nameof(IsEnabled):
                    if (IsEnabled && (IsFocused || IsMouseDirectlyOver))
                        Highlight();
                    else if (!IsEnabled)
                        Unhighlight();
                    break;
            }
        }

        private bool _isHighlighted = false;
        public bool IsHighlighted
        {
            get => _isHighlighted;
            private set => SetField(ref _isHighlighted, value);
        }

        protected virtual void Highlight()
            => Highlight(IsGamepadSelected);

        private void Highlight(bool gamepadSelected)
        {
            if (IsHighlighted || !IsEnabled)
                return;

            IsHighlighted = true;

            if (_style is not null)
            {
                ApplyStyleForCurrentState(immediate: false, gamepadSelected: gamepadSelected, focused: IsFocused);
                return;
            }

            SetBackgroundHighlighted();
            SetTextHighlighted();
        }

        protected virtual void Unhighlight()
            => Unhighlight(IsGamepadSelected);

        private void Unhighlight(bool gamepadSelected)
        {
            if (!IsHighlighted)
                return;

            IsHighlighted = false;
            if (IsPressed)
                IsPressed = false;

            if (_style is not null)
            {
                ApplyStyleForCurrentState(immediate: false, gamepadSelected: gamepadSelected, focused: IsFocused);
                return;
            }

            SetBackgroundDefault();
            SetTextDefault();
        }

        private void ApplyStyleForCurrentState(bool immediate)
            => ApplyStyleForCurrentState(immediate, IsGamepadSelected, IsFocused);

        private void ApplyStyleForCurrentState(bool immediate, bool gamepadSelected, bool focused)
        {
            if (_style is null)
                return;

            EUIInteractionState state = IsPressed
                ? EUIInteractionState.Pressed
                : gamepadSelected
                    ? (_style.Focused is not null ? EUIInteractionState.Focused : EUIInteractionState.Hover)
                    : IsHighlighted
                        ? EUIInteractionState.Hover
                        : focused
                            ? EUIInteractionState.Focused
                            : EUIInteractionState.Default;

            UIStyleProperties resolved = _style.ResolveForState(state);
            UIStyleAnimatorComponent? animator = GetSiblingComponent<UIStyleAnimatorComponent>()
                ?? SceneNode?.AddComponent<UIStyleAnimatorComponent>();
            if (animator is null)
                return;

            if (immediate)
            {
                animator.ApplyImmediate(in resolved);
            }
            else
            {
                animator.TransitionTo(
                    in resolved,
                    _style.ColorTransition,
                    _style.TransformTransition,
                    _style.BlurTransition);
            }
        }

        private void SetTextHighlighted()
        {
            var text = TextComponent;
            if (text is not null)
                text.Color = HighlightTextColor;
        }

        private void SetBackgroundHighlighted()
        {
            var component = BackgroundMaterialComponent;
            component?.SetQuadColor(BackgroundColorUniformName, HighlightBackgroundColor);
        }

        private void SetTextDefault()
        {
            var text = TextComponent;
            if (text is not null)
                text.Color = DefaultTextColor;
        }

        private void SetBackgroundDefault()
        {
            var component = BackgroundMaterialComponent;
            component?.SetQuadColor(BackgroundColorUniformName, DefaultBackgroundColor);
        }
    }
}
