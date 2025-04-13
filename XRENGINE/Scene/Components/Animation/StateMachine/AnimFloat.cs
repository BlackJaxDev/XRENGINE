namespace XREngine.Components
{
    public class AnimFloat : AnimVar
    {
        public float Value { get; set; }

        public override bool GreaterThan(AnimStateCondition condition)
            => Value > condition.ComparisonFloat;
        public override bool IsTrue()
            => Value > 0.5f;
        public override bool LessThan(AnimStateCondition condition)
            => Value < condition.ComparisonFloat;
        public override bool ValueEquals(AnimStateCondition condition)
            => Math.Abs(Value - condition.ComparisonFloat) < 0.0001f;

        public override void SetBool(bool value)
            => Value = value ? 1.0f : 0.0f;
        public override void SetFloat(float value)
            => Value = value;
        public override void SetInt(int value)
            => Value = value;
    }
}
