using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIDecimalInputComponent : UITextInputComponent
    {
        public override bool PostValidateInput(string input)
            => decimal.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (decimal.TryParse(Text, out decimal value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
