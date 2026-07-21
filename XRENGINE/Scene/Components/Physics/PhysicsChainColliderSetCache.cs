using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Components;

/// <summary>
/// Per-world collision-shape cache. Hash collisions are resolved by exact
/// content comparison, and identity is computed only at structural boundaries.
/// </summary>
internal sealed class PhysicsChainColliderSetCache
{
    private static readonly ConditionalWeakTable<PhysicsChainWorld, PhysicsChainColliderSetCache> WorldCaches = [];
    private readonly Dictionary<ulong, List<PhysicsChainColliderSet>> _setsByHash = [];
    private long _nextStableId;
    private int _totalShapeCount;
    private long _lookupCount;
    private long _deduplicatedLookupCount;

    public int UniqueSetCount { get; private set; }

    public static PhysicsChainColliderSetCache ForWorld(PhysicsChainWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return WorldCaches.GetValue(world, static _ => new PhysicsChainColliderSetCache());
    }

    public PhysicsChainColliderSet GetOrAdd(ReadOnlySpan<PhysicsChainColliderShape> shapes)
    {
        ++_lookupCount;
        ulong hash = CalculateHash(shapes);
        if (_setsByHash.TryGetValue(hash, out List<PhysicsChainColliderSet>? bucket))
        {
            for (int i = 0; i < bucket.Count; ++i)
            {
                PhysicsChainColliderSet existing = bucket[i];
                if (existing.ContentEquals(shapes))
                {
                    ++_deduplicatedLookupCount;
                    return existing;
                }
            }
        }
        else
        {
            bucket = [];
            _setsByHash.Add(hash, bucket);
        }

        long stableId = checked(++_nextStableId);
        var created = new PhysicsChainColliderSet(stableId, shapes.ToArray(), hash);
        bucket.Add(created);
        ++UniqueSetCount;
        _totalShapeCount = checked(_totalShapeCount + shapes.Length);
        return created;
    }

    public PhysicsChainColliderSetCacheSnapshot GetSnapshot()
    {
        int uniqueCount = UniqueSetCount;
        return new PhysicsChainColliderSetCacheSnapshot(
            uniqueCount,
            uniqueCount,
            _totalShapeCount,
            checked((long)_totalShapeCount * Marshal.SizeOf<PhysicsChainColliderShape>()),
            _lookupCount,
            _deduplicatedLookupCount);
    }

    private static ulong CalculateHash(ReadOnlySpan<PhysicsChainColliderShape> shapes)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        AddUInt(ref hash, unchecked((uint)shapes.Length));

        for (int i = 0; i < shapes.Length; ++i)
        {
            PhysicsChainColliderShape shape = shapes[i];
            AddUInt(ref hash, (uint)shape.Kind);
            AddVector(ref hash, shape.LocalCenter);
            AddVector(ref hash, shape.Axis);
            AddVector(ref hash, shape.HalfExtents);
            AddUInt(ref hash, BitConverter.SingleToUInt32Bits(shape.Radius));
            AddUInt(ref hash, BitConverter.SingleToUInt32Bits(shape.InverseAxisLengthSquared));
        }

        return hash;

        static void AddVector(ref ulong value, Vector3 vector)
        {
            AddUInt(ref value, BitConverter.SingleToUInt32Bits(vector.X));
            AddUInt(ref value, BitConverter.SingleToUInt32Bits(vector.Y));
            AddUInt(ref value, BitConverter.SingleToUInt32Bits(vector.Z));
        }

        static void AddUInt(ref ulong value, uint item)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                value ^= (byte)(item >> shift);
                value *= prime;
            }
        }
    }
}
