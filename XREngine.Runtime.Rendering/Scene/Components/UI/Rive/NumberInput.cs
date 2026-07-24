using XREngine.Core.Attributes;

namespace XREngine.Rendering.UI
{
    [XRTypeRedirect("XREngine.Scene.Components.UI.NumberInput")]
    public class NumberInput : StateMachineInput
    {
        private float _value = 0.0f;
        public float Value
        {
            get => _value;
            set
            {
                if (SetField(ref _value, value))
                    Apply(); // Apply the new value to the state machine.
            }
        }

        protected override void Apply(RiveUIComponent? rivePlayer, string inputName)
            => rivePlayer?.SetNumber(inputName, (float)this.Value);
    }
}
