using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIDoubleInputComponent : UITextInputComponent
    {
        public override bool PostValidateInput(string input)
            => double.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (double.TryParse(Text, out double value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
