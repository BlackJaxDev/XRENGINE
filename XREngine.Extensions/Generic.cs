using System.Runtime.InteropServices;

namespace XREngine.Extensions
{
    public static class GenericExtensions
    {
        /// <summary>
        /// Clamps the value between min and max values.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        public static T Clamp<T>(this T value, T min, T max) where T : IComparable
            => value.CompareTo(min) < 0 ? min : value.CompareTo(max) > 0 ? max : value;
        /// <summary>
        /// Converts an unmanaged structure to an array of bytes.
        /// </summary>
        public static unsafe byte[] ToByteArray<T>(this T data) where T : unmanaged
        {
            byte[] dataArr = new byte[sizeof(T)];
            void* addr = &data;
            Marshal.Copy((IntPtr)addr, dataArr, 0, dataArr.Length);
            return dataArr;
        }
        /// <summary>
        /// Converts an array of bytes to an unmanaged structure.
        /// </summary>
        public static unsafe T ToStruct<T>(this byte[] data) where T : unmanaged
        {
            T value = new();
            void* addr = &value;
            Marshal.Copy(data, 0, (IntPtr)addr, Math.Min(data.Length, sizeof(T)));
            return value;
        }
        public static bool IsBoxed<T>(this T value) =>
            (typeof(T).IsInterface || typeof(T) == typeof(object)) &&
            value != null &&
            value.GetType().IsValueType;
    }
}
