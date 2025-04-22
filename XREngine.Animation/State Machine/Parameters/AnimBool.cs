namespace XREngine.Animation
{
    public class AnimBool(bool defaultValue) : AnimVar
    {
        public bool Value { get; set; } = defaultValue;

        public override bool GreaterThan(AnimTransitionCondition condition)
            => Value && !condition.ComparisonBool;
        public override bool IsTrue()
            => Value;
        public override bool LessThan(AnimTransitionCondition condition)
            => !Value && condition.ComparisonBool;
        public override bool ValueEquals(AnimTransitionCondition condition)
            => Value == condition.ComparisonBool;

        protected override void SetBool(bool value)
            => Value = value;
        protected override void SetFloat(float value)
            => Value = value > 0.5f;
        protected override void SetInt(int value)
            => Value = value != 0;

        protected override bool GetBool()
            => Value;
        protected override float GetFloat()
            => Value ? 1.0f : 0.0f;
        protected override int GetInt()
            => Value ? 1 : 0;
    }
}
