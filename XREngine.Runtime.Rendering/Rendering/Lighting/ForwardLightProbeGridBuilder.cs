using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;

namespace XREngine.Rendering;

internal static class ForwardLightProbeGridBuilder
{
    public static ProbeGridResourceCandidate Build(
        IReadOnlyList<ProbePositionData> positions,
        IReadOnlyList<ProbeParamData> parameters,
        IReadOnlyList<ProbeTetraData>? tetrahedra,
        string cellBufferName,
        string indexBufferName)
    {
        if (positions.Count == 0)
            return new ProbeGridResourceCandidate(null, null, Vector3.Zero, 0.0f, IVector3.Zero);
        if (parameters.Count != positions.Count)
            throw new InvalidOperationException("Probe position and parameter snapshots must have identical layouts.");

        ComputeGridLayout(positions, parameters, out Vector3 origin, out float cellSize, out IVector3 dimensions);
        int cellCount = dimensions.X * dimensions.Y * dimensions.Z;
        var cellLists = new List<int>[cellCount];
        for (int index = 0; index < cellCount; index++)
            cellLists[index] = new List<int>(4);

        if (tetrahedra is { Count: > 0 })
            PopulateTetrahedronCells(positions, tetrahedra, origin, cellSize, dimensions, cellLists);

        var cells = new List<ProbeGridCell>(cellCount);
        var indices = new List<int>();
        for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            List<int> cellTetrahedra = cellLists[cellIndex];
            int offset = indices.Count;
            indices.AddRange(cellTetrahedra);
            int x = cellIndex % dimensions.X;
            int y = (cellIndex / dimensions.X) % dimensions.Y;
            int z = cellIndex / (dimensions.X * dimensions.Y);
            Vector3 center = origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * cellSize;
            List<int>? preferred = CollectPreferredProbeIndices(cellTetrahedra, tetrahedra, positions.Count);
            cells.Add(new ProbeGridCell
            {
                OffsetCount = new IVector4(offset, cellTetrahedra.Count, 0, 0),
                FallbackIndices = ComputeFallbackIndices(center, positions, preferred),
            });
        }

