namespace XREngine.Animation
{
    public class AnimInt(string name, int defaultValue) : AnimVar(name)
    {
        public override string ToString() => Value.ToString();

        private int _value = defaultValue;
        public int Value
        {
            get => _value;
            set => SetField(ref _value, value);
        }

        public override bool GreaterThan(AnimTransitionCondition condition)
            => Value > condition.ComparisonInt;
        public override bool IsTrue()
            => Value != 0;
        public override bool LessThan(AnimTransitionCondition condition)
            => Value < condition.ComparisonInt;
        public override bool ValueEquals(AnimTransitionCondition condition)
            => Value == condition.ComparisonInt;

        protected override void SetBool(bool value)
            => Value = value ? 1 : 0;
        protected override void SetFloat(float value)
            => Value = (int)value;
        protected override void SetInt(int value)
            => Value = value;

        protected override bool GetBool()
            => Value != 0;
        protected override float GetFloat()
            => Value;
        protected override int GetInt()
            => Value;

        //protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        //{
        //    base.OnPropertyChanged(propName, prev, field);
        //    switch (propName)
        //    {
        //        case nameof(Value):
        //            Debug.WriteLine($"{nameof(AnimInt)} {ParameterName} changed to {Value}");
        //            break;
        //    }
        //}
    }
}
