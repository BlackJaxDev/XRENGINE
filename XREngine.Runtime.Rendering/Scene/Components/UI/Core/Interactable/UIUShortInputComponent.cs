using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIUShortInputComponent : UITextInputComponent
    {
        public override bool PostValidateInput(string input)
            => ushort.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (ushort.TryParse(Text, out ushort value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
