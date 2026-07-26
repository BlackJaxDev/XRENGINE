using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIUIntInputComponent : UITextInputComponent
    {
        public override bool PostValidateInput(string input)
            => uint.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (uint.TryParse(Text, out uint value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
