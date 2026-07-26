using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIFloatInputComponent : UITextInputComponent
    {
        public override bool PostValidateInput(string input)
            => float.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (float.TryParse(Text, out float value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
