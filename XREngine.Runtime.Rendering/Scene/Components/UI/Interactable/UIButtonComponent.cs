using XREngine.Data.Colors;
using XREngine.Scene;

namespace XREngine.Rendering.UI
{
    public class UIButtonComponent : UIInteractableComponent
    {
        public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.Button;
        public override string AccessibilityName => AccessibilityLabel is { Length: > 0 } label ? label :
            TextComponent?.Text is { Length: > 0 } text ? text : base.AccessibilityName;
        public override bool AccessibilityActivate()
        {
            OnInteract();
            return true;
        }
        private string _backgroundColorUniformName = "MatColor";
        private ColorF4 _defaultBackgroundColor = ColorF4.Transparent;
        private ColorF4 _highlightBackgroundColor = ColorF4.DarkGray;
        private ColorF4 _defaultTextColor = ColorF4.Gray;
        private ColorF4 _highlightTextColor = ColorF4.White;

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
            if (_style is not null)
                ApplyStyleForCurrentState(immediate: false, gamepadSelected: IsGamepadSelected, focused: true);
        }

        protected override void OnLostFocus()
        {
            base.OnLostFocus();
            if (_isPressed)
                IsPressed = false;
            if (_style is not null)
                ApplyStyleForCurrentState(immediate: false, gamepadSelected: IsGamepadSelected, focused: false);
        }

        private void UpdateHighlightState(bool mouseDirectlyOver, bool gamepadSelected)
        {
            bool shouldHighlight = mouseDirectlyOver || gamepadSelected;
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

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (_style is not null)
            {
                ApplyStyleForCurrentState(immediate: true);
                return;
            }

            var bg = BackgroundMaterialComponent?.Material;
            bg?.SetVector4(BackgroundColorUniformName, DefaultBackgroundColor);

            var text = TextComponent;
            if (text is not null)
                text.Color = DefaultTextColor;
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

        private bool _isPressed;
        /// <summary>
        /// Gets a value indicating whether the button is currently in the pressed visual state.
        /// </summary>
        public bool IsPressed
        {
            get => _isPressed;
            private set => SetField(ref _isPressed, value);
        }

        /// <summary>
        /// Updates the pressed visual state and triggers any configured <see cref="Style"/> transition.
        /// </summary>
        public void SetPressed(bool pressed)
        {
            if (_isPressed == pressed)
                return;

            IsPressed = pressed;
            if (_style is not null)
                ApplyStyleForCurrentState(immediate: false);
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
            if (IsHighlighted)
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
            if (_isPressed)
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

            EUIInteractionState state = _isPressed
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
            var bg = BackgroundMaterialComponent?.Material;
            bg?.SetVector4(BackgroundColorUniformName, HighlightBackgroundColor);
        }

        private void SetTextDefault()
        {
            var text = TextComponent;
            if (text is not null)
                text.Color = DefaultTextColor;
        }

        private void SetBackgroundDefault()
        {
            var bg = BackgroundMaterialComponent?.Material;
            bg?.SetVector4(BackgroundColorUniformName, DefaultBackgroundColor);
        }
    }
}
