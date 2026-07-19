using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;
using System.Numerics;
using XREngine.Rendering.Shaders;

namespace XREngine.UnitTests.Rendering;

/// <summary>Hardware parity tests for the production compact-node BVH ray shader.</summary>
[TestFixture]
public sealed class GpuBvhRayParityIntegrationTests : GpuTestBase
{
    private const uint InvalidIndex = uint.MaxValue;
    private const int NodeScalars = 12;
    private const int TriangleScalars = 16;
    private const int RayScalars = 8;
    private const int HitScalars = 8;

    [Test]
    public unsafe void CompactNodeClosestHit_MatchesCpuBruteForceInScalarAndPacketModes()
    {
        RunWithGLContext(gl =>
        {
            Triangle[] triangles = CreateTriangles(96);
            Ray[] rays = CreateRays(triangles, hitCount: 48, missCount: 8);
            uint[] nodes = BuildCompactTree(triangles, leafCapacity: 4);

            RayResult scalar = Dispatch(gl, nodes, triangles, rays, packetWidth: 32, packetMode: false, maxStackDepth: 64);
            RayResult packet = Dispatch(gl, nodes, triangles, rays, packetWidth: 7, packetMode: true, maxStackDepth: 64);

            for (int rayIndex = 0; rayIndex < rays.Length; ++rayIndex)
            {
                CpuHit expected = TraceCpu(rays[rayIndex], triangles);
                AssertHit(expected, scalar.Hits[rayIndex], $"scalar ray {rayIndex}");
                AssertHit(expected, packet.Hits[rayIndex], $"packet ray {rayIndex}");
            }

            scalar.Diagnostics.TraceCount.ShouldBe((uint)rays.Length);
            packet.Diagnostics.TraceCount.ShouldBe((uint)rays.Length);
            scalar.Diagnostics.StackOverflows.ShouldBe(0u);
            packet.Diagnostics.StackOverflows.ShouldBe(0u);
        }, timeoutMs: 60_000);
    }

    [Test]
    public unsafe void StackPressure_ReportsAndConservativelyRecoversWithoutChangingClosestHit()
    {
        RunWithGLContext(gl =>
        {
            Triangle[] triangles =
            [
                Triangle.AtZ(3f, 100u),
                Triangle.AtZ(7f, 200u),
            ];
            Ray[] rays = [new(Vector3.Zero, Vector3.UnitZ, 0f, 100f)];
            uint[] nodes = BuildPressureTree(triangles);

            RayResult result = Dispatch(gl, nodes, triangles, rays, packetWidth: 1, packetMode: true, maxStackDepth: 1);

            AssertHit(TraceCpu(rays[0], triangles), result.Hits[0], "forced stack recovery");
            result.Diagnostics.MaxStackOccupancy.ShouldBe(1u);
            result.Diagnostics.StackOverflows.ShouldBeGreaterThan(0u);
            result.Diagnostics.ConservativeRecoveries.ShouldBe(result.Diagnostics.StackOverflows);
        });
    }

    [Test]
    public unsafe void PacketMode_InactiveWorkgroupLanesNeverWriteHitRecords()
    {
        RunWithGLContext(gl =>
        {
            Triangle[] triangles = [Triangle.AtZ(4f, 77u)];
            Ray[] rays = Enumerable.Repeat(new Ray(Vector3.Zero, Vector3.UnitZ, 0f, 20f), 5).ToArray();
            uint[] nodes = BuildCompactTree(triangles, leafCapacity: 1);

            RayResult result = Dispatch(
                gl,
                nodes,
                triangles,
                rays,
                packetWidth: 5,
                packetMode: true,
                maxStackDepth: 64,
                outputCapacity: 32);

            for (int i = 0; i < rays.Length; ++i)
                result.Hits[i].TriangleIndex.ShouldBe(0u);
            for (int i = rays.Length; i < result.RawHitScalars.Length / HitScalars; ++i)
                result.RawHitScalars[i * HitScalars + 3].ShouldBe(0xDEADBEEFu, $"inactive lane {i} wrote a hit");
            result.Diagnostics.TraceCount.ShouldBe(5u);
        });
    }

