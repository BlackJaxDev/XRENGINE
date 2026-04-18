namespace XREngine.Extensions
{
    public static partial class UInt16Extensions
    {
        public static UInt16 Reverse(this UInt16 value)
        {
            return (UInt16)(((value >> 8) & 0xFF) | (value << 8));
        }
        public static UInt16 Align(this UInt16 value, UInt16 align)
        {
            if (align == 0) return value;
            return (UInt16)((value + align - 1) / align * align);
        }
        public static UInt16 Clamp(this UInt16 value, UInt16 min, UInt16 max)
        {
            return value < min ? min : value > max ? max : value;
        }
        public static UInt16 ClampMin(this UInt16 value, UInt16 min)
        {
            return value <= min ? min : value;
        }
        public static UInt16 ClampMax(this UInt16 value, UInt16 max)
        {
            return value >= max ? max : value;
        }
        public static UInt16 RoundDownToEven(this UInt16 value)
        {
            return (UInt16)(value - (value % 2));
        }
        public static UInt16 RoundUpToEven(this UInt16 value)
        {
            return (UInt16)(value + (value % 2));
        }
        public static int CountBits(this UInt16 b)
        {
            int count = 0;
            for (int i = 0; i < 16; i++)
                if (((b >> i) & 1) != 0)
                    ++count;
            return count;
        }
    }
}
