namespace XREngine.Extensions
{
    public static partial class UInt32Extensions
    {
        public static UInt32 Reverse(this UInt32 value)
        {
            return ((value >> 24) & 0xFF) | (value << 24) | ((value >> 8) & 0xFF00) | ((value & 0xFF00) << 8);
        }
        public static UInt32 Align(this UInt32 value, UInt32 align)
        {
            if (align <= 1) return value;
            UInt32 temp = value % align;
            if (temp != 0) value += align - temp;
            return value;
        }
        public static UInt32 Clamp(this UInt32 value, UInt32 min, UInt32 max)
        {
            return value < min ? min : value > max ? max : value;
        }
        public static UInt32 ClampMin(this UInt32 value, UInt32 min)
        {
            return value <= min ? min : value;
        }
        public static UInt32 ClampMax(this UInt32 value, UInt32 max)
        {
            return value >= max ? max : value;
        }
        public static UInt32 RoundDownToEven(this UInt32 value)
        {
            return value - (value % 2);
        }
        public static UInt32 RoundUpToEven(this UInt32 value)
        {
            return value + (value % 2);
        }
        public static int CountBits(this UInt32 b)
        {
            int count = 0;
            for (int i = 0; i < 32; i++)
                if (((b >> i) & 1) != 0)
                    ++count;
            return count;
        }
    }
}
