using MIConvexHull;
using System.Numerics;

namespace XREngine.Rendering;

internal static class ForwardLightProbeTopologyBuilder
{
    private const float PositionQuantization = 0.001f;

    public static ProbeTopologyResult Build(ProbeTopologySnapshot snapshot)
    {
        ProbeTetraData[] tetrahedra = snapshot.ProbeCount is > 0 and < 5
            ? BuildFallback(snapshot.ProbeCount)
            : BuildDelaunay(snapshot.Positions);
        if (tetrahedra.Length == 0)
            tetrahedra = BuildCartesianLatticeFallback(snapshot.Positions);
        return new ProbeTopologyResult(
            snapshot.PipelineInstanceId,
            snapshot.ResourceGeneration,
            snapshot.RequestToken,
            snapshot.LayoutSignature,
            snapshot.ProbeIds,
            snapshot.SourceIndices,
            tetrahedra);
    }

    private static ProbeTetraData[] BuildDelaunay(IReadOnlyList<Vector3> positions)
    {
        List<ProbeTopologyVertex> vertices = FilterDistinctFinitePositions(positions);
        if (vertices.Count < 5)
            return [];

        try
        {
            ITriangulation<ProbeTopologyVertex, ProbeTopologyCell> triangulation =
                Triangulation.CreateDelaunay<ProbeTopologyVertex, ProbeTopologyCell>(vertices);
            var tetrahedra = new List<ProbeTetraData>();
            foreach (ProbeTopologyCell cell in triangulation.Cells)
            {
                ProbeTopologyVertex[] cellVertices = cell.Vertices;
                if (cellVertices.Length < 4)
                    continue;

                tetrahedra.Add(new ProbeTetraData
                {
                    Indices = new Vector4(
                        cellVertices[0].PublishedIndex,
                        cellVertices[1].PublishedIndex,
                        cellVertices[2].PublishedIndex,
                        cellVertices[3].PublishedIndex),
                });
            }

            return [.. tetrahedra];
        }
        catch (ConvexHullGenerationException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private static List<ProbeTopologyVertex> FilterDistinctFinitePositions(IReadOnlyList<Vector3> positions)
    {
        var distinct = new Dictionary<(int X, int Y, int Z), ProbeTopologyVertex>();
        float inverseQuantization = 1.0f / PositionQuantization;
        for (int index = 0; index < positions.Count; index++)
        {
            Vector3 position = positions[index];
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                continue;

            var key = (
                (int)MathF.Round(position.X * inverseQuantization),
                (int)MathF.Round(position.Y * inverseQuantization),
                (int)MathF.Round(position.Z * inverseQuantization));
            distinct.TryAdd(key, new ProbeTopologyVertex(index, position));
        }

        return [.. distinct.Values];
    }

    /// <summary>
    /// Builds a deterministic Kuhn decomposition when Delaunay cannot choose a
    /// triangulation for a co-spherical Cartesian lattice. The source positions
    /// remain untouched: quantization only recognizes lattice membership, while
    /// the emitted tetrahedra are validated against the original probe positions.
    /// </summary>
    private static ProbeTetraData[] BuildCartesianLatticeFallback(
        IReadOnlyList<Vector3> positions)
    {
        var indicesByCoordinate = new Dictionary<(int X, int Y, int Z), int>();
        var xCoordinates = new List<int>();
        var yCoordinates = new List<int>();
        var zCoordinates = new List<int>();
        float inverseQuantization = 1.0f / PositionQuantization;
        for (int index = 0; index < positions.Count; index++)
        {
            Vector3 position = positions[index];
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                return [];

            var coordinate = (
                X: (int)MathF.Round(position.X * inverseQuantization),
                Y: (int)MathF.Round(position.Y * inverseQuantization),
                Z: (int)MathF.Round(position.Z * inverseQuantization));
            if (!indicesByCoordinate.TryAdd(coordinate, index))
                return [];

            xCoordinates.Add(coordinate.X);
            yCoordinates.Add(coordinate.Y);
            zCoordinates.Add(coordinate.Z);
        }

        xCoordinates.Sort();
        yCoordinates.Sort();
        zCoordinates.Sort();
        RemoveDuplicateCoordinates(xCoordinates);
        RemoveDuplicateCoordinates(yCoordinates);
        RemoveDuplicateCoordinates(zCoordinates);
        if (xCoordinates.Count < 2 || yCoordinates.Count < 2 || zCoordinates.Count < 2 ||
            (long)xCoordinates.Count * yCoordinates.Count * zCoordinates.Count != indicesByCoordinate.Count)
        {
            return [];
        }

        var tetrahedra = new List<ProbeTetraData>(
            checked((xCoordinates.Count - 1) * (yCoordinates.Count - 1) * (zCoordinates.Count - 1) * 6));
        for (int z = 0; z < zCoordinates.Count - 1; z++)
        for (int y = 0; y < yCoordinates.Count - 1; y++)
        for (int x = 0; x < xCoordinates.Count - 1; x++)
        {
            if (!TryGetCellIndices(
                    indicesByCoordinate,
                    xCoordinates[x], xCoordinates[x + 1],
                    yCoordinates[y], yCoordinates[y + 1],
                    zCoordinates[z], zCoordinates[z + 1],
                    out int p000, out int p100, out int p010, out int p110,
                    out int p001, out int p101, out int p011, out int p111))
            {
                return [];
            }

            // All six tetrahedra share the global low-to-high body diagonal.
            if (!TryAddTetrahedron(tetrahedra, positions, p000, p100, p110, p111) ||
                !TryAddTetrahedron(tetrahedra, positions, p000, p100, p101, p111) ||
                !TryAddTetrahedron(tetrahedra, positions, p000, p001, p101, p111) ||
                !TryAddTetrahedron(tetrahedra, positions, p000, p001, p011, p111) ||
                !TryAddTetrahedron(tetrahedra, positions, p000, p010, p011, p111) ||
                !TryAddTetrahedron(tetrahedra, positions, p000, p010, p110, p111))
            {
                return [];
            }
        }

        return [.. tetrahedra];
    }

    private static void RemoveDuplicateCoordinates(List<int> coordinates)
    {
        for (int index = coordinates.Count - 1; index > 0; index--)
            if (coordinates[index] == coordinates[index - 1])
                coordinates.RemoveAt(index);
    }

    private static bool TryGetCellIndices(
        Dictionary<(int X, int Y, int Z), int> indicesByCoordinate,
        int x0,
        int x1,
        int y0,
        int y1,
        int z0,
        int z1,
        out int p000,
        out int p100,
        out int p010,
        out int p110,
        out int p001,
        out int p101,
        out int p011,
        out int p111)
    {
        p000 = p100 = p010 = p110 = p001 = p101 = p011 = p111 = -1;
        return indicesByCoordinate.TryGetValue((x0, y0, z0), out p000) &&
               indicesByCoordinate.TryGetValue((x1, y0, z0), out p100) &&
               indicesByCoordinate.TryGetValue((x0, y1, z0), out p010) &&
               indicesByCoordinate.TryGetValue((x1, y1, z0), out p110) &&
               indicesByCoordinate.TryGetValue((x0, y0, z1), out p001) &&
               indicesByCoordinate.TryGetValue((x1, y0, z1), out p101) &&
               indicesByCoordinate.TryGetValue((x0, y1, z1), out p011) &&
               indicesByCoordinate.TryGetValue((x1, y1, z1), out p111);
    }

    private static bool TryAddTetrahedron(
        List<ProbeTetraData> tetrahedra,
        IReadOnlyList<Vector3> positions,
        int a,
        int b,
        int c,
        int d)
    {
        if ((uint)a >= positions.Count || (uint)b >= positions.Count ||
            (uint)c >= positions.Count || (uint)d >= positions.Count ||
            a == b || a == c || a == d || b == c || b == d || c == d)
        {
            return false;
        }

        double signedVolume = SignedSixVolume(positions[a], positions[b], positions[c], positions[d]);
        double scale = MaximumCoordinateExtent(positions[a], positions[b], positions[c], positions[d]);
        if (!double.IsFinite(signedVolume) || scale <= 0.0 ||
            Math.Abs(signedVolume) <= scale * scale * scale * 1.0e-6)
        {
            return false;
        }

        if (signedVolume < 0.0)
            (b, c) = (c, b);
        tetrahedra.Add(new ProbeTetraData { Indices = new Vector4(a, b, c, d) });
        return true;
    }

    private static double SignedSixVolume(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        double abX = b.X - a.X;
        double abY = b.Y - a.Y;
        double abZ = b.Z - a.Z;
        double acX = c.X - a.X;
        double acY = c.Y - a.Y;
        double acZ = c.Z - a.Z;
        double adX = d.X - a.X;
        double adY = d.Y - a.Y;
        double adZ = d.Z - a.Z;
        return abX * (acY * adZ - acZ * adY) +
               abY * (acZ * adX - acX * adZ) +
               abZ * (acX * adY - acY * adX);
    }

    private static double MaximumCoordinateExtent(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        double minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
        double maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
        double minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
        double maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
        double minZ = Math.Min(Math.Min(a.Z, b.Z), Math.Min(c.Z, d.Z));
        double maxZ = Math.Max(Math.Max(a.Z, b.Z), Math.Max(c.Z, d.Z));
        return Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
    }

    private static ProbeTetraData[] BuildFallback(int probeCount)
    {
        int a = 0;
        int b = probeCount >= 2 ? 1 : a;
        int c = probeCount >= 3 ? 2 : b;
        int d = probeCount >= 4 ? 3 : c;
        return [new ProbeTetraData { Indices = new Vector4(a, b, c, d) }];
    }
}
