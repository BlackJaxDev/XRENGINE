using System.Collections.Concurrent;
using XREngine;

namespace XREngine.Rendering.Physics.Physx
{
    internal static class PhysxObjectLog
    {
        public static void Created(object obj, nint ptr, string? detail = null)
            => Debug.Log(ELogCategory.Physics, "[PhysxObj] + {0} ptr=0x{1:X} {2}", obj.GetType().Name, ptr, detail ?? string.Empty);

        public static void Released(object obj, nint ptr, string? detail = null)
            => Debug.Log(ELogCategory.Physics, "[PhysxObj] - {0} ptr=0x{1:X} {2}", obj.GetType().Name, ptr, detail ?? string.Empty);

        public static void Modified(object obj, nint ptr, string memberName, string? detail = null)
            => Debug.Log(ELogCategory.Physics, "[PhysxObj] ~ {0} ptr=0x{1:X} {2} {3}", obj.GetType().Name, ptr, memberName, detail ?? string.Empty);

        public static void CacheAdded(string cacheName, Type type, nint ptr)
            => Debug.Log(ELogCategory.Physics, "[PhysxCache] + {0} type={1} ptr=0x{2:X}", cacheName, type.Name, ptr);

        public static void CacheRemoved(string cacheName, Type type, nint ptr, bool removed)
            => Debug.Log(ELogCategory.Physics, "[PhysxCache] - {0} type={1} ptr=0x{2:X} removed={3}", cacheName, type.Name, ptr, removed);

        public static void CacheOverwrite(string cacheName, Type type, nint ptr, object? existing)
        {
            if (existing is null)
                return;
            Debug.Log(ELogCategory.Physics, "[PhysxCache] ! overwrite {0} type={1} ptr=0x{2:X} previous={3}", cacheName, type.Name, ptr, existing.GetType().Name);
        }

        public static void AddOrUpdate<T>(ConcurrentDictionary<nint, T> dict, string cacheName, nint ptr, T value) where T : class
        {
            dict.AddOrUpdate(
                ptr,
                _ =>
                {
                    CacheAdded(cacheName, typeof(T), ptr);
                    return value;
                },
                (_, existing) =>
                {
                    if (!ReferenceEquals(existing, value))
                        CacheOverwrite(cacheName, typeof(T), ptr, existing);
                    return value;
                });
        }

        public static bool RemoveIfSame<T>(ConcurrentDictionary<nint, T> dict, string cacheName, nint ptr, T value) where T : class
        {
            bool removed = dict.TryRemove(new KeyValuePair<nint, T>(ptr, value));
            CacheRemoved(cacheName, typeof(T), ptr, removed);
            return removed;
        }
    }
}
