namespace XREngine.Animation
{
    public class AnimBool(string name, bool defaultValue) : AnimVar(name)
    {
        public override string ToString() => Value.ToString();

        private bool _value = defaultValue;
        public bool Value
        {
            get => _value;
            set => SetField(ref _value, value);
        }

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

        public override int CalcBitCount() => 1;
        public override void WriteBits(byte[] data, ref int bitOffset)
        {
            if (data is null || bitOffset < 0 || bitOffset >= data.Length * 8)
                return;

            int byteOffset = bitOffset / 8;
            int bitInByte = bitOffset % 8;
            if (byteOffset >= data.Length)
                return;

            if (Value)
                data[byteOffset] |= (byte)(1 << bitInByte);
            else
                data[byteOffset] &= (byte)~(1 << bitInByte);

            bitOffset += 1;
        }

        //protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        //{
        //    base.OnPropertyChanged(propName, prev, field);
        //    switch (propName)
        //    {
        //        case nameof(Value):
        //            Debug.WriteLine($"{nameof(AnimBool)} {ParameterName} changed to {Value}");
        //            break;
        //    }
        //}
    }
}
