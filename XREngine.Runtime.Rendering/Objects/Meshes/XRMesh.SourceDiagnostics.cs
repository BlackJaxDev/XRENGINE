using System.Buffers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>
    /// Reads the CPU source geometry and packed skin weights for an editor diagnostic.
    /// The returned JSON uses float values that preserve the stored precision.
    /// </summary>
    internal string GetSkinningSourceDiagnosticSummary()
    {
        if (Type != EPrimitiveType.Triangles)
            throw new InvalidOperationException("The source diagnostic requires triangle topology.");
        if (VertexCount <= 0)
            throw new InvalidOperationException("The source diagnostic requires vertices.");
        if (!HasNormals || !HasTangents || TexCoordCount == 0)
            throw new InvalidOperationException("The source diagnostic requires normals, tangents, and UV0.");

        XRDataBuffer? attributes = Interleaved ? InterleavedVertexBuffer : PositionsBuffer;
        if (attributes?.ClientSideSource is null || attributes.ElementCount < (uint)VertexCount)
            throw new InvalidOperationException("The source position buffer has no complete CPU copy.");
        if (Interleaved)
        {
            if (!NormalOffset.HasValue || !TangentOffset.HasValue || !TexCoordOffset.HasValue ||
                InterleavedStride < PositionOffset + 12u ||
                InterleavedStride < NormalOffset.Value + 12u ||
                InterleavedStride < TangentOffset.Value + 16u ||
                InterleavedStride < TexCoordOffset.Value + TexCoordCount * 8u ||
                attributes.ClientSideSource.Length < (ulong)(uint)VertexCount * InterleavedStride)
                throw new InvalidOperationException("The interleaved source layout lacks an attribute.");
        }
        else if (!HasReadableImportBuffer(PositionsBuffer, 3u) ||
                 !HasReadableImportBuffer(NormalsBuffer, 3u) ||
                 !HasReadableImportBuffer(TangentsBuffer, 4u) ||
                 TexCoordBuffers is not { Length: > 0 } ||
                 TexCoordBuffers.Length < TexCoordCount)
        {
            throw new InvalidOperationException("A source attribute buffer has no complete CPU copy.");
        }
        if (!Interleaved)
        {
            for (int channel = 0; channel < TexCoordCount; channel++)
                if (!HasReadableImportBuffer(TexCoordBuffers![channel], 2u))
                    throw new InvalidOperationException($"Source UV channel {channel} has no complete CPU copy.");
        }

        if (!XRMeshSkinningInfluenceReader.TryCreate(this, out XRMeshSkinningInfluenceReader reader))
            throw new InvalidOperationException("The packed skin weights have no complete CPU copy.");
        XRMeshSkinningBufferState skinningState = GetSkinningBufferStateSnapshot();
        if (!HasCompleteCpuData(skinningState.CoreIndices) ||
            !HasCompleteCpuData(skinningState.CoreWeights) ||
            (skinningState.HasSpillInfluences &&
             (!HasCompleteCpuData(skinningState.SpillHeaders) || !HasCompleteCpuData(skinningState.SpillEntries))))
            throw new InvalidOperationException("A packed skin buffer has an incomplete CPU copy.");

        int[]? indices = GetIndices();
        if (indices is null || indices.Length == 0 || indices.Length % 3 != 0)
            throw new InvalidOperationException("The triangle index list is unavailable or incomplete.");
        foreach (int index in indices)
            if ((uint)index >= (uint)VertexCount)
                throw new InvalidOperationException("A triangle index is outside the source vertex range.");

        long geometryRevision = GeometryRevision;
        (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] bones = UtilizedBones;
        if (reader.BoneCount != bones.Length)
            throw new InvalidOperationException("The skin weights and bone table have different sizes.");

        int[] boneIndices = new int[reader.MaxInfluenceCount];
        float[] weights = new float[reader.MaxInfluenceCount];
        ArrayBufferWriter<byte> bytes = new();
        using (Utf8JsonWriter writer = new(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("meshId", ID);
            writer.WriteString("meshName", Name);
            writer.WriteNumber("vertexCount", VertexCount);
            writer.WriteNumber("triangleCount", indices.Length / 3);
            writer.WriteNumber("texCoordCount", TexCoordCount);
            writer.WriteString("indexFormat", SkinningCoreIndexFormat.ToString());
            writer.WriteStartArray("bones");
            for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", boneIndex);
                writer.WriteString("name", bones[boneIndex].tfm.SceneNode?.Name ?? bones[boneIndex].tfm.Name);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("vertices");
            for (int vertexIndex = 0; vertexIndex < VertexCount; vertexIndex++)
            {
                uint index = (uint)vertexIndex;
                Vector3 position = GetPosition(index);
                Vector3 normal = GetNormal(index);
                Vector4 tangent = GetTangentWithSign(index);
                Vector2 uv = GetTexCoord(index, 0);
                int influenceCount = reader.ReadInfluences(vertexIndex, boneIndices, weights);

                writer.WriteStartArray();
                WriteVector3(writer, position);
                WriteVector3(writer, normal);
                writer.WriteStartArray();
                writer.WriteNumberValue(tangent.X);
                writer.WriteNumberValue(tangent.Y);
                writer.WriteNumberValue(tangent.Z);
                writer.WriteNumberValue(tangent.W);
                writer.WriteEndArray();
                writer.WriteStartArray();
                writer.WriteNumberValue(uv.X);
                writer.WriteNumberValue(uv.Y);
                writer.WriteEndArray();
                writer.WriteStartArray();
                for (int influence = 0; influence < influenceCount; influence++)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(boneIndices[influence]);
                    writer.WriteNumberValue(weights[influence]);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteStartArray();
                for (uint channel = 0; channel < TexCoordCount; channel++)
                {
                    Vector2 channelUv = GetTexCoord(index, channel);
                    writer.WriteStartArray();
                    writer.WriteNumberValue(channelUv.X);
                    writer.WriteNumberValue(channelUv.Y);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("indices");
            foreach (int index in indices)
                writer.WriteNumberValue(index);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (geometryRevision != GeometryRevision || !ReferenceEquals(bones, UtilizedBones))
            throw new InvalidOperationException("The source geometry or bone table changed during the diagnostic.");
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }

    private static void WriteVector3(Utf8JsonWriter writer, in Vector3 value)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }

    private static bool HasCompleteCpuData(XRDataBuffer? buffer)
        => buffer?.ClientSideSource is { } source &&
           source.Length >= (ulong)buffer.ElementCount * buffer.ElementSize;
}
