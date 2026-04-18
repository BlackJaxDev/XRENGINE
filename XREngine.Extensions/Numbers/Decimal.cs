namespace XREngine.Extensions
{
    public static class DecimalExtensions
    {
        public static Decimal Clamp(this Decimal value, Decimal min, Decimal max)
        {
            return value < min ? min : value > max ? max : value;
        }
        public static Decimal ClampMin(this Decimal value, Decimal min)
        {
            return value <= min ? min : value;
        }
        public static Decimal ClampMax(this Decimal value, Decimal max)
        {
            return value >= max ? max : value;
        }
        public static Decimal RoundDownToEven(this Decimal value)
        {
            return value - (value % 2);
        }
        public static Decimal RoundUpToEven(this Decimal value)
        {
            return value + (value % 2);
        }
    }
}
