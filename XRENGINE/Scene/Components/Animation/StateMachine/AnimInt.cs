namespace XREngine.Components
{
    public class AnimInt : AnimVar
    {
        public int Value { get; set; }

        public override bool GreaterThan(AnimStateCondition condition)
            => Value > condition.ComparisonInt;
        public override bool IsTrue()
            => Value != 0;
        public override bool LessThan(AnimStateCondition condition)
            => Value < condition.ComparisonInt;
        public override bool ValueEquals(AnimStateCondition condition)
            => Value == condition.ComparisonInt;

        public override void SetBool(bool value)
            => Value = value ? 1 : 0;
        public override void SetFloat(float value)
            => Value = (int)value;
        public override void SetInt(int value)
            => Value = value;
    }
}
