using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>
    /// Converts mesh attributes in place during import. Call this before a renderer uses the mesh.
    /// Keeps vertex order, skinning data, colors, and texture coordinates.
    /// </summary>
    public void TransformImportGeometry(Matrix4x4 conversion)
    {
        const float affineColumnTolerance = 1.0e-5f;
        if (!IsFiniteImportMatrix(conversion) ||
            MathF.Abs(conversion.M14) > affineColumnTolerance ||
            MathF.Abs(conversion.M24) > affineColumnTolerance ||
            MathF.Abs(conversion.M34) > affineColumnTolerance ||
            MathF.Abs(conversion.M44 - 1.0f) > affineColumnTolerance)
            throw new ArgumentException(
                $"Mesh '{Name}' requires a finite affine geometry conversion. Matrix: {conversion}.",
                nameof(conversion));

        // Matrix multiplication and inversion can leave rounding in the affine column.
        conversion.M14 = 0.0f;
        conversion.M24 = 0.0f;
        conversion.M34 = 0.0f;
        conversion.M44 = 1.0f;
        if (!Matrix4x4.Invert(conversion, out Matrix4x4 inverse) ||
            !IsFiniteImportMatrix(inverse))
            throw new ArgumentException(
                $"Mesh '{Name}' requires an invertible geometry conversion. Matrix: {conversion}.",
                nameof(conversion));

        if (VertexCount <= 0)
            throw new InvalidOperationException($"Mesh '{Name}' has no vertices to convert.");

        ValidateImportAttributeBuffers();
        XRMeshBlendshapeActiveListReader blendshapeReader = default;
        // A named shape can have no deltas. Keep its name while converting the base mesh.
        bool hasNamedEmptyBlendshapes = HasNamedEmptyBlendshapes;
        if (HasBlendshapes && !hasNamedEmptyBlendshapes)
        {
            const BlendshapeShaderVariant supported =
                BlendshapeShaderVariant.ActiveList |
                BlendshapeShaderVariant.SparseDeltas |
                BlendshapeShaderVariant.QuantizedDeltas;
            if ((BlendshapeShaderVariant & ~supported) != 0 ||
                BlendshapeDeltaEncoding != BlendshapeDeltaEncoding.Snorm16Vector3 ||
                !XRMeshBlendshapeActiveListReader.TryCreate(this, out blendshapeReader) ||
                !HasValidImportBlendshapeIndices(blendshapeReader))
                throw new NotSupportedException(
                    $"Mesh '{Name}' has blendshape data that import geometry conversion cannot rebuild.");
        }

        Matrix4x4 normalConversion = Matrix4x4.Transpose(inverse);
        float determinant = conversion.GetDeterminant();
        if (!float.IsFinite(determinant) || determinant == 0.0f)
            throw new ArgumentException(
                $"Mesh '{Name}' has a singular geometry conversion basis. Matrix: {conversion}.",
                nameof(conversion));
        float handedness = determinant < 0.0f ? -1.0f : 1.0f;

        Vector3 min, max;
        if (HasBlendshapes && !CanStreamImportBlendshapes(blendshapeReader))
            TransformImportGeometryMaterialized(conversion, normalConversion, handedness, out min, out max);
        else
            TransformImportGeometryStreaming(conversion, normalConversion, handedness, blendshapeReader, out min, out max);

        if (handedness < 0.0f && _triangles is { Count: > 0 } triangles)
        {
            for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            {
                IndexTriangle triangle = triangles[triangleIndex];
                triangles[triangleIndex] = new IndexTriangle(
                    triangle.Point0, triangle.Point2, triangle.Point1);
            }
        }

        Bounds = new AABB(min, max);
        MarkGeometryChanged();
    }

    private void TransformImportGeometryMaterialized(
        in Matrix4x4 conversion,
        in Matrix4x4 normalConversion,
        float handedness,
        out Vector3 min,
        out Vector3 max)
    {
        EXRMeshVertexViewContent content = EXRMeshVertexViewContent.Positions |
            EXRMeshVertexViewContent.Normals |
            EXRMeshVertexViewContent.Tangents |
            (HasBlendshapes ? EXRMeshVertexViewContent.Blendshapes : 0);
        using XRMeshVertexView view = XRMeshVertexView.Open(this, content);
        Vertex[] vertices = view.Vertices;
        min = new(float.PositiveInfinity);
        max = new(float.NegativeInfinity);
        for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            Vertex vertex = vertices[vertexIndex];
            TransformImportVertexData(vertex, conversion, normalConversion, handedness);
            IncludeImportPoint(vertex.Position, ref min, ref max);
            if (vertex.Blendshapes is null)
                continue;

            foreach ((_, VertexData target) in vertex.Blendshapes)
            {
                TransformImportVertexData(target, conversion, normalConversion, handedness);
                IncludeImportPoint(target.Position, ref min, ref max);
            }
        }

        for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            uint index = (uint)vertexIndex;
            Vertex vertex = vertices[vertexIndex];
            SetPosition(index, vertex.Position);
            if (HasNormals && vertex.Normal is { } normal)
                SetNormal(index, normal);
            if (HasTangents && vertex.Tangent is { } tangent)
                SetTangent(index, tangent, vertex.BitangentSign);
        }

        if (HasBlendshapes)
            RebuildBlendshapeBuffersFromVertices(vertices);
    }

    private bool HasValidImportBlendshapeIndices(XRMeshBlendshapeActiveListReader reader)
    {
        for (int vertex = 0; vertex < VertexCount; vertex++)
        {
            reader.GetVertexEntries(vertex, out int first, out int count);
            for (int entry = first; entry < first + count; entry++)
            {
                reader.ReadEntry(entry, out int shape, out _, out _, out _);
                if ((uint)shape >= (uint)reader.ShapeCount)
                    return false;
            }
        }
        return true;
    }

    private bool CanStreamImportBlendshapes(XRMeshBlendshapeActiveListReader reader)
    {
        if (!reader.IsValid)
            return true;

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string name in BlendshapeNames)
            if (string.IsNullOrEmpty(name) || !names.Add(name))
                return false;

        HashSet<int> vertexShapes = [];
        for (int vertex = 0; vertex < VertexCount; vertex++)
        {
            vertexShapes.Clear();
            reader.GetVertexEntries(vertex, out int first, out int count);
            for (int entry = first; entry < first + count; entry++)
            {
                reader.ReadEntry(entry, out int shape, out Vector3 position, out Vector3 normal, out Vector3 tangent);
                if (!HasImportDelta(position, normal, tangent))
                    continue;
                if (!vertexShapes.Add(shape))
                    return false;
            }
        }
        return true;
    }

    private static bool HasImportDelta(Vector3 position, Vector3 normal, Vector3 tangent)
        => position.LengthSquared() > 0.0f || normal.LengthSquared() > 0.0f || tangent.LengthSquared() > 0.0f;

    private readonly record struct ImportBaseAttributes(
        Vector3 Position,
        Vector3? Normal,
        Vector3? Tangent,
        float BitangentSign);

    private void TransformImportGeometryStreaming(
        in Matrix4x4 conversion,
        in Matrix4x4 normalConversion,
        float handedness,
        XRMeshBlendshapeActiveListReader reader,
        out Vector3 min,
        out Vector3 max)
    {
        // Keep only base attributes so source targets stay available after base writes.
        VertexData baseData = new();
        VertexData targetData = new();
        Matrix4x4 conversionMatrix = conversion;
        Matrix4x4 normalMatrix = normalConversion;
        min = new(float.PositiveInfinity);
        max = new(float.NegativeInfinity);
        if (!reader.IsValid)
        {
            for (int vertex = 0; vertex < VertexCount; vertex++)
            {
                ReadImportBaseData(vertex, baseData);
                TransformImportVertexData(baseData, conversion, normalConversion, handedness);
                IncludeImportPoint(baseData.Position, ref min, ref max);
            }
            for (int vertex = 0; vertex < VertexCount; vertex++)
            {
                ReadImportBaseData(vertex, baseData);
                TransformImportVertexData(baseData, conversion, normalConversion, handedness);
                WriteImportBaseData(vertex, baseData);
            }
            return;
        }

        ImportBaseAttributes[] oldBases = new ImportBaseAttributes[VertexCount];
        for (int vertex = 0; vertex < VertexCount; vertex++)
        {
            ReadImportBaseData(vertex, baseData);
            Vector3 oldPosition = baseData.Position;
            Vector3? oldNormal = baseData.Normal;
            Vector3? oldTangent = baseData.Tangent;
            float oldSign = baseData.BitangentSign;
            oldBases[vertex] = new ImportBaseAttributes(oldPosition, oldNormal, oldTangent, oldSign);
            TransformImportVertexData(baseData, conversion, normalConversion, handedness);
            IncludeImportPoint(baseData.Position, ref min, ref max);
            reader.GetVertexEntries(vertex, out int first, out int count);
            for (int entry = first; entry < first + count; entry++)
            {
                reader.ReadEntry(entry, out _, out Vector3 position, out Vector3 normal, out Vector3 tangent);
                if (!HasImportDelta(position, normal, tangent))
                    continue;
                SetImportTargetData(targetData, oldPosition, oldNormal, oldTangent, oldSign, position, normal, tangent);
                TransformImportVertexData(targetData, conversion, normalConversion, handedness);
                IncludeImportPoint(targetData.Position, ref min, ref max);
            }
        }

        // Set bases before the blendshape rebuild captures its geometry ticket.
        for (int vertex = 0; vertex < oldBases.Length; vertex++)
        {
            ReadImportBaseData(oldBases[vertex], baseData);
            TransformImportVertexData(baseData, conversion, normalConversion, handedness);
            WriteImportBaseData(vertex, baseData);
        }

        RebuildBlendshapeBuffers((vertex, destination) =>
            {
                ReadImportBaseData(oldBases[vertex], baseData);
                Vector3 oldPosition = baseData.Position;
                Vector3? oldNormal = baseData.Normal;
                Vector3? oldTangent = baseData.Tangent;
                float oldSign = baseData.BitangentSign;
                TransformImportVertexData(baseData, conversionMatrix, normalMatrix, handedness);

                reader.GetVertexEntries(vertex, out int first, out int count);
                int start = destination.Count;
                for (int entry = first; entry < first + count; entry++)
                {
                    reader.ReadEntry(entry, out int shape, out Vector3 position, out Vector3 normal, out Vector3 tangent);
                    if (!HasImportDelta(position, normal, tangent))
                        continue;
                    SetImportTargetData(targetData, oldPosition, oldNormal, oldTangent, oldSign, position, normal, tangent);
                    TransformImportVertexData(targetData, conversionMatrix, normalMatrix, handedness);
                    destination.Add(new BlendshapeVertexDelta(
                        shape,
                        targetData.Position - baseData.Position,
                        (targetData.Normal ?? Vector3.Zero) - (baseData.Normal ?? Vector3.Zero),
                        (targetData.Tangent ?? Vector3.Zero) - (baseData.Tangent ?? Vector3.Zero)));
                }
                destination.Sort(start, destination.Count - start, BlendshapeVertexDeltaShapeComparer.Instance);
            });
    }

    private void WriteImportBaseData(int vertex, VertexData source)
    {
        uint index = (uint)vertex;
        SetPosition(index, source.Position);
        if (source.Normal is { } normal)
            SetNormal(index, normal);
        if (source.Tangent is { } tangent)
            SetTangent(index, tangent, source.BitangentSign);
    }

    private static void ReadImportBaseData(ImportBaseAttributes source, VertexData destination)
    {
        destination.Position = source.Position;
        destination.Normal = source.Normal;
        destination.Tangent = source.Tangent;
        destination.BitangentSign = source.BitangentSign;
    }

    private void ReadImportBaseData(int vertex, VertexData destination)
    {
        uint index = (uint)vertex;
        destination.Position = GetPosition(index);
        destination.Normal = HasNormals ? GetNormal(index) : null;
        if (HasTangents)
        {
            Vector4 tangent = GetTangentWithSign(index);
            destination.Tangent = new Vector3(tangent.X, tangent.Y, tangent.Z);
            destination.BitangentSign = tangent.W;
        }
        else
        {
            destination.Tangent = null;
            destination.BitangentSign = 1.0f;
        }
    }

    private static void SetImportTargetData(
        VertexData destination,
        Vector3 basePosition,
        Vector3? baseNormal,
        Vector3? baseTangent,
        float baseSign,
        Vector3 positionDelta,
        Vector3 normalDelta,
        Vector3 tangentDelta)
    {
        destination.Position = basePosition + positionDelta;
        destination.Normal = baseNormal.HasValue || normalDelta.LengthSquared() > 0.0f
            ? (baseNormal ?? Vector3.Zero) + normalDelta : null;
        destination.Tangent = baseTangent.HasValue || tangentDelta.LengthSquared() > 0.0f
            ? (baseTangent ?? Vector3.Zero) + tangentDelta : null;
        destination.BitangentSign = baseSign;
    }

    private void ValidateImportAttributeBuffers()
    {
        if (Interleaved)
        {
            XRDataBuffer? buffer = InterleavedVertexBuffer;
            if (buffer?.ClientSideSource is null || buffer.ElementCount < (uint)VertexCount ||
                buffer.ClientSideSource.Length < (ulong)(uint)VertexCount * InterleavedStride ||
                InterleavedStride < PositionOffset + 12u ||
                (HasNormals && (!NormalOffset.HasValue || InterleavedStride < NormalOffset.Value + 12u)) ||
                (HasTangents && (!TangentOffset.HasValue || InterleavedStride < TangentOffset.Value + 16u)))
                throw new InvalidOperationException($"Mesh '{Name}' has unreadable interleaved import attributes.");
            return;
        }

        if (!HasReadableImportBuffer(PositionsBuffer, 3u) ||
            (HasNormals && !HasReadableImportBuffer(NormalsBuffer, 3u)) ||
            (HasTangents && !HasReadableImportBuffer(TangentsBuffer, 4u)))
            throw new InvalidOperationException($"Mesh '{Name}' has unreadable import attributes.");
    }

    private bool HasReadableImportBuffer(XRDataBuffer? buffer, uint components)
        => buffer?.ClientSideSource is { } source &&
           buffer.ComponentType == EComponentType.Float &&
           buffer.ComponentCount == components &&
           buffer.ElementCount >= (uint)VertexCount &&
           source.Length >= (ulong)(uint)VertexCount * components * sizeof(float);

    private static void TransformImportVertexData(
        VertexData vertex,
        in Matrix4x4 conversion,
        in Matrix4x4 normalConversion,
        float handedness)
    {
        vertex.Position = Vector3.Transform(vertex.Position, conversion);
        if (!IsFiniteImportVector(vertex.Position))
            throw new InvalidOperationException("Geometry conversion produced a non-finite position.");

        if (vertex.Normal is { } oldNormal)
        {
            Vector3 normal = Vector3.TransformNormal(oldNormal, normalConversion);
            vertex.Normal = NormalizeImportDirection(normal);
        }

        if (vertex.Tangent is { } oldTangent)
        {
            Vector3 tangent = Vector3.TransformNormal(oldTangent, conversion);
            if (vertex.Normal is { } normal && normal != Vector3.Zero)
                tangent -= normal * Vector3.Dot(normal, tangent);
            vertex.Tangent = NormalizeImportDirection(tangent);
            vertex.BitangentSign *= handedness;
            if (!float.IsFinite(vertex.BitangentSign))
                throw new InvalidOperationException("Geometry conversion produced a non-finite tangent sign.");
        }
    }

    private static Vector3 NormalizeImportDirection(Vector3 direction)
    {
        if (!IsFiniteImportVector(direction))
            throw new InvalidOperationException("Geometry conversion produced a non-finite direction.");
        float lengthSquared = direction.LengthSquared();
        if (lengthSquared == 0.0f)
            return Vector3.Zero;
        if (!float.IsFinite(lengthSquared))
            throw new InvalidOperationException("Geometry conversion produced a direction outside float range.");
        return direction / MathF.Sqrt(lengthSquared);
    }

    private static void IncludeImportPoint(Vector3 point, ref Vector3 min, ref Vector3 max)
    {
        min = Vector3.Min(min, point);
        max = Vector3.Max(max, point);
    }

    private static bool IsFiniteImportVector(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFiniteImportMatrix(in Matrix4x4 matrix)
    {
        ReadOnlySpan<float> values =
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ];
        foreach (float value in values)
            if (!float.IsFinite(value))
                return false;
        return true;
    }
}
