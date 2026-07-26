using System.Reflection;

namespace XREngine.Rendering.UI
{
    public class UIIntInputComponent : UITextInputComponent
    {
        public override bool PostValidateInput(string input)
            => int.TryParse(input, out _);

        protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            if (int.TryParse(Text, out int value))
                foreach (var target in targets)
                    prop.SetValue(target, value);
        }
    }
}
