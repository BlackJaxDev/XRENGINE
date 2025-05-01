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

        private bool _negativeAllowed = false;
        public bool NegativeAllowed
        {
            get => _negativeAllowed;
            set => SetField(ref _negativeAllowed, value);
        }

        private int _calculatedBitCountWithoutSign = 0;

        public override int CalcBitCount()
        {
            int bitCount = 0;
            if (NegativeAllowed)
            {
                // Get the absolute value of the negative number
                int absValue = Math.Abs(Value);
                // Count the number of bits needed to represent the absolute value
                while (absValue > 0)
                {
                    absValue >>= 1;
                    bitCount++;
                }
            }
            else
            {
                // Count the number of bits needed to represent the positive value
                int absValue = Value;
                while (absValue > 0)
                {
                    absValue >>= 1;
                    bitCount++;
                }
            }
            _calculatedBitCountWithoutSign = bitCount;
            if (NegativeAllowed)
            {
                // Add 1 bit for the sign
                bitCount++;
            }
            return bitCount;
        }
        public override void WriteBits(byte[] data, ref int bitOffset)
        {
            if (data is null || bitOffset < 0 || bitOffset >= data.Length * 8)
                return;

            int byteOffset = bitOffset / 8;
            int bitInByte = bitOffset % 8;
            if (byteOffset >= data.Length)
                return;

            // Write the sign bit if negative
            if (NegativeAllowed && Value < 0)
            {
                data[byteOffset] |= (byte)(1 << bitInByte);
                bitOffset++;
            }

            // Write the absolute value bits
            int absValue = Math.Abs(Value);
            for (int i = 0; i < _calculatedBitCountWithoutSign; i++)
            {
                if ((absValue & (1 << i)) != 0)
                    data[byteOffset] |= (byte)(1 << (bitInByte + i));
                else
                    data[byteOffset] &= (byte)~(1 << (bitInByte + i));
            }

            bitOffset += _calculatedBitCountWithoutSign;
        }

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
