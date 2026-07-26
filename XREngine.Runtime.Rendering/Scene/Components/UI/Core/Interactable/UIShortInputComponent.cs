using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIShortInputComponent : UITextInputComponent
    {
        public override bool PostValidateInput(string input)
            => short.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (short.TryParse(Text, out short value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
