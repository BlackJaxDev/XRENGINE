
namespace XREngine.Core
{
    public static class Utility
    {
        /// <summary>
        /// Multiply this constant by a byte value to convert to normalized float.
        /// </summary>
        public static readonly float ByteToFloat = 1.0f / 255.0f;

        /// <summary>
        /// Swaps the two values with each other.
        /// </summary>
        public static void Swap<T>(ref T value1, ref T value2)
        {
            (value2, value1) = (value1, value2);
        }
    }
}
