using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIULongInputComponent : UITextInputComponent
    {
        public UIULongInputComponent()
        {
            SingleLineMode = true;
            CommitOnSubmit = true;
            AllowInvalidDraft = true;
        }

        public override bool PostValidateInput(string input)
            => ulong.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (ulong.TryParse(Text, out ulong value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
