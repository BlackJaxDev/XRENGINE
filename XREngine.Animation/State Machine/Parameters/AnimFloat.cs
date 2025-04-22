namespace XREngine.Animation
{
    public class AnimFloat(float defaultValue) : AnimVar
    {
        public float Value { get; set; } = defaultValue;

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
    }
}
