using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UILongInputComponent : UITextInputComponent
    {
        public override bool PostValidateInput(string input)
            => long.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (long.TryParse(Text, out long value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
