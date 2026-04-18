namespace XREngine.Extensions
{
    public static class SingleExtensions
    {
        public const Single ZeroTolerance = 1e-6f;

        public static bool IsZero(this Single value, Single tolerance = ZeroTolerance)
            => Math.Abs(value) < tolerance;

        public static bool EqualTo(this Single value, Single other, Single tolerance = ZeroTolerance)
            => Math.Abs(other - value) < tolerance;

        public static unsafe Single Reverse(this Single value)
        {
            *(uint*)(&value) = ((uint*)&value)->Reverse();
            return value;
        }
        public static Single Clamp(this Single value, Single min, Single max)
            => value <= min ? min : value >= max ? max : value;
        public static Single ClampMin(this Single value, Single min)
            => value <= min ? min : value;
        public static Single ClampMax(this Single value, Single max)
            => value >= max ? max : value;
        /// <summary>
        /// Remaps values outside of a range into the first multiple of that range.
        /// When it comes to signed numbers, negative is farther from zero.
        /// For example, -128 (0xFF) vs 127 (0x7F).
        /// Because of this, the max value is non-inclusive while the min value is.
        /// </summary>
        public static Single RemapToRange(this Single value, Single min, Single max)
        {
            //Check if the value is already in the range
            if (value < max && value >= min)
                return value;

            //Get the distance between max and min
            Single range = max - min;

            //First figure out how many multiples of the range there are.
            //Dividing the value by the range and cutting off the decimal places
            //will return the number of multiples of whole ranges in the value.
            //Those multiples need to be subtracted out.
            value -= range * (int)(value / range);

            //Now the value is in the range of +range to -range.
            //The value needs to be within +(range/2) to -(range/2).
            value += value > max ? -range : value < min ? range : 0;

            //Max value is non-inclusive
            if (value == max)
                value = min;

            return value;
        }
        public static Single FeetToMeters(this Single value)
            => value * 0.3048f;
        /// <summary>
        /// Positive is to a bigger unit (ex Meters to Kilometers is 2 steps)
        /// Negative is to a smaller unit (ex Meters to Centimeters is -2 steps)
        /// </summary>
        public static Single MetricUnitScale(this Single value, int steps)
        {
            if (steps == 0)
                return value;
            float scale;
            if (steps > 0)
                scale = (float)Math.Pow(10.0f, steps);
            else
                scale = (float)Math.Pow(0.1f, -steps);
            return value * scale;
        }
        public static Single MetersToFeet(this Single value)
            => value * 3.280839895f;
        public static Single FeetToYards(this Single value)
            => value * 0.33333333333f;
        public static Single YardsToFeet(this Single value)
            => value * 3.0f;
        public static Single MilesToKilometers(this Single value)
            => value * 1.60934f;
        public static Single KilometersToMiles(this Single value)
            => value * 0.6213727366498068f;
        public static Single InchesToMeters(this Single value)
            => value * 0.0254f;
        public static Single MetersToInches(this Single value)
            => value * 39.3701f;
        /// <summary>
        /// Converts a float from the range 0.0-1.0 to 0-255.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static byte ToByte(this Single value)
        {
            //Casting a decimal to an integer floors the value
            //So multiply by 256 to get the proper value.
            //1.0f is the edge case, so clamp to ensure no rounding issues 
            //and handle edge case appropriately
            float f2 = value.Clamp(0.0f, 1.0f);
            return (byte)Math.Floor(f2 == 1.0f ? 255.0f : f2 * 256.0f);
        }
        /// <summary>
        /// Converts a float from the range 0.0-1.0 to 0-65535.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static ushort ToUShort(this Single value)
        {
            float f2 = value.Clamp(0.0f, 1.0f);
            return (byte)Math.Floor(f2 == 1.0f ? 65535.0f : f2 * 65536.0f);
        }
        /// <summary>
        /// Converts a float from the range 0.0-1.0 to 0-4294967295.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static uint ToUInt(this Single value)
        {
            float f2 = value.Clamp(0.0f, 1.0f);
            return (byte)Math.Floor(f2 == 1.0f ? 4294967295.0f : f2 * 4294967296.0f);
        }
        public static Single RoundedToNearestMultiple(this Single value, Single multiple)
        {
            double nearestMultiple = Math.Round((value / multiple), MidpointRounding.AwayFromZero) * multiple;
            return (float)nearestMultiple;
        }
        public static Single RoundedToNearest(this Single value, Single intervalBias, Single interval)
        {
            float numIntervals = (value - intervalBias) / interval;
            
            int floor = (int)numIntervals;

            //Calculate ceiling away from zero
            int ceil = (int)(numIntervals < 0 ? numIntervals - 1.0f : numIntervals + 1.0f);

            float ceilDist = Math.Abs(ceil - numIntervals);
            float floorDist = Math.Abs(numIntervals - floor);
            float val = floorDist < ceilDist ? floor : ceil;
            return val * interval + intervalBias;
        }
        public static Single Rounded(this Single value, int decimalPlaces, MidpointRounding roundingMode = MidpointRounding.AwayFromZero)
            => (Single)Math.Round(value, decimalPlaces, roundingMode);
    }
}
