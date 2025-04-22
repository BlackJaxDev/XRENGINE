namespace XREngine.Animation
{
    public class AnimInt(int defaultValue) : AnimVar
    {
        public int Value { get; set; } = defaultValue;

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
    }
}
