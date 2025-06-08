namespace XREngine.Scene.Components.UI
{
    public class NumberInput : StateMachineInput
    {
        private float _value = 0.0f;
        public float Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    Apply(); // Apply the new value to the state machine.
                }
            }
        }

        protected override void Apply(RiveUIComponent? rivePlayer, string inputName)
            => rivePlayer?.SetNumber(inputName, (float)this.Value);
    }
}
