using System.Buffers;
using System.Numerics;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Driver;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Build;

// Driver triangle sections, and the triangle order that the runtime decodes.
public static partial class GaussianCacheBuilder
{
    /// <summary>Encodes all triangles of a driver level as one section, for chunk 0.</summary>
    /// <param name="stored">The triangle list that the runtime decodes from the section.</param>
    private static GaussianCacheSection EncodeWholeDriverLevel(DriverHierarchy drivers, int level, IMeshOptimizerBackend meshopt, out uint[] stored)
    {
        uint[] optimized = OptimizeLevel(drivers, level, meshopt);
        int triangleCount = optimized.Length / 3;
        GaussianCacheSection section = EncodeIndices(optimized, level, 0, triangleCount, drivers.CumulativeVertexCounts[level], meshopt);
        stored = new uint[optimized.Length];
        DecodeIndices(section, stored, meshopt);
        return section;
    }

    /// <summary>Appends the triangles of a driver level, split at triangle ranges when they do not fit in one chunk.</summary>
    /// <returns>The triangle list that the runtime decodes from the sections.</returns>
    private static uint[] AppendDriverIndices(ChunkPlanner planner, DriverHierarchy drivers, int level, IMeshOptimizerBackend meshopt)
    {
        uint[] optimized = OptimizeLevel(drivers, level, meshopt);
        int vertexCount = drivers.CumulativeVertexCounts[level];
        List<(int First, int Count, GaussianCacheSection Section)> parts = planner.AppendSplit(
            optimized.Length / 3,
            (first, count) => EncodeIndices(optimized, level, first, count, vertexCount, meshopt),
            $"driver level {level} triangles");

        uint[] stored = new uint[optimized.Length];
        foreach ((int first, int count, GaussianCacheSection section) in parts)
            DecodeIndices(section, stored.AsSpan(first * 3, count * 3), meshopt);
        return stored;
    }

    /// <summary>Puts the triangles of a level in vertex cache order. Each triangle keeps its corner order.</summary>
    private static uint[] OptimizeLevel(DriverHierarchy drivers, int level, IMeshOptimizerBackend meshopt)
    {
        uint[] source = drivers.LevelTriangleIndices[level];
        uint[] optimized = new uint[source.Length];
        meshopt.OptimizeVertexCache(optimized, source, drivers.CumulativeVertexCounts[level]);
        return optimized;
    }

    private static GaussianCacheSection EncodeIndices(uint[] indices, int level, int firstTriangle, int triangleCount, int vertexCount, IMeshOptimizerBackend meshopt)
    {
        ReadOnlySpan<uint> range = indices.AsSpan(firstTriangle * 3, triangleCount * 3);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(meshopt.EncodeIndexBufferBound(range.Length, vertexCount));
        try
        {
            int size = meshopt.EncodeIndexBuffer(buffer, range);
            return new GaussianCacheSection(
                GaussianCacheSectionKind.DriverIndices, (byte)level, 0, 0, firstTriangle, triangleCount, 0, 0, buffer.AsSpan(0, size).ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Decodes a triangle section as the runtime does. The index codec keeps the triangle order but can turn the corners
    /// of a triangle, so the decoded list is the list that the bindings must use.
    /// </summary>
    private static void DecodeIndices(GaussianCacheSection section, Span<uint> destination, IMeshOptimizerBackend meshopt)
        => meshopt.DecodeIndexBuffer(destination, section.Payload.Span);

    /// <summary>
    /// Returns, for each triangle of the original list, its index in the stored list and how many corner positions the
    /// codec turned it by: 0, 1 for (b, c, a), or 2 for (c, a, b).
    /// </summary>
    /// <exception cref="InvalidOperationException">The stored list does not hold the triangles of the original list.</exception>
    private static (uint Triangle, byte Turn)[] MapTriangles(uint[] original, uint[] stored)
    {
        int count = original.Length / 3;
        if (stored.Length != original.Length)
            throw new InvalidOperationException("The decoded driver triangle list has another length than the source list.");

        Dictionary<(uint, uint, uint), Queue<int>> byShape = new(count);
        for (int triangle = 0; triangle < count; triangle++)
        {
            (uint, uint, uint) key = SmallestRotation(original, triangle);
            if (!byShape.TryGetValue(key, out Queue<int>? queue))
                byShape.Add(key, queue = new Queue<int>(1));
            queue.Enqueue(triangle);
        }

        (uint, byte)[] map = new (uint, byte)[count];
        for (int position = 0; position < count; position++)
        {
            if (!byShape.TryGetValue(SmallestRotation(stored, position), out Queue<int>? queue) || queue.Count == 0)
                throw new InvalidOperationException($"The decoded driver triangle {position} is not a triangle of the source list.");

            int triangle = queue.Dequeue();
            uint a = original[triangle * 3];
            uint b = original[triangle * 3 + 1];
            uint c = original[triangle * 3 + 2];
            uint first = stored[position * 3];
            byte turn = first == a ? (byte)0 : first == b ? (byte)1 : (byte)2;
            map[triangle] = ((uint)position, turn);
        }

        return map;
    }

    private static (uint, uint, uint) SmallestRotation(uint[] indices, int triangle)
    {
        uint a = indices[triangle * 3];
        uint b = indices[triangle * 3 + 1];
        uint c = indices[triangle * 3 + 2];
        if (a <= b && a <= c)
            return (a, b, c);
        return b <= c ? (b, c, a) : (c, a, b);
    }

    /// <summary>
    /// Moves a binding to the stored triangle. When the codec turned the corners, the plane coordinates change to the
    /// new corner order, and the local rotation changes so that <c>frame × LocalRotation</c> at the rest pose stays the same.
    /// </summary>
    private static SplatBinding MoveBinding(SplatBinding binding, (uint Triangle, byte Turn) stored, uint[] original, Vector3[] rest)
    {
        if (stored.Turn == 0)
            return binding with { Triangle = stored.Triangle };

        int offset = (int)binding.Triangle * 3;
        Vector3 a = rest[original[offset]];
        Vector3 b = rest[original[offset + 1]];
        Vector3 c = rest[original[offset + 2]];
        double w = 1.0 - binding.U - binding.V;

        // The barycentric weights of the corners stay with the corners: (w, u, v) for (a, b, c).
        (float u, float v, Vector3 a2, Vector3 b2, Vector3 c2) = stored.Turn == 1
            ? (binding.V, (float)w, b, c, a)
            : ((float)w, binding.U, c, a, b);

        Quaternion local = binding.LocalRotation;
        if (TriangleFrame.TryCompute(a, b, c, out Quaternion frame) && TriangleFrame.TryCompute(a2, b2, c2, out Quaternion turnedFrame))
            local = Quaternion.Normalize(Quaternion.Inverse(turnedFrame) * frame * local);

        return new SplatBinding
        {
            Triangle = stored.Triangle,
            U = u,
            V = v,
            H = binding.H,
            LocalRotation = local,
        };
    }
}
