using System.Diagnostics;

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

        public int CompressedBitCount { get; set; } = 8;
        public override int CalcBitCount() => CompressedBitCount;

        public override void WriteBits(byte[] data, ref int bitOffset)
        {
            if (CompressedBitCount == 32) //Write raw float
            {
                Span<byte> bytes = stackalloc byte[4];
                if (!BitConverter.TryWriteBytes(bytes, Value))
                {
                    Trace.WriteLine($"AnimFloat.WriteBits: Failed to convert float {Value} to bytes.");
                    return;
                }
                int byteOffset = bitOffset / 8;
                int bitsToWrite = Math.Min(32, data.Length * 8 - bitOffset);
                for (int i = 0; i < bitsToWrite; i++)
                {
                    data[byteOffset] |= (byte)((bytes[i / 8] >> (i % 8)) & 1);
                    bitOffset++;
                }
            }
            else if (CompressedBitCount == 16) //Write half float
            {
                Span<byte> bytes = stackalloc byte[2];
                if (!BitConverter.TryWriteBytes(bytes, (Half)Value))
                {
                    Trace.WriteLine($"AnimFloat.WriteBits: Failed to convert float {Value} to half-float.");
                    return;
                }
                int byteOffset = bitOffset / 8;
                int bitsToWrite = Math.Min(16, data.Length * 8 - bitOffset);
                for (int i = 0; i < bitsToWrite; i++)
                {
                    data[byteOffset] |= (byte)((bytes[i / 8] >> (i % 8)) & 1);
                    bitOffset++;
                }
            }
            else //Write scaled integer value
            {
                int scaledValue = (int)(Value * ((1 << CompressedBitCount) - 1));
                int byteOffset = bitOffset / 8;
                int bitsToWrite = Math.Min(CompressedBitCount, data.Length * 8 - bitOffset);
                for (int i = 0; i < bitsToWrite; i++)
                {
                    data[byteOffset] |= (byte)((scaledValue >> i) & 1);
                    bitOffset++;
                }
            }
        }
        public override void ReadBits(byte[]? bytes, ref int bitOffset)
        {
            if (bytes is null)
                return;

            if (CompressedBitCount == 32) //Read raw float
            {
                Span<byte> floatBytes = stackalloc byte[4];
                int bitsToRead = Math.Min(32, bytes.Length * 8 - bitOffset);
                for (int i = 0; i < bitsToRead; i++)
                {
                    int byteOffset = bitOffset / 8;
                    floatBytes[i / 8] |= (byte)(((bytes[byteOffset] >> (bitOffset % 8)) & 1) << (i % 8));
                    bitOffset++;
                }
                Value = BitConverter.ToSingle(floatBytes);
            }
            else if (CompressedBitCount == 16) //Read half float
            {
                Span<byte> halfBytes = stackalloc byte[2];
                int bitsToRead = Math.Min(16, bytes.Length * 8 - bitOffset);
                for (int i = 0; i < bitsToRead; i++)
                {
                    int byteOffset = bitOffset / 8;
                    halfBytes[i / 8] |= (byte)(((bytes[byteOffset] >> (bitOffset % 8)) & 1) << (i % 8));
                    bitOffset++;
                }
                Value = (float)(Half)BitConverter.ToUInt16(halfBytes);
            }
            else //Read scaled integer value
            {
                int scaledValue = 0;
                int bitsToRead = Math.Min(CompressedBitCount, bytes.Length * 8 - bitOffset);
                for (int i = 0; i < bitsToRead; i++)
                {
                    int byteOffset = bitOffset / 8;
                    scaledValue |= ((bytes[byteOffset] >> (bitOffset % 8)) & 1) << i;
                    bitOffset++;
                }
                Value = (float)scaledValue / ((1 << CompressedBitCount) - 1);
            }
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
