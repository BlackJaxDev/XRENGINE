using System.Numerics;
using System.Text;
using XREngine.Data.Rendering;
using XREngine.Modeling;

namespace XREngine.Rendering.Modeling;

public static class XRMeshModelingExporter
{
    public static XRMesh Export(ModelingMeshDocument document, XRMeshModelingExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new XRMeshModelingExportOptions();

        if (options.ValidateDocument)
        {
            ModelingMeshValidationReport report = ModelingMeshValidation.Validate(document);
            if (!report.IsValid)
                throw new InvalidOperationException(BuildValidationFailureMessage(report));
        }

        ExportOrderingResult ordering = BuildOrderingResult(document, options.OrderingPolicy);
        int exportedVertexCount = ordering.NewToOldVertexMap?.Length ?? document.Positions.Count;

        if (exportedVertexCount > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"Phase 0 exporter supports up to {ushort.MaxValue} vertices, but received {exportedVertexCount}.");
        }

        List<Vertex> vertices = BuildVertices(document, ordering.NewToOldVertexMap);
        List<ushort> triangleIndices = BuildTriangleIndices(ordering.TriangleIndices);

        XRMesh mesh = new(vertices, triangleIndices)
        {
            Type = EPrimitiveType.Triangles
        };
        mesh.Triangles = BuildTriangleList(ordering.TriangleIndices);

        if (mesh.VertexCount != exportedVertexCount)
        {
            throw new InvalidOperationException(
                $"Exported mesh vertex count mismatch. Expected {exportedVertexCount}, got {mesh.VertexCount}.");
        }

        int[]? exportedIndices = mesh.GetIndices(EPrimitiveType.Triangles);
        if (exportedIndices is null || exportedIndices.Length != ordering.TriangleIndices.Count)
        {
            throw new InvalidOperationException(
                $"Exported triangle index count mismatch. Expected {ordering.TriangleIndices.Count}, got {exportedIndices?.Length ?? 0}.");
        }

