using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace XREngine.Extensions
{
    public static partial class ArrayExtensions
    {
        public delegate bool DelTryConvert(object? obj, out string result);
        public delegate bool DelTryConvert<T>(T? obj, out string result);
        public delegate string DelConvert(object? obj);
        public delegate string DelConvert<T>(T? obj);
        public delegate string DelConvertIndexed(object? obj, int index);
        public delegate string DelConvertIndexed<T>(T? obj, int index);
        
        /*
        return list.Cast<object>().Aggregate(new StringBuilder(),
                (builder, obj) => builder.Append(separator + elementToString(obj)),
                (builder) => 
                {
                    if (list.Count == 0)
                        return builder.ToString();
                    else
                        return builder.Remove(0, separator.Length).ToString();
                });
        */

        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="elementToString">The method for converting individual array elements to strings.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringList<T>(this IList<T> a, string separator, DelConvert<T> elementToString)
        {
            StringBuilder builder = new();

            for (int i = 0; i < a.Count; ++i)
                builder.Append(elementToString(a[i]) + separator);

            int sepLen = separator.Length;
            if (builder.Length >= sepLen)
                builder.Remove(builder.Length - sepLen, sepLen);

            return builder.ToString();
        }
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="elementToString">The method for converting individual array elements to strings.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringList<T>(this IList<T> a, DelConvert<T> elementToString)
        {
            StringBuilder builder = new();

            for (int i = 0; i < a.Count; ++i)
                builder.Append(elementToString(a[i]));

            return builder.ToString();
        }
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="lastSeparator">The separator to use in the list between the last two elements.</param>
        /// <param name="elementToString">The method for converting individual array elements to strings.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringList<T>(this IList<T> a, string separator, string lastSeparator, DelConvert<T> elementToString)
        {
            int countMin2 = a.Count - 2;

            StringBuilder builder = new();

            string sep = separator;
            for (int i = 0; i < countMin2; ++i)
                builder.Append(elementToString(a[i]) + sep);

            if (countMin2 >= 0)
                builder.Append(elementToString(a[countMin2]) + lastSeparator);

            ++countMin2;
            if (countMin2 >= 0)
                builder.Append(elementToString(a[countMin2]));

            return builder.ToString();
        }
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="elementToString">The method for converting individual array elements to strings.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringList<T>(this IList<T> a, string separator, DelConvertIndexed<T> elementToString)
        {
            StringBuilder builder = new();

            for (int i = 0; i < a.Count; ++i)
                builder.Append(elementToString(a[i], i) + separator);

            int sepLen = separator.Length;
            if (builder.Length >= sepLen)
                builder.Remove(builder.Length - sepLen, sepLen);

            return builder.ToString();
        }
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="elementToString">The method for converting individual array elements to strings.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringList<T>(this IList<T> a, string separator, string lastSeparator, DelConvertIndexed<T> elementToString)
        {
            int countMin2 = a.Count - 2;

            StringBuilder builder = new();

            string sep = separator;
            for (int i = 0; i < a.Count; ++i)
                builder.Append(elementToString(a[i], i) + sep);

            if (countMin2 >= 0)
                builder.Append(elementToString(a[countMin2], countMin2) + lastSeparator);

            ++countMin2;
            if (countMin2 >= 0)
                builder.Append(elementToString(a[countMin2], countMin2));

            return builder.ToString();
        }
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringList<T>(this IList<T> a, string separator)
            => ToStringList(a, separator, o => o?.ToString() ?? string.Empty);
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="lastSeparator">The separator to use in the list between the last two elements.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringList<T>(this IList<T> a, string separator, string lastSeparator)
            => ToStringList(a, separator, lastSeparator, o => o?.ToString() ?? string.Empty);

        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="elementToString">The method for converting individual array elements to strings.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringListGeneric(this IList a, string separator, DelConvert elementToString)
        {
            StringBuilder builder = new();

            for (int i = 0; i < a.Count; ++i)
                builder.Append(elementToString(a[i]) + separator);

            int sepLen = separator.Length;
            if (builder.Length >= sepLen)
                builder.Remove(builder.Length - sepLen, sepLen);

            return builder.ToString();
        }
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="lastSeparator">The separator to use in the list between the last two elements.</param>
        /// <param name="elementToString">The method for converting individual array elements to strings.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringListGeneric(this IList a, string separator, string lastSeparator, DelConvert elementToString)
        {
            int countMin2 = a.Count - 2;

            StringBuilder builder = new();

            string sep = separator;
            for (int i = 0; i < countMin2; ++i)
                builder.Append(elementToString(a[i]) + sep);

            if (countMin2 >= 0)
                builder.Append(elementToString(a[countMin2]) + lastSeparator);

            ++countMin2;
            if (countMin2 >= 0)
                builder.Append(elementToString(a[countMin2]));

            return builder.ToString();
        }
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="lastSeparator">The separator to use in the list between the last two elements.</param>
        /// <param name="elementToString">The method for converting individual array elements to strings.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static bool ToStringListGenericChecked(this IList a, string separator, string lastSeparator, DelTryConvert elementToString, out string? str)
        {
            int countMin2 = a.Count - 2;

            StringBuilder builder = new();

            string sep = separator;
            for (int i = 0; i < countMin2; ++i)
            {
                if (elementToString(a[i], out string val))
                    builder.Append(val + sep);
                else
                {
                    str = null;
                    return false;
                }
            }

            if (countMin2 >= 0)
            {
                if (elementToString(a[countMin2], out string val))
                    builder.Append(val + lastSeparator);
                else
                {
                    str = null;
                    return false;
                }
            }

            ++countMin2;
            if (countMin2 >= 0)
            {
                if (elementToString(a[countMin2], out string val))
                    builder.Append(val);
                else
                {
                    str = null;
                    return false;
                }
            }

            str = builder.ToString();
            return true;
        }

        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="elementToString">The method for converting individual array elements to strings.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringListGeneric(this IList a, string separator, DelConvertIndexed elementToString)
        {
            StringBuilder builder = new();

            for (int i = 0; i < a.Count; ++i)
                builder.Append(elementToString(a[i], i) + separator);

            int sepLen = separator.Length;
            if (builder.Length >= sepLen)
                builder.Remove(builder.Length - sepLen, sepLen);

            return builder.ToString();
        }
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="elementToString">The method for converting individual array elements to strings.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringListGeneric(this IList a, string separator, string lastSeparator, DelConvertIndexed elementToString)
        {
            int countMin2 = a.Count - 2;

            StringBuilder builder = new();

            string sep = separator;
            for (int i = 0; i < a.Count; ++i)
                builder.Append(elementToString(a[i], i) + sep);

            if (countMin2 >= 0)
                builder.Append(elementToString(a[countMin2], countMin2) + lastSeparator);

            ++countMin2;
            if (countMin2 >= 0)
                builder.Append(elementToString(a[countMin2], countMin2));

            return builder.ToString();
        }
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringListGeneric(this IList a, string separator)
            => ToStringListGeneric(a, separator, o => o?.ToString() ?? string.Empty);
        /// <summary>
        /// Converts the elements of an array into a well-formatted list.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="a">The array to format into a list.</param>
        /// <param name="separator">The separator to use to separate items in the list.</param>
        /// <param name="lastSeparator">The separator to use in the list between the last two elements.</param>
        /// <returns>A list of the elements in the array as a string.</returns>
        public static string ToStringListGeneric(this IList a, string separator, string lastSeparator)
            => ToStringListGeneric(a, separator, lastSeparator, o => o?.ToString() ?? string.Empty);
        /// <summary>
        /// Returns true if index >= 0 && index is less than length.
        /// Use this so you don't have to write that every time.
        /// </summary>
        public static bool IndexInRangeIList(this IList a, int value)
            => a is not null && value >= 0 && value < a.Count;
        /// <summary>
        /// Returns true if index >= 0 && index is less than length.
        /// Use this so you don't have to write that every time.
        /// </summary>
        public static bool IndexInRangeArray(this Array a, int value)
            => a is not null && value >= 0 && value < a.Length;
        /// <summary>
        /// Returns true if index >= 0 && index is less than length.
        /// Use this so you don't have to write that every time.
        /// </summary>
        public static bool IndexInRangeIListT<T>(this IList<T> a, int value)
            => a is not null && value >= 0 && value < a.Count;
        /// <summary>
        /// Returns true if index >= 0 && index is less than length.
        /// Use this so you don't have to write that every time.
        /// </summary>
        public static bool IndexInRangeArrayT<T>(this T[] a, int value)
            => a is not null && value >= 0 && value < a.Length;

        public static int[] FindAllOccurences<T>(this IList<T> a, T o) where T : IEquatable<T>
        {
            List<int> l = [];
            int i = 0;
            foreach (T x in a)
            {
                if (x.Equals(o))
                    l.Add(i);
                i++;
            }
            return [.. l];
        }

        public static int[] FindAllMatchIndices<T>(this IList<T> a, Predicate<T> predicate)
        {
            List<int> list = new(a.Count);
            for (int i = 0; i < a.Count; ++i)
                if (predicate(a[i]))
                    list.Add(i);
            return [.. list];
        }

        public static int IndexOf<T>(this T[] a, T value)
            => Array.IndexOf(a, value);
        
        public static bool Contains(this string[] a, string value, StringComparison comp)
        {
            for (int i = 0; i < a.Length; ++i)
                if (string.Equals(a[i], value, comp))
                    return true;
            return false;
        }
        public static T[] SubArray<T>(this T[] data, int index, int length)
        {
            T[] result = new T[length];
            Array.Copy(data, index, result, 0, length);
            return result;
        }
        public static T[] Append<T>(this T[] data, T[] appended)
        {
            T[] final = new T[data.Length + appended.Length];
            data.CopyTo(final, 0);
            appended.CopyTo(final, data.Length);
            return final;
        }
        public static T[] Resize<T>(this T[] data, int newSize)
        {
            Array.Resize(ref data, newSize);
            return data;
        }
        public static T[] Fill<T>(this T[] array, T value)
        {
            for (int i = 0; i < array.Length; i++)
                array[i] = value;

            return array;
        }
        public static T[] Fill<T>(this T[] array, Func<int, T> factory)
        {
            if (factory is null)
                return array;

            for (int i = 0; i < array.Length; i++)
                array[i] = factory(i);

            return array;
        }
        /// <summary>
        /// Fills the array with a newly constructed item for every index.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="array"></param>
        /// <returns></returns>
        public static T[] Fill<T>(this T[] array) where T : new()
        {
            for (int i = 0; i < array.Length; i++)
                array[i] = new T();

            return array;
        }
        public static void ForEach<T>(this T[] array, Action<T> action)
        {
            try
            {
                if (array != null && action != null)
                    Array.ForEach(array, action);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
        public static void ForEachParallelArray<T>(this T[] array, Action<T, ParallelLoopState> action)
            => ForEachParallelArray(array, action, CancellationToken.None);
        public static void ForEachParallelArray<T>(this T[] array, Action<T, ParallelLoopState> action, CancellationToken cancellationToken)
        {
            try
            {
                if (array != null && action != null)
                {
                    OrderablePartitioner<T> rangePartitioner = Partitioner.Create(array, true);
                    ParallelOptions options = new()
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        CancellationToken = cancellationToken,
                    };
                    Parallel.ForEach(rangePartitioner, options, action);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
        public static void ForEachParallelArray<T>(this T[] array, Action<T> action)
            => ForEachParallelArray(array, action, CancellationToken.None);
        public static void ForEachParallelArray<T>(this T[] array, Action<T> action, CancellationToken cancellationToken)
        {
            try
            {
                if (array != null && action != null)
                {
                    OrderablePartitioner<T> rangePartitioner = Partitioner.Create(array, true);
                    ParallelOptions options = new()
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        CancellationToken = cancellationToken,
                    };
                    Parallel.ForEach(rangePartitioner, options, action);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
    }
}
