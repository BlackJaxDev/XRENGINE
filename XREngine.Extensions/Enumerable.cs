using System.Collections.Concurrent;
using System.Diagnostics;

namespace Extensions
{
    public static class EnumerableExtension
    {
        //public static IEnumerable<T> AsThreadSafeEnumerable<T>(this IEnumerable<T> enumerable, ReaderWriterLockSlim rwlock)
        //{
        //    return new ThreadSafeEnumerable<T>(enumerable, rwlock);
        //}
        public static IEnumerable<TResult> SelectEvery<TElement, TResult>(
            this IEnumerable<TElement> source,
            int count,
            Func<List<TElement>, TResult> formatter)
        {
            return source.Split(count).Select(arg => formatter(arg.ToList()));
        }
        public static IEnumerable<IEnumerable<T>> Split<T>(this IEnumerable<T> source, int size)
        {
            var i = 0;
            return
                from element in source
                group element by i++ / size into splitGroups
                select splitGroups.AsEnumerable();
        }
        public static void ForEach<T>(this IEnumerable<T> enumeration, Action<T> action)
        {
            try
            {
                if (enumeration != null && action != null)
                    foreach (T item in enumeration)
                        action(item);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
        //public static void ForEachParallel<T>(this IEnumerable<T> enumeration, Action<T> action)
        //{
        //    try
        //    {
        //        if (enumeration != null && action != null)
        //            Parallel.ForEach(enumeration, action);
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine(ex.ToString());
        //    }
        //}
        public static void ForEachParallel<T>(this IEnumerable<T> array, Action<T, ParallelLoopState> action)
            => ForEachParallel(array, action, CancellationToken.None);
        public static void ForEachParallel<T>(this IEnumerable<T> array, Action<T, ParallelLoopState> action, CancellationToken cancellationToken)
        {
            try
            {
                if (array != null && action != null)
                {
                    OrderablePartitioner<T> rangePartitioner = Partitioner.Create(array);
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
        public static void ForEachParallel<T>(this IEnumerable<T> array, Action<T> action)
            => ForEachParallel(array, action, CancellationToken.None);
        public static void ForEachParallel<T>(this IEnumerable<T> array, Action<T> action, CancellationToken cancellationToken)
        {
            try
            {
                if (array != null && action != null)
                {
                    OrderablePartitioner<T> rangePartitioner = Partitioner.Create(array);
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
