namespace XREngine.Extensions
{
    public static class Int16Extensions
    {
        public static Int16 Reverse(this Int16 value)
        {
            return (Int16)(((value >> 8) & 0xFF) | (value << 8));
        }
        public static Int16 Align(this Int16 value, Int16 align)
        {
            if (align == 0) return value;
            return (Int16)((value + align - 1) / align * align);
        }
        public static Int16 Clamp(this Int16 value, Int16 min, Int16 max)
        {
            return value < min ? min : value > max ? max : value;
        }
        public static Int16 ClampMin(this Int16 value, Int16 min)
        {
            return value <= min ? min : value;
        }
        public static Int16 ClampMax(this Int16 value, Int16 max)
        {
            return value >= max ? max : value;
        }
        public static Int16 RoundDownToEven(this Int16 value)
        {
            return (Int16)(value - (value % 2));
        }
        public static Int16 RoundUpToEven(this Int16 value)
        {
            return (Int16)(value + (value % 2));
        }
        public static int CountBits(this Int16 b)
        {
            int count = 0;
            for (int i = 0; i < 16; i++)
                if (((b >> i) & 1) != 0)
                    ++count;
            return count;
        }
    }
}
