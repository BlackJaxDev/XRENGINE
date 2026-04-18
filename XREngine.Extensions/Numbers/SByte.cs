namespace XREngine.Extensions
{
    public static partial class SByteExtensions
    {
        /// <summary>
        /// Finds the first differing bit starting from the lsb.
        /// </summary>
        /// <returns>The index of the bit.</returns>
        public static int CompareBits(this sbyte b1, sbyte b2)
        {
            for (int i = 8, b = 0x80; i-- != 0; b >>= 1)
                if ((b1 & b) != (b2 & b))
                    return i;
            return 0;
        }
        public static int CountBits(this sbyte b)
        {
            int count = 0;
            for (int i = 0; i < 8; i++)
                if (((b >> i) & 1) != 0)
                    ++count;
            return count;
        }
        public static sbyte Clamp(this sbyte value, sbyte min, sbyte max)
        {
            return value < min ? min : value > max ? max : value;
        }
        public static sbyte ClampMin(this sbyte value, sbyte min)
        {
            return value <= min ? min : value;
        }
        public static sbyte ClampMax(this sbyte value, sbyte max)
        {
            return value >= max ? max : value;
        }
        public static sbyte RoundDownToEven(this sbyte value)
        {
            return (sbyte)(value - (value % 2));
        }
        public static sbyte RoundUpToEven(this sbyte value)
        {
            return (sbyte)(value + (value % 2));
        }
    }
}
