using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIFloatInputComponent : UITextInputComponent
    {
        public UIFloatInputComponent()
        {
            SingleLineMode = true;
            CommitOnSubmit = true;
            AllowInvalidDraft = true;
        }

        public override bool PostValidateInput(string input)
            => float.TryParse(input, out float value) && float.IsFinite(value);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (float.TryParse(Text, out float value) && float.IsFinite(value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
