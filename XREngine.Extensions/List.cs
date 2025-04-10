using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Extensions
{
    public static partial class ListExtension
    {
        public static Type? DetermineElementType(this Type listType)
        {
            Type? elementType = listType.GetElementType();
            if (elementType != null)
                return elementType;
            if (listType.IsGenericType && listType.GenericTypeArguments.Length == 1)
                return listType.GenericTypeArguments[0];
            return null;
        }
        public static Type? DetermineElementType(this IList list)
        {
            Type listType = list.GetType();
            Type? elementType = listType.GetElementType();
            if (elementType != null)
                return elementType;
            if (listType.IsGenericType && listType.GenericTypeArguments.Length == 1)
                return listType.GenericTypeArguments[0];
            return null;
        }
        public static Type? DetermineKeyType(this IDictionary dic)
        {
            Type listType = dic.GetType();
            if (listType.IsGenericType)
                return listType.GenericTypeArguments[0];
            return null;
        }
        public static Type? DetermineValueType(this IDictionary dic)
        {
            Type listType = dic.GetType();
            if (listType.IsGenericType)
                return listType.GenericTypeArguments[1];
            return null;
        }
        ///// <summary>
        ///// Returns true if index >= 0 && index<count
        ///// Use this so you don't have to write that every time.
        ///// </summary>
        //public static bool IndexInRange(this IList a, int value)
        //{
        //    return value >= 0 && value < a.Count;
        //}
        public static void RadixSort(this List<uint> array)
        {
            //int id = Engine.StartTimer();
            Queue<uint>[] buckets = new Queue<uint>[15];
            for (int i = 0; i < 0xF; i++)
                buckets[i] = new Queue<uint>();
            for (int i = 60; i >= 0; i -= 4)
            {
                foreach (uint value in array)
                    buckets[(int)((value >> i) & 0xF)].Enqueue(value);
                int x = 0;
                foreach (Queue<uint> bucket in buckets)
                    while (bucket.Count > 0)
                        array[x++] = bucket.Dequeue();
            }
            //float seconds = Engine.EndTimer(id);
            //Engine.DebugPrint("Radix Sort took " + seconds + " seconds.");
        }
        public static void AddRange<T>(this IList<T> list, params T[] values)
        {
            foreach (var item in values)
                list.Add(item);
        }
        public static void AddRange<T>(this IList<T> list, IEnumerable<T> values)
        {
            foreach (var item in values)
                list.Add(item);
        }
        public static IList<T> Fill<T>(this IList<T> list, T value)
        {
            for (int i = 0; i < list.Count; i++)
                list[i] = value;

            return list;
        }
        public static IList<T> Fill<T>(this IList<T> list, Func<int, T> factory)
        {
            if (factory is null)
                return list;

            for (int i = 0; i < list.Count; i++)
                list[i] = factory(i);

            return list;
        }
        public static void ForEachParallelIList<T>(this IList<T> array, Action<T, ParallelLoopState> action)
            => ForEachParallelIList(array, action, CancellationToken.None);
        public static void ForEachParallelIList<T>(this IList<T> array, Action<T, ParallelLoopState> action, CancellationToken cancellationToken)
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
        public static void ForEachParallelIList<T>(this IList<T> array, Action<T> action)
            => ForEachParallelIList(array, action, CancellationToken.None);
        public static void ForEachParallelIList<T>(this IList<T> array, Action<T> action, CancellationToken cancellationToken)
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

        public static bool IndexInRange<T>(this IReadOnlyList<T> a, int value)
            => value >= 0 && value < a.Count;

        public static T? TryGet<T>(this IReadOnlyList<T> list, int index)
            => list.IndexInRange(index) ? list[index] : default;

        public static bool TryGet<T>(this IReadOnlyList<T> list, int index, out T? value)
        {
            if (list.IndexInRange(index))
            {
                value = list[index];
                return true;
            }
            value = default;
            return false;
        }

        public static bool IndexInRange(this Array a, int value)
            => value >= 0 && value < a.Length;

        public static T? TryGet<T>(this T[] list, int index)
            => list.IndexInRangeArrayT(index) ? list[index] : default;

        public static bool TryGet<T>(this T[] list, int index, out T? value)
        {
            if (list.IndexInRangeArrayT(index))
            {
                value = list[index];
                return true;
            }
            value = default;
            return false;
        }
    }
}