        ApplyExportContract(mesh);
        return mesh;
    }

    /// <summary>
    /// Applies the XRMesh save/update contract used by modeling export:
    /// rebuild bounds from current positions, repopulate triangle index state,
    /// invalidate index-buffer cache, clear stale acceleration caches, and
    /// emit mesh change notification for renderer backends.
    /// </summary>
    internal static void ApplyExportContract(XRMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        mesh.RebuildBoundsFromPositions();
        mesh.InvalidateIndexBufferCache(EPrimitiveType.Triangles);
        mesh.ClearAccelerationCaches();
        mesh.NotifyMeshDataChanged();
    }

    private static ExportOrderingResult BuildOrderingResult(
        ModelingMeshDocument document,
        XRMeshModelingExportOrderingPolicy orderingPolicy)
    {
        return orderingPolicy switch
        {
            XRMeshModelingExportOrderingPolicy.PreserveDocumentOrder => new ExportOrderingResult(document.TriangleIndices, null),
            XRMeshModelingExportOrderingPolicy.Canonicalized => BuildCanonicalizedOrderingResult(document),
            _ => throw new ArgumentOutOfRangeException(nameof(orderingPolicy), orderingPolicy, "Unknown export ordering policy.")
        };
    }

    private static ExportOrderingResult BuildCanonicalizedOrderingResult(ModelingMeshDocument document)
    {
        if ((document.TriangleIndices.Count % 3) != 0)
            throw new InvalidOperationException("Triangle index count must be divisible by 3 for canonicalized export ordering.");

        int vertexCount = document.Positions.Count;
        int[] newToOldVertexMap = [.. Enumerable.Range(0, vertexCount)];
        Array.Sort(newToOldVertexMap, new CanonicalVertexComparer(document));

        int[] oldToNewVertexMap = new int[vertexCount];
        for (int newIndex = 0; newIndex < newToOldVertexMap.Length; newIndex++)
            oldToNewVertexMap[newToOldVertexMap[newIndex]] = newIndex;

        List<CanonicalTriangle> triangles = new(document.TriangleIndices.Count / 3);
        for (int i = 0; i + 2 < document.TriangleIndices.Count; i += 3)
        {
            int a = RemapTriangleIndex(oldToNewVertexMap, document.TriangleIndices[i], i);
            int b = RemapTriangleIndex(oldToNewVertexMap, document.TriangleIndices[i + 1], i + 1);
            int c = RemapTriangleIndex(oldToNewVertexMap, document.TriangleIndices[i + 2], i + 2);

            // Canonical rotation preserves winding while ensuring the smallest index starts each face tuple.
            RotateTriangleToMinimalStart(ref a, ref b, ref c);
            triangles.Add(new CanonicalTriangle(a, b, c, i / 3));
        }

        triangles.Sort(CanonicalTriangleComparer.Instance);

        List<int> remappedIndices = new(document.TriangleIndices.Count);
        foreach (CanonicalTriangle triangle in triangles)
        {
            remappedIndices.Add(triangle.A);
            remappedIndices.Add(triangle.B);
            remappedIndices.Add(triangle.C);
        }

        return new ExportOrderingResult(remappedIndices, newToOldVertexMap);
    }

    private static int RemapTriangleIndex(int[] oldToNewVertexMap, int sourceIndex, int elementIndex)
    {
        if (sourceIndex < 0 || sourceIndex >= oldToNewVertexMap.Length)
        {
            throw new InvalidOperationException(
                $"Triangle index {sourceIndex} at element {elementIndex} is out of range for vertex count {oldToNewVertexMap.Length}.");
        }

        return oldToNewVertexMap[sourceIndex];
    }

    private static void RotateTriangleToMinimalStart(ref int a, ref int b, ref int c)
    {
        if (b <= a && b <= c)
        {
            (a, b, c) = (b, c, a);
        }
        else if (c <= a && c <= b)
        {
            (a, b, c) = (c, a, b);
        }
    }

    private static List<Vertex> BuildVertices(ModelingMeshDocument document, int[]? newToOldVertexMap)
    {
        int vertexCount = newToOldVertexMap?.Length ?? document.Positions.Count;
        List<Vertex> vertices = new(vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            int sourceIndex = newToOldVertexMap?[i] ?? i;
            Vertex vertex = new(document.Positions[sourceIndex]);

            if (document.Normals is not null && sourceIndex < document.Normals.Count)
                vertex.Normal = document.Normals[sourceIndex];
            if (document.Tangents is not null && sourceIndex < document.Tangents.Count)
                vertex.Tangent = document.Tangents[sourceIndex];

            if (document.TexCoordChannels is { Count: > 0 })
            {
                List<Vector2> texCoords = new(document.TexCoordChannels.Count);
                for (int channel = 0; channel < document.TexCoordChannels.Count; channel++)
                {
                    List<Vector2>? sourceChannel = document.TexCoordChannels[channel];
                    if (sourceChannel is null || sourceIndex >= sourceChannel.Count)
                        continue;
                    texCoords.Add(sourceChannel[sourceIndex]);
                }

                if (texCoords.Count > 0)
                    vertex.TextureCoordinateSets = texCoords;
            }

            if (document.ColorChannels is { Count: > 0 })
            {
                List<Vector4> colors = new(document.ColorChannels.Count);
                for (int channel = 0; channel < document.ColorChannels.Count; channel++)
                {
                    List<Vector4>? sourceChannel = document.ColorChannels[channel];
                    if (sourceChannel is null || sourceIndex >= sourceChannel.Count)
                        continue;
                    colors.Add(sourceChannel[sourceIndex]);
                }

                if (colors.Count > 0)
                    vertex.ColorSets = colors;
            }

            vertices.Add(vertex);
        }

        return vertices;
    }

    private sealed class CanonicalVertexComparer(ModelingMeshDocument document) : IComparer<int>
    {
        public int Compare(int leftIndex, int rightIndex)
        {
            int comparison = CompareVector3Bits(document.Positions[leftIndex], document.Positions[rightIndex]);
            if (comparison != 0)
                return comparison;

            comparison = CompareOptionalVector3(document.Normals, leftIndex, rightIndex);
            if (comparison != 0)
                return comparison;

            comparison = CompareOptionalVector3(document.Tangents, leftIndex, rightIndex);
            if (comparison != 0)
                return comparison;

            comparison = CompareOptionalVector2Channels(document.TexCoordChannels, leftIndex, rightIndex);
            if (comparison != 0)
                return comparison;

            comparison = CompareOptionalVector4Channels(document.ColorChannels, leftIndex, rightIndex);
            if (comparison != 0)
                return comparison;

            // Stable tie-break: if all channels match, keep lower source index first.
            return leftIndex.CompareTo(rightIndex);
        }
    }

    private static int CompareOptionalVector3(List<Vector3>? values, int leftIndex, int rightIndex)
    {
        if (values is null)
            return 0;

        Vector3 left = leftIndex < values.Count ? values[leftIndex] : Vector3.Zero;
        Vector3 right = rightIndex < values.Count ? values[rightIndex] : Vector3.Zero;
        return CompareVector3Bits(left, right);
    }

    private static int CompareOptionalVector2Channels(List<List<Vector2>>? channels, int leftIndex, int rightIndex)
    {
        if (channels is null)
            return 0;

        for (int i = 0; i < channels.Count; i++)
        {
            List<Vector2>? channel = channels[i];
            Vector2 left = channel is not null && leftIndex < channel.Count ? channel[leftIndex] : Vector2.Zero;
            Vector2 right = channel is not null && rightIndex < channel.Count ? channel[rightIndex] : Vector2.Zero;

            int comparison = CompareVector2Bits(left, right);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static int CompareOptionalVector4Channels(List<List<Vector4>>? channels, int leftIndex, int rightIndex)
    {
        if (channels is null)
            return 0;

        for (int i = 0; i < channels.Count; i++)
        {
            List<Vector4>? channel = channels[i];
            Vector4 left = channel is not null && leftIndex < channel.Count ? channel[leftIndex] : Vector4.Zero;
            Vector4 right = channel is not null && rightIndex < channel.Count ? channel[rightIndex] : Vector4.Zero;

            int comparison = CompareVector4Bits(left, right);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static int CompareVector2Bits(Vector2 left, Vector2 right)
    {
        int comparison = CompareFloatBits(left.X, right.X);
        if (comparison != 0)
            return comparison;

        return CompareFloatBits(left.Y, right.Y);
    }

    private static int CompareVector3Bits(Vector3 left, Vector3 right)
    {
        int comparison = CompareFloatBits(left.X, right.X);
        if (comparison != 0)
            return comparison;

        comparison = CompareFloatBits(left.Y, right.Y);
        if (comparison != 0)
            return comparison;

        return CompareFloatBits(left.Z, right.Z);
    }

    private static int CompareVector4Bits(Vector4 left, Vector4 right)
    {
        int comparison = CompareFloatBits(left.X, right.X);
        if (comparison != 0)
            return comparison;

        comparison = CompareFloatBits(left.Y, right.Y);
        if (comparison != 0)
            return comparison;

        comparison = CompareFloatBits(left.Z, right.Z);
        if (comparison != 0)
            return comparison;

        return CompareFloatBits(left.W, right.W);
    }

    private static int CompareFloatBits(float left, float right)
        => BitConverter.SingleToInt32Bits(left).CompareTo(BitConverter.SingleToInt32Bits(right));

    private static List<ushort> BuildTriangleIndices(IReadOnlyList<int> triangleIndices)
    {
        if ((triangleIndices.Count % 3) != 0)
            throw new InvalidOperationException("Triangle index count must be divisible by 3.");

        List<ushort> indices = new(triangleIndices.Count);
        for (int i = 0; i < triangleIndices.Count; i++)
        {
            int index = triangleIndices[i];
            if (index < 0)
                throw new InvalidOperationException($"Triangle index at element {i} is negative.");
            if (index > ushort.MaxValue)
                throw new InvalidOperationException($"Triangle index {index} exceeds {ushort.MaxValue}.");
            indices.Add((ushort)index);
        }

        return indices;
    }

    private static List<IndexTriangle> BuildTriangleList(IReadOnlyList<int> triangleIndices)
    {
        List<IndexTriangle> triangles = new(triangleIndices.Count / 3);
        for (int i = 0; i + 2 < triangleIndices.Count; i += 3)
            triangles.Add(new IndexTriangle(triangleIndices[i], triangleIndices[i + 1], triangleIndices[i + 2]));
        return triangles;
    }

    private readonly struct ExportOrderingResult(IReadOnlyList<int> triangleIndices, int[]? newToOldVertexMap)
    {
        public IReadOnlyList<int> TriangleIndices { get; } = triangleIndices;
        public int[]? NewToOldVertexMap { get; } = newToOldVertexMap;
    }

    private readonly record struct CanonicalTriangle(int A, int B, int C, int SourceFaceIndex);

    private sealed class CanonicalTriangleComparer : IComparer<CanonicalTriangle>
    {
        public static readonly CanonicalTriangleComparer Instance = new();

        public int Compare(CanonicalTriangle left, CanonicalTriangle right)
        {
            int comparison = left.A.CompareTo(right.A);
            if (comparison != 0)
                return comparison;

            comparison = left.B.CompareTo(right.B);
            if (comparison != 0)
                return comparison;

            comparison = left.C.CompareTo(right.C);
            if (comparison != 0)
                return comparison;

            return left.SourceFaceIndex.CompareTo(right.SourceFaceIndex);
        }
    }

    private static string BuildValidationFailureMessage(ModelingMeshValidationReport report)
    {
        StringBuilder builder = new("Modeling mesh validation failed during export:");
        foreach (ModelingMeshValidationIssue issue in report.Issues.Where(x => x.Severity == ModelingValidationSeverity.Error))
        {
            builder.AppendLine();
            builder.Append("- ");
            builder.Append(issue.Code);
            builder.Append(": ");
            builder.Append(issue.Message);
            if (issue.ElementIndex.HasValue)
            {
                builder.Append(" (element ");
                builder.Append(issue.ElementIndex.Value);
                builder.Append(')');
            }
        }

        return builder.ToString();
    }
}
