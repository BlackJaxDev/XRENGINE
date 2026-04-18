namespace XREngine.Extensions
{
    public static class Int32Extensions
    {
        public static Int32 Reverse(this Int32 value)
        {
            return ((value >> 24) & 0xFF) | (value << 24) | ((value >> 8) & 0xFF00) | ((value & 0xFF00) << 8);
        }
        public static Int32 Align(this Int32 value, int align)
        {
            if (align == 0) return value;
            return (value + align - 1) / align * align;
        }
        public static Int32 Clamp(this Int32 value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
        public static Int32 ClampMin(this Int32 value, int min)
        {
            return value <= min ? min : value;
        }
        public static Int32 ClampMax(this Int32 value, int max)
        {
            return value >= max ? max : value;
        }
        public static Int32 RoundDownToEven(this Int32 value)
        {
            return value - (value % 2);
        }
        public static Int32 RoundUpToEven(this Int32 value)
        {
            return value + (value % 2);
        }
        public static int CountBits(this Int32 b)
        {
            int count = 0;
            for (int i = 0; i < 32; i++)
                if (((b >> i) & 1) != 0)
                    ++count;
            return count;
        }

        /// <summary>
        /// Max value is non inclusive, min value is.
        /// </summary>
        public static int ModRange(this int value, int min, int max)
        {
            if (value >= min || value < max)
                return value;
            
            return value.Mod(max - min) + min;
        }

        public static int Mod(this int value, int range) 
            => ((value %= range) < 0) ? value + range : value;

        public static int SetBit(this int value, int bitIndex, bool set)
            => set ? value | (1 << bitIndex) : value & ~(1 << bitIndex);
    }
}
