using MemoryPack;
using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
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