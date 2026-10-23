using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIDoubleInputComponent : UITextInputComponent
    {
        public UIDoubleInputComponent()
        {
            SingleLineMode = true;
            CommitOnSubmit = true;
            AllowInvalidDraft = true;
        }

        public override bool PostValidateInput(string input)
            => double.TryParse(input, out double value) && double.IsFinite(value);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (double.TryParse(Text, out double value) && double.IsFinite(value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
