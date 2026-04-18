namespace XREngine.Extensions
{
    public static class Int64Extensions
    {
        public static Int64 Reverse(this Int64 value)
        {
            return
                ((value >> 56) & 0x000000FF) | ((value & 0x000000FF) << 56) |
                ((value >> 40) & 0x0000FF00) | ((value & 0x0000FF00) << 40) |
                ((value >> 24) & 0x00FF0000) | ((value & 0x00FF0000) << 24) |
                ((value >> 08) & 0xFF000000) | ((value & 0xFF000000) << 08);
        }
        public static Int64 Align(this Int64 value, Int64 align)
        {
            if (align <= 1) return value;
            Int64 temp = value % align;
            if (temp != 0) value += align - temp;
            return value;
        }
        public static Int64 Clamp(this Int64 value, Int64 min, Int64 max)
        {
            return value < min ? min : value > max ? max : value;
        }
        public static Int64 ClampMin(this Int64 value, Int64 min)
        {
            return value <= min ? min : value;
        }
        public static Int64 ClampMax(this Int64 value, Int64 max)
        {
            return value >= max ? max : value;
        }
        public static Int64 RoundDownToEven(this Int64 value)
        {
            return value - (value % 2);
        }
        public static Int64 RoundUpToEven(this Int64 value)
        {
            return value + (value % 2);
        }
        public static int CountBits(this Int64 b)
        {
            int count = 0;
            for (int i = 0; i < 64; i++)
                if (((b >> i) & 1) != 0)
                    ++count;
            return count;
        }
    }
}
