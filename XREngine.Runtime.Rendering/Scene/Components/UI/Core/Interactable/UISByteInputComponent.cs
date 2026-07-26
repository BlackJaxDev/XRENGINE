using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UISByteInputComponent : UITextInputComponent
    {
        public override bool PostValidateInput(string input)
            => sbyte.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (sbyte.TryParse(Text, out sbyte value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
