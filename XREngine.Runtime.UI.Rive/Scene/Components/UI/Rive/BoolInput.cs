using XREngine.Core.Attributes;

namespace XREngine.Rendering.UI
{
    [XRTypeRedirect("XREngine.Scene.Components.UI.BoolInput")]
    public class BoolInput : StateMachineInput
    {
        private bool _value = false;
        public bool Value
        {
            get => _value;
            set
            {
                if (SetField(ref _value, value))
                    Apply(); // Apply the new value to the state machine.
            }
        }

        protected override void Apply(RiveUIComponent? rivePlayer, string inputName)
            => rivePlayer?.SetBool(inputName, this.Value);
    }
}