    private static unsafe RayResult Dispatch(
        GL gl,
        uint[] nodes,
        Triangle[] triangles,
        Ray[] rays,
        uint packetWidth,
        bool packetMode,
        uint maxStackDepth,
        int? outputCapacity = null)
    {
        string path = Path.Combine(ShaderBasePath, "Compute", "BVH", "bvh_raycast.comp");
        string source = ShaderSourcePreprocessor.ResolveSource(File.ReadAllText(path), path);
        uint shader = CompileComputeShader(gl, source);
        uint program = CreateComputeProgram(gl, shader);
        uint[] buffers = new uint[5];
        int hitCapacity = outputCapacity ?? rays.Length;

        try
        {
            buffers[0] = CreateBuffer(gl, 0, PackRays(rays));
            buffers[1] = CreateBuffer(gl, 1, nodes);
            buffers[2] = CreateBuffer(gl, 2, PackTriangles(triangles));
            uint[] hits = Enumerable.Repeat(0xDEADBEEFu, Math.Max(1, hitCapacity * HitScalars)).ToArray();
            buffers[3] = CreateBuffer(gl, 3, hits);
            buffers[4] = CreateBuffer(gl, 4, new uint[4]);

            gl.UseProgram(program);
            SetUniform(gl, program, "uRayCount", (uint)rays.Length);
            SetUniform(gl, program, "uRootIndex", nodes[1]);
            SetUniform(gl, program, "uPacketWidth", packetWidth);
            SetUniform(gl, program, "uUsePacketMode", packetMode ? 1u : 0u);
            SetUniform(gl, program, "uAnyHitMode", 0u);
            SetUniform(gl, program, "uMaxStackDepth", maxStackDepth);
            SetUniform(gl, program, "uDiagnosticsEnabled", 1u);

            uint groups = packetMode
                ? ((uint)rays.Length + packetWidth - 1u) / packetWidth
                : ((uint)rays.Length + 31u) / 32u;
            gl.DispatchCompute(Math.Max(groups, 1u), 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.BufferUpdateBarrierBit);

            uint[] rawHits = ReadBuffer(gl, buffers[3], hits.Length);
            uint[] diagnostics = ReadBuffer(gl, buffers[4], 4);
            GpuHit[] decoded = new GpuHit[rays.Length];
            for (int i = 0; i < decoded.Length; ++i)
            {
                int offset = i * HitScalars;
                decoded[i] = new(
                    BitConverter.UInt32BitsToSingle(rawHits[offset]),
                    rawHits[offset + 1],
                    rawHits[offset + 2],
                    rawHits[offset + 3]);
            }

            return new RayResult(
                decoded,
                rawHits,
                new(diagnostics[0], diagnostics[1], diagnostics[2], diagnostics[3]));
        }
        finally
        {
            foreach (uint buffer in buffers)
                if (buffer != 0u) gl.DeleteBuffer(buffer);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
    }

    private static uint[] BuildCompactTree(Triangle[] triangles, int leafCapacity)
    {
        List<Node> nodes = [];
        uint root = BuildNode(nodes, triangles, 0, triangles.Length, InvalidIndex, leafCapacity);
        uint[] packed = new uint[4 + nodes.Count * NodeScalars];
        packed[0] = (uint)nodes.Count;
        packed[1] = root;
        packed[2] = NodeScalars;
        packed[3] = (uint)leafCapacity;
        for (int i = 0; i < nodes.Count; ++i)
            PackNode(nodes[i], packed.AsSpan(4 + i * NodeScalars, NodeScalars));
        return packed;
    }

    private static uint BuildNode(List<Node> nodes, Triangle[] triangles, int start, int count, uint parent, int leafCapacity)
    {
        uint index = (uint)nodes.Count;
        nodes.Add(default);
        Bounds bounds = triangles[start].Bounds;
        for (int i = 1; i < count; ++i)
            bounds = Bounds.Union(bounds, triangles[start + i].Bounds);

        if (count <= leafCapacity)
        {
            nodes[(int)index] = new(bounds, InvalidIndex, InvalidIndex, (uint)start, (uint)count, parent, 1u);
            return index;
        }

        int leftCount = count / 2;
        uint left = BuildNode(nodes, triangles, start, leftCount, index, leafCapacity);
        uint right = BuildNode(nodes, triangles, start + leftCount, count - leftCount, index, leafCapacity);
        nodes[(int)index] = new(bounds, left, right, (uint)start, (uint)count, parent, 0u);
        return index;
    }

    private static uint[] BuildPressureTree(Triangle[] triangles)
    {
        Bounds leftBounds = triangles[0].Bounds.Expanded(0.25f);
        Bounds rightBounds = triangles[1].Bounds.Expanded(0.25f);
        Bounds rootBounds = Bounds.Union(leftBounds, rightBounds);
        Node[] nodes =
        [
            new(rootBounds, 1u, 2u, 0u, 2u, InvalidIndex, 0u),
            new(leftBounds, InvalidIndex, InvalidIndex, 0u, 1u, 0u, 1u),
            new(rightBounds, InvalidIndex, InvalidIndex, 1u, 1u, 0u, 1u),
        ];
        uint[] packed = new uint[4 + nodes.Length * NodeScalars];
        packed[0] = (uint)nodes.Length;
        packed[1] = 0u;
        packed[2] = NodeScalars;
        packed[3] = 1u;
        for (int i = 0; i < nodes.Length; ++i)
            PackNode(nodes[i], packed.AsSpan(4 + i * NodeScalars, NodeScalars));
        return packed;
    }

    private static void PackNode(Node node, Span<uint> output)
    {
        output[0] = Bits(node.Bounds.Min.X);
        output[1] = Bits(node.Bounds.Min.Y);
        output[2] = Bits(node.Bounds.Min.Z);
        output[3] = node.Left;
        output[4] = Bits(node.Bounds.Max.X);
        output[5] = Bits(node.Bounds.Max.Y);
        output[6] = Bits(node.Bounds.Max.Z);
        output[7] = node.Right;
        output[8] = node.Start;
        output[9] = node.Count;
        output[10] = node.Parent;
        output[11] = node.Flags;
    }

    private static uint[] PackTriangles(Triangle[] triangles)
    {
        uint[] output = new uint[Math.Max(TriangleScalars, triangles.Length * TriangleScalars)];
        for (int i = 0; i < triangles.Length; ++i)
        {
            int offset = i * TriangleScalars;
            PackVector4(new(triangles[i].A, 1f), output.AsSpan(offset, 4));
            PackVector4(new(triangles[i].B, 1f), output.AsSpan(offset + 4, 4));
            PackVector4(new(triangles[i].C, 1f), output.AsSpan(offset + 8, 4));
            output[offset + 12] = triangles[i].ObjectId;
            output[offset + 13] = (uint)i;
        }
        return output;
    }

    private static uint[] PackRays(Ray[] rays)
    {
        uint[] output = new uint[Math.Max(RayScalars, rays.Length * RayScalars)];
        for (int i = 0; i < rays.Length; ++i)
        {
            int offset = i * RayScalars;
            PackVector4(new(rays[i].Origin, rays[i].MinDistance), output.AsSpan(offset, 4));
            PackVector4(new(rays[i].Direction, rays[i].MaxDistance), output.AsSpan(offset + 4, 4));
        }
        return output;
    }

    private static void PackVector4(Vector4 value, Span<uint> output)
    {
        output[0] = Bits(value.X);
        output[1] = Bits(value.Y);
        output[2] = Bits(value.Z);
        output[3] = Bits(value.W);
    }

    private static Triangle[] CreateTriangles(int count)
        => Enumerable.Range(0, count).Select(i =>
        {
            float x = (i % 12 - 5.5f) * 1.5f;
            float y = (i / 12 % 8 - 3.5f) * 1.5f;
            float z = 4f + (i % 7) * 1.25f;
            return new Triangle(
                new(x - 0.45f, y - 0.4f, z),
                new(x + 0.45f, y - 0.4f, z),
                new(x, y + 0.5f, z),
                (uint)(1000 + i));
        }).ToArray();

    private static Ray[] CreateRays(Triangle[] triangles, int hitCount, int missCount)
    {
        List<Ray> rays = new(hitCount + missCount);
        for (int i = 0; i < hitCount; ++i)
        {
            Vector3 target = triangles[i].Centroid;
            Vector3 origin = new(0.013f * (i + 1), -0.017f * (i + 1), -2f);
            rays.Add(new(origin, Vector3.Normalize(target - origin), 0f, 100f));
        }
        for (int i = 0; i < missCount; ++i)
            rays.Add(new(Vector3.Zero, Vector3.Normalize(new Vector3(20 + i, 30, 1)), 0f, 100f));
        return rays.ToArray();
    }

    private static CpuHit TraceCpu(Ray ray, Triangle[] triangles)
    {
        CpuHit result = new(ray.MaxDistance, InvalidIndex, InvalidIndex);
        for (int i = 0; i < triangles.Length; ++i)
        {
            if (!Intersect(ray, triangles[i], out float distance) || distance >= result.Distance)
                continue;
            result = new(distance, triangles[i].ObjectId, (uint)i);
        }
        return result;
    }

    private static bool Intersect(Ray ray, Triangle triangle, out float distance)
    {
        distance = 0f;
        Vector3 edge1 = triangle.B - triangle.A;
        Vector3 edge2 = triangle.C - triangle.A;
        Vector3 p = Vector3.Cross(ray.Direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-6f)
            return false;
        float inverse = 1f / determinant;
        Vector3 t = ray.Origin - triangle.A;
        float u = Vector3.Dot(t, p) * inverse;
        if (u < 0f || u > 1f)
            return false;
        Vector3 q = Vector3.Cross(t, edge1);
        float v = Vector3.Dot(ray.Direction, q) * inverse;
        if (v < 0f || u + v > 1f)
            return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= ray.MinDistance && distance <= ray.MaxDistance;
    }

    private static void AssertHit(CpuHit expected, GpuHit actual, string context)
    {
        actual.TriangleIndex.ShouldBe(expected.TriangleIndex, context);
        if (expected.TriangleIndex == InvalidIndex)
            return;
        actual.ObjectId.ShouldBe(expected.ObjectId, context);
        actual.Distance.ShouldBe(expected.Distance, 1e-4f, context);
    }

    private static uint CreateBuffer(GL gl, uint binding, uint[] data)
    {
        uint buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, data.AsSpan(), BufferUsageARB.DynamicCopy);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, binding, buffer);
        return buffer;
    }

    private static unsafe uint[] ReadBuffer(GL gl, uint buffer, int count)
    {
        uint[] result = new uint[count];
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        uint* source = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
        for (int i = 0; i < count; ++i)
            result[i] = source[i];
        gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
        return result;
    }

    private static void SetUniform(GL gl, uint program, string name, uint value)
    {
        int location = gl.GetUniformLocation(program, name);
        if (location >= 0) gl.Uniform1(location, value);
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private readonly record struct Ray(Vector3 Origin, Vector3 Direction, float MinDistance, float MaxDistance);
    private readonly record struct GpuHit(float Distance, uint ObjectId, uint FaceIndex, uint TriangleIndex);
    private readonly record struct CpuHit(float Distance, uint ObjectId, uint TriangleIndex);
    private readonly record struct RayDiagnostics(uint TraceCount, uint MaxStackOccupancy, uint StackOverflows, uint ConservativeRecoveries);
    private readonly record struct RayResult(GpuHit[] Hits, uint[] RawHitScalars, RayDiagnostics Diagnostics);
    private readonly record struct Node(Bounds Bounds, uint Left, uint Right, uint Start, uint Count, uint Parent, uint Flags);
    private readonly record struct Bounds(Vector3 Min, Vector3 Max)
    {
        public Bounds Expanded(float amount) => new(Min - new Vector3(amount), Max + new Vector3(amount));
        public static Bounds Union(Bounds a, Bounds b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
    }
    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, uint ObjectId)
    {
        public Vector3 Centroid => (A + B + C) / 3f;
        public Bounds Bounds => new(Vector3.Min(A, Vector3.Min(B, C)), Vector3.Max(A, Vector3.Max(B, C)));
        public static Triangle AtZ(float z, uint objectId) => new(new(-1, -1, z), new(1, -1, z), new(0, 1, z), objectId);
    }
}