        XRDataBuffer? cellBuffer = null;
        XRDataBuffer? indexBuffer = null;
        try
        {
            cellBuffer = new XRDataBuffer(cellBufferName, EBufferTarget.ShaderStorageBuffer,
                (uint)cells.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeGridCell>(), false, false)
            {
                BindingIndexOverride = 3,
            };
            cellBuffer.SetDataRaw(cells);
            cellBuffer.PushData();

            indexBuffer = new XRDataBuffer(indexBufferName, EBufferTarget.ShaderStorageBuffer,
                (uint)indices.Count, EComponentType.Int, sizeof(int), false, false)
            {
                BindingIndexOverride = 4,
            };
            indexBuffer.SetDataRaw(indices);
            indexBuffer.PushData();
            return new ProbeGridResourceCandidate(cellBuffer, indexBuffer, origin, cellSize, dimensions);
        }
        catch
        {
            ForwardLightProbeInstanceResources.DestroyBuffer(ref cellBuffer);
            ForwardLightProbeInstanceResources.DestroyBuffer(ref indexBuffer);
            throw;
        }
    }

    private static void ComputeGridLayout(
        IReadOnlyList<ProbePositionData> positions,
        IReadOnlyList<ProbeParamData> parameters,
        out Vector3 origin,
        out float cellSize,
        out IVector3 dimensions)
    {
        Vector3 minimum = new(float.MaxValue);
        Vector3 maximum = new(float.MinValue);
        for (int index = 0; index < positions.Count; index++)
        {
            GetInfluenceBounds(positions[index], parameters[index], out Vector3 probeMinimum, out Vector3 probeMaximum);
            minimum = Vector3.Min(minimum, probeMinimum);
            maximum = Vector3.Max(maximum, probeMaximum);
        }

        Vector3 extents = maximum - minimum;
        float maximumExtent = Math.Max(extents.X, Math.Max(extents.Y, extents.Z));
        if (maximumExtent <= 0.0001f)
            maximumExtent = 1.0f;

        const int TargetCellsPerAxis = 16;
        cellSize = maximumExtent / TargetCellsPerAxis;
        origin = minimum;
        Vector3 dimensionsFloat = extents / cellSize + Vector3.One;
        dimensions = IVector3.Min(
            new IVector3(
                Math.Max(1, (int)Math.Ceiling(dimensionsFloat.X)),
                Math.Max(1, (int)Math.Ceiling(dimensionsFloat.Y)),
                Math.Max(1, (int)Math.Ceiling(dimensionsFloat.Z))),
            new IVector3(64, 64, 64));
    }

    private static void PopulateTetrahedronCells(
        IReadOnlyList<ProbePositionData> positions,
        IReadOnlyList<ProbeTetraData> tetrahedra,
        Vector3 origin,
        float cellSize,
        IVector3 dimensions,
        IReadOnlyList<List<int>> cellLists)
    {
        Vector3 padding = new(cellSize * 0.5f);
        for (int tetrahedronIndex = 0; tetrahedronIndex < tetrahedra.Count; tetrahedronIndex++)
        {
            if (!TryGetTetrahedronBounds(tetrahedra[tetrahedronIndex], positions, out Vector3 minimum, out Vector3 maximum))
                continue;

            GetCellRange(minimum - padding, maximum + padding, origin, cellSize, dimensions, out IVector3 minCell, out IVector3 maxCell);
            for (int z = minCell.Z; z <= maxCell.Z; z++)
                for (int y = minCell.Y; y <= maxCell.Y; y++)
                    for (int x = minCell.X; x <= maxCell.X; x++)
                        cellLists[x + y * dimensions.X + z * dimensions.X * dimensions.Y].Add(tetrahedronIndex);
        }
    }

    private static void GetInfluenceBounds(ProbePositionData position, ProbeParamData parameters, out Vector3 minimum, out Vector3 maximum)
    {
        Vector4 pos = position.Position;
        Vector4 offset = parameters.InfluenceOffsetShape;
        Vector3 center = new(pos.X + offset.X, pos.Y + offset.Y, pos.Z + offset.Z);
        Vector4 outer = parameters.InfluenceOuter;
        Vector3 extents = offset.W >= 0.5f
            ? new Vector3(MathF.Max(outer.X, 0.0001f), MathF.Max(outer.Y, 0.0001f), MathF.Max(outer.Z, 0.0001f))
            : new Vector3(MathF.Max(outer.W, 0.0001f));
        minimum = center - extents;
        maximum = center + extents;
    }

    private static void GetCellRange(
        Vector3 minimum,
        Vector3 maximum,
        Vector3 origin,
        float cellSize,
        IVector3 dimensions,
        out IVector3 minCell,
        out IVector3 maxCell)
    {
        Vector3 minRelative = (minimum - origin) / cellSize;
        Vector3 maxRelative = (maximum - origin) / cellSize;
        minCell = new IVector3(
            Math.Clamp((int)MathF.Floor(minRelative.X), 0, dimensions.X - 1),
            Math.Clamp((int)MathF.Floor(minRelative.Y), 0, dimensions.Y - 1),
            Math.Clamp((int)MathF.Floor(minRelative.Z), 0, dimensions.Z - 1));
        maxCell = new IVector3(
            Math.Clamp((int)MathF.Floor(maxRelative.X), 0, dimensions.X - 1),
            Math.Clamp((int)MathF.Floor(maxRelative.Y), 0, dimensions.Y - 1),
            Math.Clamp((int)MathF.Floor(maxRelative.Z), 0, dimensions.Z - 1));
    }

    private static bool TryGetTetrahedronBounds(
        ProbeTetraData tetrahedron,
        IReadOnlyList<ProbePositionData> positions,
        out Vector3 minimum,
        out Vector3 maximum)
    {
        Vector4 indices = tetrahedron.Indices;
        int index0 = (int)indices.X;
        int index1 = (int)indices.Y;
        int index2 = (int)indices.Z;
        int index3 = (int)indices.W;
        if ((uint)index0 >= positions.Count || (uint)index1 >= positions.Count ||
            (uint)index2 >= positions.Count || (uint)index3 >= positions.Count)
        {
            minimum = maximum = Vector3.Zero;
            return false;
        }

        Vector3 p0 = ToVector3(positions[index0].Position);
        Vector3 p1 = ToVector3(positions[index1].Position);
        Vector3 p2 = ToVector3(positions[index2].Position);
        Vector3 p3 = ToVector3(positions[index3].Position);
        minimum = Vector3.Min(Vector3.Min(p0, p1), Vector3.Min(p2, p3));
        maximum = Vector3.Max(Vector3.Max(p0, p1), Vector3.Max(p2, p3));
        return true;
    }

    private static List<int>? CollectPreferredProbeIndices(
        IReadOnlyList<int> tetrahedronIndices,
        IReadOnlyList<ProbeTetraData>? tetrahedra,
        int probeCount)
    {
        if (tetrahedra is null || tetrahedronIndices.Count == 0 || probeCount <= 0)
            return null;

        var preferred = new List<int>(Math.Min(probeCount, tetrahedronIndices.Count * 4));
        var seen = new HashSet<int>();
        foreach (int tetrahedronIndex in tetrahedronIndices)
        {
            if ((uint)tetrahedronIndex >= tetrahedra.Count)
                continue;
            Vector4 indices = tetrahedra[tetrahedronIndex].Indices;
            AddPreferred((int)indices.X, probeCount, seen, preferred);
            AddPreferred((int)indices.Y, probeCount, seen, preferred);
            AddPreferred((int)indices.Z, probeCount, seen, preferred);
            AddPreferred((int)indices.W, probeCount, seen, preferred);
        }

        return preferred.Count == 0 ? null : preferred;
    }

    private static void AddPreferred(int index, int count, HashSet<int> seen, List<int> preferred)
    {
        if ((uint)index < count && seen.Add(index))
            preferred.Add(index);
    }

    private static IVector4 ComputeFallbackIndices(
        Vector3 cellCenter,
        IReadOnlyList<ProbePositionData> positions,
        IReadOnlyList<int>? preferred)
    {
        Span<float> distances = stackalloc float[4] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
        Span<int> indices = stackalloc int[4] { -1, -1, -1, -1 };
        if (preferred is { Count: > 0 })
        {
            foreach (int index in preferred)
                ConsiderProbe(index, cellCenter, positions, distances, indices);
        }
        else
        {
            for (int index = 0; index < positions.Count; index++)
                ConsiderProbe(index, cellCenter, positions, distances, indices);
        }

        return new IVector4(indices[0], indices[1], indices[2], indices[3]);
    }

    private static void ConsiderProbe(
        int index,
        Vector3 center,
        IReadOnlyList<ProbePositionData> positions,
        Span<float> distances,
        Span<int> indices)
    {
        if ((uint)index >= positions.Count)
            return;
        for (int slot = 0; slot < indices.Length; slot++)
            if (indices[slot] == index)
                return;

        float distance = Vector3.Distance(center, ToVector3(positions[index].Position));
        for (int slot = 0; slot < distances.Length; slot++)
        {
            if (distance >= distances[slot])
                continue;
            for (int shift = distances.Length - 1; shift > slot; shift--)
            {
                distances[shift] = distances[shift - 1];
                indices[shift] = indices[shift - 1];
            }
            distances[slot] = distance;
            indices[slot] = index;
            break;
        }
    }

    private static Vector3 ToVector3(Vector4 value)
        => new(value.X, value.Y, value.Z);
}
