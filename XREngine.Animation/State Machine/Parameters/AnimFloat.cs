namespace XREngine.Animation
{
    public class AnimFloat(string name, float defaultValue) : AnimVar(name)
    {
        public override string ToString() => Value.ToString();

        private float _value = defaultValue;
        public float Value
        {
            get => _value;
            set => SetField(ref _value, value);
        }

        public override bool GreaterThan(AnimTransitionCondition condition)
            => Value > condition.ComparisonFloat;
        public override bool IsTrue()
            => Value > 0.5f;
        public override bool LessThan(AnimTransitionCondition condition)
            => Value < condition.ComparisonFloat;
        public override bool ValueEquals(AnimTransitionCondition condition)
            => Math.Abs(Value - condition.ComparisonFloat) < 0.0001f;

        protected override void SetBool(bool value)
            => Value = value ? 1.0f : 0.0f;
        protected override void SetFloat(float value)
            => Value = value;
        protected override void SetInt(int value)
            => Value = value;

        protected override bool GetBool()
            => Value > 0.5f;
        protected override float GetFloat()
            => Value;
        protected override int GetInt()
            => (int)Value;

        private float? _smoothing = null;
        public float? Smoothing
        {
            get => _smoothing;
            set => SetField(ref _smoothing, value);
        }

        //protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        //{
        //    base.OnPropertyChanged(propName, prev, field);
        //    switch (propName)
        //    {
        //        case nameof(Value):
        //            Debug.WriteLine($"{nameof(AnimFloat)} {ParameterName} changed to {Value}");
        //            break;
        //    }
        //}
    }
}
