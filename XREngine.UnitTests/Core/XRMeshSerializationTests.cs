using MemoryPack;
using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class XRMeshSerializationTests
{
    [Test]
    public void CookedBinarySerializer_RoundTrips_XRMesh_BufferBytes()
    {
        XRMesh original = CreateSampleMesh();

        byte[] bytes = CookedBinarySerializer.Serialize(original);
        bytes.Length.ShouldBeGreaterThan(0);

        XRMesh? clone = CookedBinarySerializer.Deserialize(typeof(XRMesh), bytes) as XRMesh;
        clone.ShouldNotBeNull();

        AssertMeshesEquivalent(original, clone!);
    }

    [Test]
    public void MemoryPackSerializer_RoundTrips_XRMesh_BufferBytes()
    {
        XRMesh original = CreateSampleMesh();

        byte[] bytes = MemoryPackSerializer.Serialize(original);
        bytes.Length.ShouldBeGreaterThan(0);

        XRMesh? clone = MemoryPackSerializer.Deserialize<XRMesh>(bytes);
        clone.ShouldNotBeNull();

        AssertMeshesEquivalent(original, clone!);
    }

    [Test]
    public void YamlSerializer_RoundTrips_XRMesh_BufferBytes()
    {
        XRMesh original = CreateSampleMesh();

        string yaml = AssetManager.Serializer.Serialize(original, typeof(XRMesh));
        yaml.ShouldContain("Payload:");

        XRMesh? clone = AssetManager.Deserializer.Deserialize<XRMesh>(yaml);
        clone.ShouldNotBeNull();

        AssertMeshesEquivalent(original, clone!);
    }

    [Test]
    public void RuntimeCookedBinarySerializer_Reads_LegacyMeshWithoutMeshletFooter()
    {
        XRMesh original = CreateSampleMesh();

        byte[] bytes = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => RuntimeCookedBinarySerializer.Serialize(original));
        bytes[^1].ShouldBe((byte)0);

        byte[] legacyBytes = bytes[..^1];
        XRMesh? clone = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => RuntimeCookedBinarySerializer.Deserialize(typeof(XRMesh), legacyBytes) as XRMesh);

        clone.ShouldNotBeNull();
        clone!.MeshletPayload.ShouldBeNull();
        AssertMeshesEquivalent(original, clone);
    }

    [Test]
    public void RuntimeCookedBinarySerializer_Reads_LegacyArrayMeshPayload()
    {
        XRMesh original = CreateSampleMesh();
        byte[] legacyBytes = CreateLegacyArrayMeshPayload(original);

        XRMesh? clone = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => RuntimeCookedBinarySerializer.Deserialize(typeof(XRMesh), legacyBytes) as XRMesh);

        clone.ShouldNotBeNull();
        clone!.MeshletPayload.ShouldBeNull();
        AssertMeshesEquivalent(original, clone);
    }

    [Test]
    public void YamlSerializer_Reads_LegacyArrayMeshPayload()
    {
        XRMesh original = CreateSampleMesh();
        byte[] legacyBytes = CreateLegacyArrayMeshPayload(original);
        string yaml = BuildMeshEnvelopeYaml(original, legacyBytes);

        XRMesh? clone = AssetManager.Deserializer.Deserialize<XRMesh>(yaml);

        clone.ShouldNotBeNull();
        clone!.MeshletPayload.ShouldBeNull();
        AssertMeshesEquivalent(original, clone);
    }

    [Test]
    public void YamlSerializer_Reads_CookedBinaryWrappedRuntimeMeshPayload()
    {
        XRMesh original = CreateSampleMesh();
        byte[] wrappedPayload = CookedBinarySerializer.Serialize(original);
        string yaml = BuildMeshEnvelopeYaml(original, wrappedPayload);

        XRMesh? clone = AssetManager.Deserializer.Deserialize<XRMesh>(yaml);

        clone.ShouldNotBeNull();
        clone!.MeshletPayload.ShouldBeNull();
        AssertMeshesEquivalent(original, clone);
    }

    [Test]
    public void YamlSerializer_CorruptCookedMeshPayload_ReturnsPlaceholderAndRecordsDiagnostic()
    {
        AssetDiagnostics.ClearTrackedMissingAssets();
        _ = AssetDiagnostics.ConsumePendingDisplayFlag();

        try
        {
            XRMesh original = CreateSampleMesh();
            byte[] corruptPayload = [25, 0];
            string yaml = BuildMeshEnvelopeYaml(original, corruptPayload);

            XRMesh? clone = AssetManager.Deserializer.Deserialize<XRMesh>(yaml);

            clone.ShouldNotBeNull();
            clone!.Name.ShouldStartWith("FailedCookedMesh_");
            clone.VertexCount.ShouldBe(0);

            AssetDiagnostics.ConsumePendingDisplayFlag().ShouldBeTrue();
            AssetDiagnostics.MissingAssetInfo info = AssetDiagnostics.GetTrackedMissingAssets().ShouldHaveSingleItem();
            info.Category.ShouldBe("XRMesh Cooked Payload");
            info.AssetPath.ShouldContain("<inline XRMesh payload");
            info.LastContext.ShouldNotBeNull();
            info.LastContext!.ShouldContain(original.ID.ToString("D"));
            info.LastContext.ShouldContain("Reimport the source FBX/model");
        }
        finally
        {
            AssetDiagnostics.ClearTrackedMissingAssets();
            _ = AssetDiagnostics.ConsumePendingDisplayFlag();
        }
    }

    [Test]
    public void XRMesh_Clone_PreservesBufferBindingMetadata()
    {
        XRMesh original = CreateSampleMesh();

        XRMesh clone = original.Clone();

        AssertMeshesEquivalent(original, clone);
    }

    private static XRMesh CreateSampleMesh()
    {
        XRMesh mesh = new(
        [
            new VertexTriangle(
                new Vertex(new Vector3(0.0f, 0.0f, 0.0f), Vector3.UnitZ, new Vector2(0.0f, 0.0f), new Vector4(1.0f, 0.0f, 0.0f, 1.0f)),
                new Vertex(new Vector3(1.0f, 0.0f, 0.0f), Vector3.UnitZ, new Vector2(1.0f, 0.0f), new Vector4(0.0f, 1.0f, 0.0f, 1.0f)),
                new Vertex(new Vector3(0.0f, 1.0f, 0.0f), Vector3.UnitZ, new Vector2(0.0f, 1.0f), new Vector4(0.0f, 0.0f, 1.0f, 1.0f)))
        ]);

        mesh.Name = "SerializationMesh";
        return mesh;
    }

    private static string BuildMeshEnvelopeYaml(XRMesh mesh, byte[] payload)
    {
        string payloadYaml = AssetManager.Serializer.Serialize(new DataSource(payload) { PreferCompressedYaml = true });
        return string.Join(
            Environment.NewLine,
            $"ID: {mesh.ID}",
            "__assetType: XREngine.Rendering.XRMesh",
            "Format: CookedBinary",
            "Version: 1",
            "Payload:",
            IndentYaml(payloadYaml, 2));
    }

    private static unsafe byte[] CreateLegacyArrayMeshPayload(XRMesh mesh)
    {
        const byte runtimeCookedCustomObjectMarker = 25;
        byte[] buffer = new byte[64 * 1024];

        fixed (byte* ptr = buffer)
        {
            using RuntimeCookedBinaryWriter writer = new(ptr, buffer.Length);
            writer.Write(runtimeCookedCustomObjectMarker);
            writer.Write(typeof(XRMesh).AssemblyQualifiedName ?? typeof(XRMesh).FullName ?? nameof(XRMesh));

            writer.WriteValue(mesh.ID);
            writer.WriteValue(mesh.Name);
            writer.WriteValue(mesh.FilePath);
            writer.WriteValue(mesh.OriginalPath);
            writer.WriteValue(mesh.OriginalLastWriteTimeUtc);

            WriteLegacyArrayMeshBody(writer, mesh);
            return buffer[..(int)writer.Position];
        }
    }

    private static void WriteLegacyArrayMeshBody(RuntimeCookedBinaryWriter writer, XRMesh mesh)
    {
        int vertexCount = mesh.VertexCount;
        Vector3[] positions = new Vector3[vertexCount];
        Vector3[]? normals = mesh.HasNormals ? new Vector3[vertexCount] : null;
        Vector3[]? tangents = mesh.HasTangents ? new Vector3[vertexCount] : null;
        Vector4[][]? colors = mesh.ColorCount > 0 ? CreateColorArrays(mesh, vertexCount) : null;
        Vector2[][]? texCoords = mesh.TexCoordCount > 0 ? CreateTexCoordArrays(mesh, vertexCount) : null;

        for (int i = 0; i < vertexCount; i++)
        {
            uint index = (uint)i;
            positions[i] = mesh.GetPosition(index);
            if (normals is not null)
                normals[i] = mesh.GetNormal(index);
            if (tangents is not null)
                tangents[i] = mesh.GetTangent(index);
        }

        writer.WriteValue(positions);
        writer.WriteValue(normals);
        writer.WriteValue(tangents);
        writer.WriteValue(colors);
        writer.WriteValue(texCoords);
        writer.WriteValue(CreateTriangleIndexArray(mesh));
        writer.WriteValue(CreateLineIndexArray(mesh));
        writer.WriteValue(mesh.Points?.ToArray());
        writer.WriteValue(mesh.Bounds.Min);
        writer.WriteValue(mesh.Bounds.Max);
        writer.WriteValue(mesh.Type);
    }

    private static Vector4[][] CreateColorArrays(XRMesh mesh, int vertexCount)
    {
        Vector4[][] colors = new Vector4[(int)mesh.ColorCount][];
        for (uint channel = 0; channel < mesh.ColorCount; channel++)
        {
            Vector4[] values = new Vector4[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                values[i] = mesh.GetColor((uint)i, channel);

            colors[(int)channel] = values;
        }

        return colors;
    }

    private static Vector2[][] CreateTexCoordArrays(XRMesh mesh, int vertexCount)
    {
        Vector2[][] texCoords = new Vector2[(int)mesh.TexCoordCount][];
        for (uint channel = 0; channel < mesh.TexCoordCount; channel++)
        {
            Vector2[] values = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                values[i] = mesh.GetTexCoord((uint)i, channel);

            texCoords[(int)channel] = values;
        }

        return texCoords;
    }

    private static int[]? CreateTriangleIndexArray(XRMesh mesh)
    {
        if (mesh.Triangles is not { Count: > 0 } triangles)
            return null;

        int[] indices = new int[triangles.Count * 3];
        for (int i = 0; i < triangles.Count; i++)
        {
            int index = i * 3;
            indices[index] = triangles[i].Point0;
            indices[index + 1] = triangles[i].Point1;
            indices[index + 2] = triangles[i].Point2;
        }

        return indices;
    }

    private static int[]? CreateLineIndexArray(XRMesh mesh)
    {
        if (mesh.Lines is not { Count: > 0 } lines)
            return null;

        int[] indices = new int[lines.Count * 2];
        for (int i = 0; i < lines.Count; i++)
        {
            int index = i * 2;
            indices[index] = lines[i].Point0;
            indices[index + 1] = lines[i].Point1;
        }

        return indices;
    }

    private static string IndentYaml(string yaml, int spaces)
    {
        string prefix = new(' ', spaces);
        string normalized = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n', '\r');
        string[] lines = normalized.Split('\n');
        StringBuilder builder = new();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                builder.AppendLine();
            builder.Append(prefix).Append(lines[i]);
        }

        return builder.ToString();
    }

    private static void AssertMeshesEquivalent(XRMesh expected, XRMesh actual)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.Type.ShouldBe(expected.Type);
        actual.VertexCount.ShouldBe(expected.VertexCount);
        actual.Interleaved.ShouldBe(expected.Interleaved);
        actual.InterleavedStride.ShouldBe(expected.InterleavedStride);
        actual.PositionOffset.ShouldBe(expected.PositionOffset);
        actual.NormalOffset.ShouldBe(expected.NormalOffset);
        actual.TangentOffset.ShouldBe(expected.TangentOffset);
        actual.ColorOffset.ShouldBe(expected.ColorOffset);
        actual.TexCoordOffset.ShouldBe(expected.TexCoordOffset);
        actual.ColorCount.ShouldBe(expected.ColorCount);
        actual.TexCoordCount.ShouldBe(expected.TexCoordCount);
        actual.Bounds.Min.ShouldBe(expected.Bounds.Min);
        actual.Bounds.Max.ShouldBe(expected.Bounds.Max);

        (actual.Triangles?.Count ?? 0).ShouldBe(expected.Triangles?.Count ?? 0);
        (actual.Lines?.Count ?? 0).ShouldBe(expected.Lines?.Count ?? 0);
        (actual.Points?.Count ?? 0).ShouldBe(expected.Points?.Count ?? 0);

        actual.Buffers.Count.ShouldBe(expected.Buffers.Count);
        foreach (KeyValuePair<string, XRDataBuffer> kvp in (IEnumerable<KeyValuePair<string, XRDataBuffer>>)expected.Buffers)
        {
            actual.Buffers.ContainsKey(kvp.Key).ShouldBeTrue();

            XRDataBuffer expectedBuffer = kvp.Value;
            XRDataBuffer actualBuffer = actual.Buffers[kvp.Key];

            actualBuffer.AttributeName.ShouldBe(expectedBuffer.AttributeName);
            actualBuffer.Target.ShouldBe(expectedBuffer.Target);
            actualBuffer.ComponentType.ShouldBe(expectedBuffer.ComponentType);
            actualBuffer.ComponentCount.ShouldBe(expectedBuffer.ComponentCount);
            actualBuffer.ElementCount.ShouldBe(expectedBuffer.ElementCount);
            actualBuffer.Normalize.ShouldBe(expectedBuffer.Normalize);
            actualBuffer.Integral.ShouldBe(expectedBuffer.Integral);
            actualBuffer.PadEndingToVec4.ShouldBe(expectedBuffer.PadEndingToVec4);
            actualBuffer.Length.ShouldBe(expectedBuffer.Length);

            uint logicalByteLength = expectedBuffer.ElementCount * expectedBuffer.ElementSize;
            actualBuffer.GetRawBytes(logicalByteLength).ShouldBe(expectedBuffer.GetRawBytes(logicalByteLength));
        }
    }
}
