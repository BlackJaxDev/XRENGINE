namespace XREngine.Components
{
    public class AnimBool : AnimVar
    {
        public bool Value { get; set; }

        public override bool GreaterThan(AnimStateCondition condition)
            => Value && !condition.ComparisonBool;
        public override bool IsTrue()
            => Value;
        public override bool LessThan(AnimStateCondition condition)
            => !Value && condition.ComparisonBool;
        public override bool ValueEquals(AnimStateCondition condition)
            => Value == condition.ComparisonBool;

        public override void SetBool(bool value)
            => Value = value;
        public override void SetFloat(float value)
            => Value = value > 0.5f;
        public override void SetInt(int value)
            => Value = value != 0;
    }
}
