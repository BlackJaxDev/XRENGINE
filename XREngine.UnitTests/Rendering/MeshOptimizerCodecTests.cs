using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class MeshOptimizerCodecTests
{
    private IMeshOptimizerBackend _meshopt = null!;

    [OneTimeSetUp]
    public void InstallMeshProcessing()
    {
        if (MeshOptimizerBackendServices.Current is null)
            MeshOptimizerBackend.Register();
        _meshopt = MeshOptimizerBackendServices.Required;
    }

    private static byte[] SeededBytes(int vertexCount, int stride)
    {
        Random random = new(1234);
        byte[] bytes = new byte[vertexCount * stride];
        for (int v = 0; v < vertexCount; v++)
        {
            for (int b = 0; b < stride; b++)
                bytes[v * stride + b] = (byte)((v * (b + 1) / 3 + random.Next(0, 8)) & 0xFF);
        }
        return bytes;
    }

    private byte[] Encode(byte[] vertices, int stride)
    {
        int count = vertices.Length / stride;
        byte[] buffer = new byte[_meshopt.EncodeVertexBufferBound(count, stride)];
        int size = _meshopt.EncodeVertexBuffer(buffer, vertices, count, stride, level: 3, version: 1);
        return buffer.AsSpan(0, size).ToArray();
    }

    private static (uint[] Indices, float[] Positions, int VertexCount) CreateGrid(int quads)
    {
        int side = quads + 1;
        float[] positions = new float[side * side * 3];
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int v = (y * side + x) * 3;
                positions[v] = x;
                positions[v + 1] = y;
                positions[v + 2] = 0.0f;
            }
        }

        uint[] indices = new uint[quads * quads * 6];
        int i = 0;
        for (int y = 0; y < quads; y++)
        {
            for (int x = 0; x < quads; x++)
            {
                uint a = (uint)(y * side + x);
                uint b = a + 1;
                uint c = a + (uint)side;
                uint d = c + 1;
                indices[i++] = a; indices[i++] = b; indices[i++] = c;
                indices[i++] = b; indices[i++] = d; indices[i++] = c;
            }
        }
        return (indices, positions, side * side);
    }

    private static (uint, uint, uint) CanonicalTriangle(uint[] indices, int offset)
    {
        uint a = indices[offset], b = indices[offset + 1], c = indices[offset + 2];
        if (a <= b && a <= c)
            return (a, b, c);
        return b <= c ? (b, c, a) : (c, a, b);
    }

    [Test]
    [TestCase(8)]
    [TestCase(32)]
    public void VertexCodec_RoundTripsSeededBytes_Stride8And32(int stride)
    {
        byte[] source = SeededBytes(500, stride);
        byte[] encoded = Encode(source, stride);
        byte[] decoded = new byte[source.Length];

        _meshopt.DecodeVertexBuffer(decoded, 500, stride, encoded);

        decoded.ShouldBe(source);
    }

    [Test]
    public void VertexCodec_EncodesVersion1()
        => _meshopt.DecodeVertexVersion(Encode(SeededBytes(100, 8), stride: 8)).ShouldBe(1);

    [Test]
    public void VertexCodec_CorruptBuffer_ThrowsInvalidData()
    {
        byte[] encoded = Encode(SeededBytes(100, 8), stride: 8);
        encoded[0] = 0xFF;
        byte[] decoded = new byte[800];

        Should.Throw<InvalidDataException>(() => _meshopt.DecodeVertexBuffer(decoded, 100, 8, encoded));
        Should.Throw<InvalidDataException>(() => _meshopt.DecodeVertexVersion(encoded));
    }

    [Test]
    public void VertexCodec_StrideNotMultipleOf4_Throws()
    {
        byte[] source = new byte[60];
        byte[] destination = new byte[1024];

        Should.Throw<ArgumentException>(() => _meshopt.EncodeVertexBuffer(destination, source, 10, 6));
        Should.Throw<ArgumentException>(() => _meshopt.EncodeVertexBuffer(destination, new byte[260 * 2], 2, 260));
    }

    [Test]
    public void IndexCodec_RoundTripsGridMeshTriangles()
    {
        (uint[] indices, _, int vertexCount) = CreateGrid(16);
        uint[] optimized = new uint[indices.Length];
        _meshopt.OptimizeVertexCache(optimized, indices, vertexCount);

        byte[] buffer = new byte[_meshopt.EncodeIndexBufferBound(optimized.Length, vertexCount)];
        int size = _meshopt.EncodeIndexBuffer(buffer, optimized);
        uint[] decoded = new uint[optimized.Length];
        _meshopt.DecodeIndexBuffer(decoded, buffer.AsSpan(0, size));

        // The encoder may rotate the corner order of a triangle, so compare each triangle from its smallest index.
        for (int i = 0; i < optimized.Length; i += 3)
            CanonicalTriangle(decoded, i).ShouldBe(CanonicalTriangle(optimized, i));
        optimized.OrderBy(static v => v).ShouldBe(indices.OrderBy(static v => v));
    }

    [Test]
    public void FilterQuat12_RoundTrip_AngleErrorBelowHalfDegree()
    {
        const int count = 10000;
        Random random = new(7);
        float[] input = new float[count * 4];
        for (int i = 0; i < count; i++)
        {
            Vector4 q;
            do
            {
                q = new Vector4(
                    (float)(random.NextDouble() * 2 - 1),
                    (float)(random.NextDouble() * 2 - 1),
                    (float)(random.NextDouble() * 2 - 1),
                    (float)(random.NextDouble() * 2 - 1));
            }
            while (q.LengthSquared() < 0.01f || q.LengthSquared() > 1.0f);
            q = Vector4.Normalize(q);
            input[i * 4] = q.X; input[i * 4 + 1] = q.Y; input[i * 4 + 2] = q.Z; input[i * 4 + 3] = q.W;
        }

        byte[] buffer = new byte[count * 8];
        _meshopt.EncodeFilterQuat(buffer, count, 8, 12, input);
        _meshopt.DecodeFilterQuat(buffer, count, 8);

        ReadOnlySpan<short> decoded = MemoryMarshal.Cast<byte, short>(buffer);
        double worstDegrees = 0.0;
        for (int i = 0; i < count; i++)
        {
            Vector4 d = Vector4.Normalize(new Vector4(decoded[i * 4], decoded[i * 4 + 1], decoded[i * 4 + 2], decoded[i * 4 + 3]));
            Vector4 s = new(input[i * 4], input[i * 4 + 1], input[i * 4 + 2], input[i * 4 + 3]);
            double dot = Math.Min(1.0, Math.Abs(Vector4.Dot(d, s)));
            worstDegrees = Math.Max(worstDegrees, 2.0 * Math.Acos(dot) * 180.0 / Math.PI);
        }

        worstDegrees.ShouldBeLessThan(0.5);
    }

    [Test]
    public void FilterExp16Shared_RoundTrip_ErrorBelow1e4OfComponentMax()
    {
        const int count = 5000;
        Random random = new(11);
        float[] input = new float[count * 3];
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)Math.Pow(10.0, -4.0 + 5.0 * random.NextDouble());

        byte[] buffer = new byte[count * 12];
        _meshopt.EncodeFilterExp(buffer, count, 12, 16, input, MeshOptimizerExpMode.SharedComponent);
        _meshopt.DecodeFilterExp(buffer, count, 12);

        ReadOnlySpan<float> decoded = MemoryMarshal.Cast<byte, float>(buffer);
        // A shared exponent fixes one absolute step for each component, so the error is measured against the largest value of that component.
        float[] componentMax = new float[3];
        for (int i = 0; i < input.Length; i++)
            componentMax[i % 3] = Math.Max(componentMax[i % 3], input[i]);

        double worst = 0.0;
        for (int i = 0; i < input.Length; i++)
            worst = Math.Max(worst, Math.Abs(decoded[i] - input[i]) / componentMax[i % 3]);

        worst.ShouldBeLessThan(1e-4);
    }

    [Test]
    public void FilterColor8_RoundTrip_ErrorAtMostTwoLevels()
    {
        const int count = 4096;
        Random random = new(5);
        float[] input = new float[count * 4];
        for (int i = 0; i < input.Length; i++)
            input[i] = random.Next(0, 256) / 255.0f;

        byte[] buffer = new byte[count * 4];
        _meshopt.EncodeFilterColor(buffer, count, 4, 8, input);
        _meshopt.DecodeFilterColor(buffer, count, 4);

        int worst = 0;
        for (int i = 0; i < input.Length; i++)
            worst = Math.Max(worst, Math.Abs(buffer[i] - (int)MathF.Round(input[i] * 255.0f)));

        worst.ShouldBeLessThanOrEqualTo(2);
    }

    [Test]
    public void SpatialClusterPoints_ReturnsPermutationInRunsOfClusterSize()
    {
        const int count = 1003;
        Random random = new(3);
        float[] positions = new float[count * 3];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = (float)random.NextDouble() * 10.0f;

        uint[] clustered = new uint[count];
        _meshopt.SpatialClusterPoints(clustered, positions, count, 12, 8);

        clustered.Distinct().Count().ShouldBe(count);
        clustered.Max().ShouldBe((uint)count - 1u);
    }

    [Test]
    public void SpatialSortRemap_ReturnsPermutation()
    {
        const int count = 200;
        Random random = new(9);
        float[] positions = new float[count * 3];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = (float)random.NextDouble();

        uint[] remap = new uint[count];
        _meshopt.SpatialSortRemap(remap, positions, count, 12);

        remap.Distinct().Count().ShouldBe(count);
        remap.Max().ShouldBe((uint)count - 1u);
    }

    [Test]
    public void GeneratePositionRemap_WeldsDuplicatePositions()
    {
        float[] positions =
        [
            0, 0, 0,
            1, 0, 0,
            0, 0, 0,
            2, 0, 0,
            1, 0, 0,
        ];
        uint[] remap = new uint[5];

        _meshopt.GeneratePositionRemap(remap, positions, 5, 12);

        remap.ShouldBe([0u, 1u, 0u, 3u, 1u]);
    }

    [Test]
    public void FilterIndexBuffer_DropsDegenerateAndDuplicateTriangles()
    {
        float[] vertices =
        [
            0, 0, 0,
            1, 0, 0,
            0, 1, 0,
            1, 1, 0,
            0, 0, 0,
        ];
        uint[] indices =
        [
            0, 1, 2,
            0, 1, 2,
            3, 3, 2,
            4, 1, 2,
            2, 1, 0,
            1, 3, 2,
        ];
        uint[] result = new uint[indices.Length];

        int count = _meshopt.FilterIndexBuffer(result, indices, vertices, 5, 12, 12);

        // The duplicate, the degenerate and the triangle that repeats by position are dropped; opposite winding stays.
        count.ShouldBe(9);
        result.AsSpan(0, 3).ToArray().ShouldBe([0u, 1u, 2u]);
    }

    [Test]
    public void Simplify_OutputIndicesAreSubsetOfInputVertices()
    {
        (uint[] indices, float[] positions, int vertexCount) = CreateGrid(63);
        uint[] work = (uint[])indices.Clone();
        int target = indices.Length / 4;

        int count = _meshopt.Simplify(work, positions, vertexCount, target, 1.0f, MeshOptimizerSimplifyOptions.None, out _);

        count.ShouldBeGreaterThan(0);
        count.ShouldBeLessThanOrEqualTo(indices.Length);
        HashSet<uint> inputVertices = [.. indices];
        for (int i = 0; i < count; i++)
        {
            work[i].ShouldBeLessThan((uint)vertexCount);
            inputVertices.ShouldContain(work[i]);
        }
    }
}
