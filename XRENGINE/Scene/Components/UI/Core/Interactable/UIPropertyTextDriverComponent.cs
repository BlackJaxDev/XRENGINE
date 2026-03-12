using System.Reflection;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Timers;

namespace XREngine.Rendering.UI
{
    [RequireComponents(typeof(UITextComponent))]
    public class UIPropertyTextDriverComponent : XRComponent
    {
        private PropertyInfo? _property = null;
        private object?[]? _targets = null;
        private GameTimer? _timer = null;

        public UITextComponent TextComponent => GetSiblingComponent<UITextComponent>(true)!;
        //Text input component is optional - if it exists, don't set the text value if the component is focused (aka, the user is inputting text).
        public UITextInputComponent? TextInputComponent => GetSiblingComponent<UITextInputComponent>(false);

        public PropertyInfo? Property
        {
            get => _property;
            set => SetField(ref _property, value);
        }
        public object?[]? Sources
        {
            get => _targets;
            set => SetField(ref _targets, value);
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            _timer = new GameTimer(this);
            _timer.StartMultiFire(UpdateText, TimeSpan.FromSeconds(1.0), -1, null, ETickGroup.Late, (int)ETickOrder.Scene);
        }
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            _timer?.Cancel();
            _timer = null;
        }

        private void UpdateText(TimeSpan totalElapsed, int fireNumber)
        {
            if (Property is null || Sources is null)
                return;

            string? text = null;
            foreach (var target in Sources)
            {
                if (target is null)
                    continue;

                string valueText = Property.GetValue(target)?.ToString() ?? string.Empty;
                if (text is null)
                    text = valueText;
                else if (text != valueText)
                {
                    text = "-";
                    break;
                }
            }
            text ??= string.Empty;

            //Set through the input component if it exists and is not focused, in case pre or post text needs to be added.
            var input = TextInputComponent;
            if (input is not null && !input.IsFocused)
                input.Text = text;
            else
                TextComponent.Text = text;
        }
    }
}
