using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIByteInputComponent : UITextInputComponent
    {
        public override bool PostValidateInput(string input)
            => byte.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (byte.TryParse(Text, out byte value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
