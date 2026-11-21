using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The span overload of the canonical Advanced vertex codec must produce the same records as the
/// per-vertex overload, which reads the mesh through its accessors.
/// </summary>
[TestFixture]
public sealed class AdvancedPackedVertexCodecBulkTests
{
    [TestCase(true, true, 2, 2)]
    [TestCase(true, false, 1, 1)]
    [TestCase(false, true, 4, 0)]
    [TestCase(false, false, 0, 2)]
    public void BulkPack_MatchesPerVertexPack(bool normals, bool tangents, int texCoordSets, int colorSets)
    {
        XRMesh mesh = CreateMesh(97, normals, tangents, texCoordSets, colorSets, seed: 7);
        AdvancedDeformedVertex[] bulk = new AdvancedDeformedVertex[mesh.VertexCount];

        AdvancedPackedVertexCodec.Pack(mesh, 0u, bulk, firstSourceVertex: 0u);

        for (uint index = 0; index < (uint)mesh.VertexCount; ++index)
            bulk[index].ShouldBe(AdvancedPackedVertexCodec.Pack(mesh, index, index));
    }

    [Test]
    public void BulkPack_SubrangeUsesSourceVertexOffsets()
    {
        XRMesh mesh = CreateMesh(40, normals: true, tangents: true, texCoordSets: 1, colorSets: 1, seed: 3);
        AdvancedDeformedVertex[] bulk = new AdvancedDeformedVertex[10];

        AdvancedPackedVertexCodec.Pack(mesh, firstVertex: 12u, bulk, firstSourceVertex: 100u);

        for (uint index = 0; index < 10u; ++index)
            bulk[index].ShouldBe(AdvancedPackedVertexCodec.Pack(mesh, 12u + index, 100u + index));
    }

    [Test]
    public void BulkPack_RejectsRangePastTheMesh()
    {
        XRMesh mesh = CreateMesh(8, normals: true, tangents: true, texCoordSets: 1, colorSets: 1, seed: 1);
        Should.Throw<ArgumentOutOfRangeException>(
            () => AdvancedPackedVertexCodec.Pack(mesh, 4u, new AdvancedDeformedVertex[5], 0u));
    }

    [Test]
    public void DefaultAttributeFastPaths_KeepExactBits()
    {
        AdvancedPackedVertexCodec.PackHalf2(Vector2.Zero).ShouldBe(0u);
        AdvancedPackedVertexCodec.PackHalf2(new Vector2(-0.0f, 0.0f)).ShouldBe(0x8000u);
        AdvancedPackedVertexCodec.PackHalf2(new Vector2(0.0f, -0.0f)).ShouldBe(0x80000000u);
        AdvancedPackedVertexCodec.PackRgba8(Vector4.One).ShouldBe(uint.MaxValue);
        AdvancedPackedVertexCodec.PackRgba8(new Vector4(1.0f, 1.0f, 1.0f, 0.0f)).ShouldBe(0x00FFFFFFu);
        AdvancedPackedVertexCodec.PackRgba8(new Vector4(2.0f, 1.0f, 1.0f, 1.0f)).ShouldBe(uint.MaxValue);
    }

    /// <summary>Reports pack time per million vertices for both overloads. Run on demand.</summary>
    [Test, Explicit("Timing report, not a correctness check.")]
    public void BulkPack_ReportsTimePerMillionVertices()
    {
        const int vertexCount = 250_000;
        Report("2 UV sets, 1 color set", CreateMesh(vertexCount, normals: true, tangents: true, texCoordSets: 2, colorSets: 1, seed: 11));
        Report("1 UV set, no colors", CreateMesh(vertexCount, normals: true, tangents: true, texCoordSets: 1, colorSets: 0, seed: 12));
    }

    private static void Report(string label, XRMesh mesh)
    {
        int vertexCount = mesh.VertexCount;
        AdvancedDeformedVertex[] target = new AdvancedDeformedVertex[vertexCount];

        AdvancedPackedVertexCodec.Pack(mesh, 0u, target, 0u);
        for (uint index = 0; index < 1000u; ++index)
            target[index] = AdvancedPackedVertexCodec.Pack(mesh, index, index);

        double perVertexMs = Measure(() =>
        {
            for (uint index = 0; index < (uint)vertexCount; ++index)
                target[index] = AdvancedPackedVertexCodec.Pack(mesh, index, index);
        });
        double bulkMs = Measure(() => AdvancedPackedVertexCodec.Pack(mesh, 0u, target, 0u));

        double scale = 1_000_000.0 / vertexCount;
        TestContext.Out.WriteLine($"{label}: per-vertex {perVertexMs * scale:F1}, bulk {bulkMs * scale:F1} ms per million vertices");
    }

    private static double Measure(Action action)
    {
        double best = double.MaxValue;
        for (int run = 0; run < 5; ++run)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        return best;
    }

    private static XRMesh CreateMesh(int vertexCount, bool normals, bool tangents, int texCoordSets, int colorSets, int seed)
    {
        Random random = new(seed);
        List<Vertex> vertices = new(vertexCount);
        for (int index = 0; index < vertexCount; ++index)
        {
            Vertex vertex = new(NextVector3(random) * 10.0f);
            if (normals)
                vertex.Normal = Vector3.Normalize(NextVector3(random) + new Vector3(0.01f));
            if (tangents)
            {
                vertex.Tangent = Vector3.Normalize(NextVector3(random) + new Vector3(0.02f));
                vertex.BitangentSign = random.Next(2) == 0 ? -1.0f : 1.0f;
            }
            if (texCoordSets > 0)
            {
                vertex.TextureCoordinateSets = [];
                for (int set = 0; set < texCoordSets; ++set)
                    vertex.TextureCoordinateSets.Add(new Vector2(random.NextSingle() * 4.0f - 1.0f, random.NextSingle() * 4.0f - 1.0f));
            }
            if (colorSets > 0)
            {
                vertex.ColorSets = [];
                for (int set = 0; set < colorSets; ++set)
                    vertex.ColorSets.Add(new Vector4(random.NextSingle(), random.NextSingle(), random.NextSingle(), random.NextSingle()));
            }
            vertices.Add(vertex);
        }

        List<ushort> indices = new(vertexCount - vertexCount % 3);
        for (int index = 0; index + 2 < vertexCount; index += 3)
        {
            indices.Add((ushort)index);
            indices.Add((ushort)(index + 1));
            indices.Add((ushort)(index + 2));
        }
        return new XRMesh(vertices, indices);
    }

    private static Vector3 NextVector3(Random random)
        => new(random.NextSingle() * 2.0f - 1.0f, random.NextSingle() * 2.0f - 1.0f, random.NextSingle() * 2.0f - 1.0f);
}
