namespace XREngine.Extensions
{
    public static partial class UInt64Extensions
    {
        public static UInt64 Reverse(this UInt64 value)
        {
            return
                ((value >> 56) & 0xFF) | ((value & 0xFF) << 56) |
                ((value >> 40) & 0xFF00) | ((value & 0xFF00) << 40) |
                ((value >> 24) & 0xFF0000) | ((value & 0xFF0000) << 24) |
                ((value >> 8) & 0xFF000000) | ((value & 0xFF000000) << 8);
        }
        public static UInt64 Align(this UInt64 value, UInt64 align)
        {
            if (align <= 1) return value;
            UInt64 temp = value % align;
            if (temp != 0) value += align - temp;
            return value;
        }
        public static UInt64 Clamp(this UInt64 value, UInt64 min, UInt64 max)
        {
            return value < min ? min : value > max ? max : value;
        }
        public static UInt64 ClampMin(this UInt64 value, UInt64 min)
        {
            return value <= min ? min : value;
        }
        public static UInt64 ClampMax(this UInt64 value, UInt64 max)
        {
            return value >= max ? max : value;
        }
        public static UInt64 RoundDownToEven(this UInt64 value)
        {
            return value - (value % 2);
        }
        public static UInt64 RoundUpToEven(this UInt64 value)
        {
            return value + (value % 2);
        }
        public static int CountBits(this UInt64 b)
        {
            int count = 0;
            for (int i = 0; i < 64; i++)
                if (((b >> i) & 1) != 0)
                    ++count;
            return count;
        }
    }
}
