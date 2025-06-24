using XREngine.Data.Colors;
using XREngine.Scene;

namespace XREngine.Rendering.UI
{
    public class UIButtonComponent : UIInteractableComponent
    {
        private string _backgroundColorUniformName = "MatColor";
        private ColorF4 _defaultBackgroundColor = ColorF4.Transparent;
        private ColorF4 _highlightBackgroundColor = ColorF4.DarkGray;
        private ColorF4 _defaultTextColor = ColorF4.Gray;
        private ColorF4 _highlightTextColor = ColorF4.White;

        protected override void OnMouseDirectOverlapEnter()
            => Highlight();
        protected override void OnMouseDirectOverlapLeave()
            => Unhighlight();
        protected override void OnGamepadNavigateEnter()
            => Highlight();
        protected override void OnGamepadNavigateLeave()
            => Unhighlight();

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

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            var bg = BackgroundMaterialComponent?.Material;
            bg?.SetVector4(BackgroundColorUniformName, DefaultBackgroundColor);

            var text = TextComponent;
            if (text is not null)
                text.Color = DefaultTextColor;
        }

        public UIMaterialComponent? BackgroundMaterialComponent => GetSiblingComponent<UIMaterialComponent>();
        public UITextComponent? TextComponent => SceneNode.FirstChild?.GetComponent<UITextComponent>();

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
        {
            if (IsHighlighted)
                return;

            IsHighlighted = true;

            SetBackgroundHighlighted();
            SetTextHighlighted();
        }

        protected virtual void Unhighlight()
        {
            if (!IsHighlighted)
                return;

            IsHighlighted = false;

            SetBackgroundDefault();
            SetTextDefault();
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