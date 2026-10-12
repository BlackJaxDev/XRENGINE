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
        if (HasBlendshapes)
        {
            const BlendshapeShaderVariant supported =
                BlendshapeShaderVariant.ActiveList |
                BlendshapeShaderVariant.SparseDeltas |
                BlendshapeShaderVariant.QuantizedDeltas;
            if ((BlendshapeShaderVariant & ~supported) != 0 ||
                BlendshapeDeltaEncoding != BlendshapeDeltaEncoding.Snorm16Vector3 ||
                !XRMeshBlendshapeDeltaTable.TryCreate(this, out _))
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

        EXRMeshVertexViewContent content = EXRMeshVertexViewContent.Positions |
            EXRMeshVertexViewContent.Normals |
            EXRMeshVertexViewContent.Tangents |
            (HasBlendshapes ? EXRMeshVertexViewContent.Blendshapes : 0);
        using XRMeshVertexView view = XRMeshVertexView.Open(this, content);
        Vertex[] vertices = view.Vertices;
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
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
