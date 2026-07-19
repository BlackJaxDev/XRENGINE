using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;
using System.Numerics;
using System.Text.RegularExpressions;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Hardware parity coverage for the production root-down scene-BVH frustum
/// shader. A deterministic CPU builder supplies compact nodes so failures are
/// isolated to traversal, command filtering, multi-view emission, or bounded
/// queue recovery rather than construction.
/// </summary>
[TestFixture]
public sealed class GpuBvhFrustumParityIntegrationTests : GpuTestBase
{
    private const int DrawMetadataUInts = 16;
    private const int BoundsLanes = 16;
    private const int CommandLanes = 20;
    private const int StatsUInts = 32;
    private const uint InvalidIndex = uint.MaxValue;

    private static readonly Vector4[] BoxFrustum =
    [
        new(1, 0, 0, 10), new(-1, 0, 0, 10),
        new(0, 1, 0, 10), new(0, -1, 0, 10),
        new(0, 0, 1, 10), new(0, 0, -1, 10),
    ];

    [TestCase(0u, 1u)]
    [TestCase(512u, 1u)]
    [TestCase(513u, 2u)]
    [TestCase(10_000u, 32u)]
    [TestCase(100_000u, 256u)]
    [TestCase(1_000_000u, 256u)]
    public void TraversalWorkgroupCount_IsPowerOfTwoAndBounded(uint commands, uint expected)
        => GpuBvhCullingDispatch.CalculateWorkgroupCount(commands).ShouldBe(expected);

    [Test]
    public unsafe void ProductionTraversal_MatchesCpuBruteForceAcrossPathologiesLeafSizesAndViews()
    {
        RunWithGLContext(gl =>
        {
            uint shader = 0;
            uint program = 0;
            try
            {
                string source = ResolveIncludes(LoadShaderSource(Path.Combine("Scene3D", "RenderPipeline", "bvh_frustum_cull.comp")));
                shader = CompileComputeShader(gl, source);
                program = CreateComputeProgram(gl, shader);

                SceneBounds[][] scenes =
                [
                    [],
                    [new(Vector3.Zero, Vector3.Zero)],
                    CreateDuplicateCenters(64),
                    CreateDegenerateBounds(65),
                    CreateGiantAndSmallBounds(129),
                    CreateInvalidBounds(67),
                    CreateClusteredBounds(257, seed: 0xB71),
                    CreateRandomBounds(513, seed: 0x5EED),
                    Translate(CreateRandomBounds(193, seed: 0xA11CE), new Vector3(7.5f, -3.0f, 2.0f)),
                ];

                foreach (uint leafCapacity in new uint[] { 1, 2, 4, 8, 16 })
                foreach (SceneBounds[] scene in scenes)
                foreach (int viewCount in new[] { 1, 2, 3 })
                {
                    TraversalResult result = Dispatch(gl, program, scene, leafCapacity, viewCount);
                    uint[] expected = CpuVisible(scene);

                    result.VisibleIds.Order().ToArray().ShouldBe(expected, $"leafCapacity={leafCapacity}, commands={scene.Length}, views={viewCount}");
                    for (int view = 0; view < viewCount; ++view)
                    {
                        uint[] expectedPerView = expected.Where(id => ViewIncludes(id, view)).ToArray();
                        result.PerViewVisibleIds[view].Order().ToArray().ShouldBe(
                            expectedPerView,
                            $"per-view parity failed for leafCapacity={leafCapacity}, commands={scene.Length}, view={view}/{viewCount}");
                    }
                }
            }
            finally
            {
                if (program != 0) gl.DeleteProgram(program);
                if (shader != 0) gl.DeleteShader(shader);
            }
        }, timeoutMs: 60_000);
    }

    [Test]
    public unsafe void QueuePressure_IsObservableAndConservativelyPreservesVisibility()
    {
        RunWithGLContext(gl =>
        {
            uint shader = 0;
            uint program = 0;
            try
            {
                string source = ResolveIncludes(LoadShaderSource(Path.Combine("Scene3D", "RenderPipeline", "bvh_frustum_cull.comp")));
                shader = CompileComputeShader(gl, source);
                program = CreateComputeProgram(gl, shader);

                SceneBounds[] scene = CreateQueuePressureBounds(4096);
                TraversalResult result = Dispatch(gl, program, scene, leafCapacity: 1, viewCount: 2, workgroupCount: 1u);

                result.VisibleIds.Order().ToArray().ShouldBe(CpuVisible(scene));
                (result.OverflowFlags & 2u).ShouldBe(2u, "bounded traversal queue pressure must be externally observable");
                result.QueueOverflowCount.ShouldBeGreaterThan(0u);
            }
            finally
            {
                if (program != 0) gl.DeleteProgram(program);
                if (shader != 0) gl.DeleteShader(shader);
            }
        }, timeoutMs: 60_000);
    }

    private static unsafe TraversalResult Dispatch(
        GL gl,
        uint program,
        SceneBounds[] scene,
        uint leafCapacity,
        int viewCount,
        uint? workgroupCount = null)
    {
        uint[] metadata = CreateMetadata(scene.Length);
        uint[] bounds = CreateBounds(scene);
        uint[] nodes = BuildCompactTree(scene, leafCapacity);
        uint[] morton = CreateIdentityMorton(scene.Length);
        uint[] output = new uint[Math.Max(1, scene.Length * CommandLanes)];
        uint[] count = new uint[3];
        uint[] overflow = new uint[1];
        uint[] stats = new uint[StatsUInts];
        uint[] viewDescriptors = CreateViewDescriptors(scene.Length, viewCount);
        uint[] viewConstants = new uint[Math.Max(72, viewCount * 72)];
        uint[] viewMasks = CreateViewMasks(scene.Length, viewCount);
        uint[] perViewVisible = new uint[Math.Max(1, scene.Length * viewCount)];
        uint[] perViewCounts = new uint[Math.Max(1, viewCount)];

        uint[] buffers = new uint[16];
        try
        {
            buffers[0] = CreateBuffer(gl, 0, metadata);
            buffers[1] = CreateBuffer(gl, 1, bounds);
            buffers[2] = CreateBuffer(gl, 2, output);
            buffers[3] = CreateBuffer(gl, 3, count);
            buffers[4] = CreateBuffer(gl, 4, overflow);
            buffers[5] = CreateBuffer(gl, 5, nodes);
            buffers[7] = CreateBuffer(gl, 7, morton);
            buffers[8] = CreateBuffer(gl, 8, stats);
            buffers[9] = CreateBuffer(gl, 9, new uint[1]);
            buffers[10] = CreateBuffer(gl, 10, output);
            buffers[11] = CreateBuffer(gl, 11, viewDescriptors);
            buffers[12] = CreateBuffer(gl, 12, viewConstants);
            buffers[13] = CreateBuffer(gl, 13, viewMasks);
            buffers[14] = CreateBuffer(gl, 14, perViewVisible);
            buffers[15] = CreateBuffer(gl, 15, perViewCounts);

            gl.UseProgram(program);
            float[] planes = BoxFrustum.SelectMany(p => new[] { p.X, p.Y, p.Z, p.W }).ToArray();
            SetUniform4(gl, program, "FrustumPlanes", planes);
            SetUniform4(gl, program, "ClusterPlanes", planes);
            SetUniform(gl, program, "UseClusterPlanes", 0u);
            SetUniform(gl, program, "UseClusterPlaneBuffer", 0u);
            SetUniform(gl, program, "ClusterPlaneOffset", 0u);
            SetUniform(gl, program, "ClusterPlaneStride", 0u);
            SetUniform(gl, program, "MaxRenderDistance", float.MaxValue);
            SetUniform(gl, program, "CameraLayerMask", uint.MaxValue);
            SetUniform(gl, program, "CurrentRenderPass", -1);
            SetUniform(gl, program, "InputCommandCount", scene.Length);
            SetUniform(gl, program, "MaxCulledCommands", Math.Max(1, scene.Length));
            SetUniform(gl, program, "DisabledFlagsMask", 0u);
            int camera = gl.GetUniformLocation(program, "CameraPosition");
            if (camera >= 0) gl.Uniform3(camera, 0f, 0f, 0f);
            SetUniform(gl, program, "StatsEnabled", 1u);
            SetUniform(gl, program, "OverflowDebugEnabled", 0u);
            SetUniform(gl, program, "ENABLE_CPU_GPU_COMPARE", 0u);
            SetUniform(gl, program, "ActiveViewCount", viewCount);
            SetUniform(gl, program, "UseHotCommands", 0u);

            gl.DispatchCompute(
                workgroupCount ?? GpuBvhCullingDispatch.CalculateWorkgroupCount((uint)scene.Length),
                1,
                1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.BufferUpdateBarrierBit);

            uint[] countResult = ReadBuffer(gl, buffers[3], count.Length);
            uint visibleCount = Math.Min(countResult[0], (uint)scene.Length);
            uint[] outputResult = ReadBuffer(gl, buffers[2], output.Length);
            uint[] visibleIds = new uint[visibleCount];
            for (int i = 0; i < visibleIds.Length; ++i)
                visibleIds[i] = outputResult[i * CommandLanes + 19];

            uint[] perViewCountResult = ReadBuffer(gl, buffers[15], perViewCounts.Length);
            uint[] perViewResult = ReadBuffer(gl, buffers[14], perViewVisible.Length);
            uint[][] perViewIds = new uint[viewCount][];
            for (int view = 0; view < viewCount; ++view)
            {
                int n = (int)Math.Min(perViewCountResult[view], (uint)scene.Length);
                perViewIds[view] = new uint[n];
                for (int i = 0; i < n; ++i)
                {
                    uint outputIndex = perViewResult[view * Math.Max(1, scene.Length) + i];
                    perViewIds[view][i] = outputResult[outputIndex * CommandLanes + 19];
                }
            }

            uint[] statsResult = ReadBuffer(gl, buffers[8], stats.Length);
            uint[] overflowResult = ReadBuffer(gl, buffers[4], overflow.Length);
            return new(visibleIds, perViewIds, overflowResult[0], statsResult[31]);
        }
        finally
        {
            foreach (uint buffer in buffers)
                if (buffer != 0) gl.DeleteBuffer(buffer);
        }
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

    private static void SetUniform(GL gl, uint program, string name, int value)
    {
        int location = gl.GetUniformLocation(program, name);
        if (location >= 0) gl.Uniform1(location, value);
    }

    private static void SetUniform(GL gl, uint program, string name, float value)
    {
        int location = gl.GetUniformLocation(program, name);
        if (location >= 0) gl.Uniform1(location, value);
    }

    private static void SetUniform4(GL gl, uint program, string name, float[] values)
    {
        int location = gl.GetUniformLocation(program, name);
        if (location >= 0) gl.Uniform4(location, 6, values.AsSpan());
    }

    private static uint[] CreateMetadata(int count)
    {
        uint[] result = new uint[Math.Max(DrawMetadataUInts, count * DrawMetadataUInts)];
        for (int i = 0; i < count; ++i)
        {
            int offset = i * DrawMetadataUInts;
            result[offset] = (uint)i;
            result[offset + 1] = (uint)i;
            result[offset + 6] = uint.MaxValue;
            result[offset + 7] = uint.MaxValue;
            result[offset + 11] = 1u;
            result[offset + 12] = 0u;
            result[offset + 14] = (uint)i;
            result[offset + 15] = (uint)i;
        }
        return result;
    }

    private static uint[] CreateBounds(SceneBounds[] scene)
    {
        uint[] result = new uint[Math.Max(BoundsLanes, scene.Length * BoundsLanes)];
        for (int i = 0; i < scene.Length; ++i)
        {
            SceneBounds bounds = scene[i];
            Vector3 center = bounds.SphereCenter;
            float radius = bounds.SphereRadius;
            int offset = i * BoundsLanes;
            result[offset] = FloatBits(center.X);
            result[offset + 1] = FloatBits(center.Y);
            result[offset + 2] = FloatBits(center.Z);
            result[offset + 3] = FloatBits(radius);
            result[offset + 4] = FloatBits(bounds.Min.X);
            result[offset + 5] = FloatBits(bounds.Min.Y);
            result[offset + 6] = FloatBits(bounds.Min.Z);
            result[offset + 8] = FloatBits(bounds.Max.X);
            result[offset + 9] = FloatBits(bounds.Max.Y);
            result[offset + 10] = FloatBits(bounds.Max.Z);
            result[offset + 12] = 1u;
        }
        return result;
    }

    private static uint[] CreateIdentityMorton(int count)
    {
        uint[] result = new uint[Math.Max(2, count * 2)];
        for (int i = 0; i < count; ++i)
        {
            result[i * 2] = (uint)i;
            result[i * 2 + 1] = (uint)i;
        }
        return result;
    }

    private static uint[] BuildCompactTree(SceneBounds[] scene, uint leafCapacity)
    {
        if (scene.Length == 0)
            return [0u, InvalidIndex, 12u, leafCapacity];

        List<CpuNode> nodes = [];
        uint root = BuildNode(nodes, scene, 0, scene.Length, InvalidIndex, Math.Max(1, (int)leafCapacity));
        uint[] packed = new uint[4 + nodes.Count * 12];
        packed[0] = (uint)nodes.Count;
        packed[1] = root;
        packed[2] = 12u;
        packed[3] = leafCapacity;
        for (int i = 0; i < nodes.Count; ++i)
            PackNode(nodes[i], packed.AsSpan(4 + i * 12, 12));
        return packed;
    }

    private static uint BuildNode(List<CpuNode> nodes, SceneBounds[] scene, int start, int count, uint parent, int leafCapacity)
    {
        uint index = (uint)nodes.Count;
        nodes.Add(default);
        SceneBounds combined = EffectiveBounds(scene[start]);
        for (int i = 1; i < count; ++i)
            combined = Union(combined, EffectiveBounds(scene[start + i]));

        if (count <= leafCapacity)
        {
            nodes[(int)index] = new(combined.Min, combined.Max, InvalidIndex, InvalidIndex, (uint)start, (uint)count, parent, 1u);
            return index;
        }

        int leftCount = count / 2;
        uint left = BuildNode(nodes, scene, start, leftCount, index, leafCapacity);
        uint right = BuildNode(nodes, scene, start + leftCount, count - leftCount, index, leafCapacity);
        nodes[(int)index] = new(combined.Min, combined.Max, left, right, (uint)start, (uint)count, parent, 0u);
        return index;
    }

    private static void PackNode(CpuNode node, Span<uint> destination)
    {
        destination[0] = FloatBits(node.Min.X);
        destination[1] = FloatBits(node.Min.Y);
        destination[2] = FloatBits(node.Min.Z);
        destination[3] = node.Left;
        destination[4] = FloatBits(node.Max.X);
        destination[5] = FloatBits(node.Max.Y);
        destination[6] = FloatBits(node.Max.Z);
        destination[7] = node.Right;
        destination[8] = node.Start;
        destination[9] = node.Count;
        destination[10] = node.Parent;
        destination[11] = node.Flags;
    }

    private static uint[] CreateViewDescriptors(int commandCount, int viewCount)
    {
        const int descriptorUInts = 20;
        uint[] result = new uint[Math.Max(descriptorUInts, viewCount * descriptorUInts)];
        for (int view = 0; view < viewCount; ++view)
        {
            int offset = view * descriptorUInts;
            result[offset] = (uint)view;
            result[offset + 1] = InvalidIndex;
            result[offset + 3] = uint.MaxValue;
            result[offset + 4] = uint.MaxValue;
            result[offset + 10] = (uint)(view * Math.Max(1, commandCount));
            result[offset + 11] = (uint)Math.Max(1, commandCount);
        }
        return result;
    }

    private static uint[] CreateViewMasks(int commandCount, int viewCount)
    {
        uint[] result = new uint[Math.Max(2, commandCount * 2)];
        for (uint command = 0; command < commandCount; ++command)
        for (int view = 0; view < viewCount; ++view)
            if (ViewIncludes(command, view))
                result[command * 2] |= 1u << view;
        return result;
    }

    private static bool ViewIncludes(uint command, int view)
        => view switch
        {
            0 => true,
            1 => (command & 1u) == 0u,
            _ => command % 3u == 0u,
        };

    private static uint[] CpuVisible(SceneBounds[] scene)
        => scene.Select((bounds, index) => (bounds, index))
            .Where(item => IsVisible(item.bounds))
            .Select(item => (uint)item.index)
            .ToArray();

    private static bool IsVisible(SceneBounds bounds)
    {
        if (bounds.IsValid)
        {
            foreach (Vector4 plane in BoxFrustum)
            {
                Vector3 positive = new(
                    plane.X >= 0 ? bounds.Max.X : bounds.Min.X,
                    plane.Y >= 0 ? bounds.Max.Y : bounds.Min.Y,
                    plane.Z >= 0 ? bounds.Max.Z : bounds.Min.Z);
                if (Vector3.Dot(new(plane.X, plane.Y, plane.Z), positive) + plane.W < 0)
                    return false;
            }
            return true;
        }

        foreach (Vector4 plane in BoxFrustum)
            if (Vector3.Dot(new(plane.X, plane.Y, plane.Z), bounds.SphereCenter) + plane.W < -bounds.SphereRadius)
                return false;
        return true;
    }

    private static SceneBounds EffectiveBounds(SceneBounds bounds)
        => bounds.IsValid
            ? bounds
            : new(bounds.SphereCenter - new Vector3(bounds.SphereRadius), bounds.SphereCenter + new Vector3(bounds.SphereRadius));

    private static SceneBounds Union(SceneBounds a, SceneBounds b)
        => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));

    private static SceneBounds[] CreateDuplicateCenters(int count)
        => Enumerable.Range(0, count).Select(i => SceneBounds.FromCenter(Vector3.Zero, 0.1f + i % 5)).ToArray();

    private static SceneBounds[] CreateDegenerateBounds(int count)
        => Enumerable.Range(0, count).Select(i => new SceneBounds(new((i % 17) - 8, (i % 7) - 3, 0), new((i % 17) - 8, (i % 7) - 3, 0))).ToArray();

    private static SceneBounds[] CreateGiantAndSmallBounds(int count)
    {
        SceneBounds[] result = CreateRandomBounds(count, 0x611A);
        result[0] = SceneBounds.FromCenter(Vector3.Zero, 1000f);
        return result;
    }

    private static SceneBounds[] CreateInvalidBounds(int count)
    {
        SceneBounds[] result = CreateRandomBounds(count, 0xBAD);
        for (int i = 0; i < result.Length; i += 5)
        {
            Vector3 center = result[i].SphereCenter;
            result[i] = new(center + Vector3.One, center - Vector3.One, center, 0.5f);
        }
        return result;
    }

    private static SceneBounds[] CreateClusteredBounds(int count, int seed)
    {
        Random random = new(seed);
        Vector3[] centers = [new(-8, -8, 0), new(8, 8, 0), Vector3.Zero];
        return Enumerable.Range(0, count).Select(i =>
        {
            Vector3 center = centers[i % centers.Length] + RandomVector(random, -1.0f, 1.0f);
            return SceneBounds.FromCenter(center, 0.05f + (float)random.NextDouble());
        }).ToArray();
    }

    private static SceneBounds[] CreateRandomBounds(int count, int seed)
    {
        Random random = new(seed);
        return Enumerable.Range(0, count).Select(_ =>
        {
            Vector3 center = RandomVector(random, -30f, 30f);
            Vector3 extent = RandomVector(random, 0.01f, 2.0f);
            return new SceneBounds(center - extent, center + extent);
        }).ToArray();
    }

    private static SceneBounds[] CreateQueuePressureBounds(int count)
        => Enumerable.Range(0, count).Select(i =>
        {
            float x = i % 2 == 0 ? (i % 19) - 9 : 50 + i % 13;
            float y = (i % 17) - 8;
            float z = (i % 11) - 5;
            return SceneBounds.FromCenter(new(x, y, z), 0.1f);
        }).ToArray();

    private static SceneBounds[] Translate(SceneBounds[] source, Vector3 offset)
        => source.Select(bounds => new SceneBounds(bounds.Min + offset, bounds.Max + offset)).ToArray();

    private static Vector3 RandomVector(Random random, float min, float max)
    {
        float scale = max - min;
        return new(
            min + (float)random.NextDouble() * scale,
            min + (float)random.NextDouble() * scale,
            min + (float)random.NextDouble() * scale);
    }

    private static string ResolveIncludes(string source)
    {
        Regex regex = new("#include\\s+\"([^\"]+)\"", RegexOptions.Multiline);
        return regex.Replace(source, match =>
        {
            string include = match.Groups[1].Value;
            string[] candidates =
            [
                Path.Combine(ShaderBasePath, "Scene3D", "RenderPipeline", include),
                Path.Combine(ShaderBasePath, include),
            ];
            string path = candidates.First(File.Exists);
            return ResolveIncludes(File.ReadAllText(path));
        });
    }

    private static uint FloatBits(float value) => BitConverter.SingleToUInt32Bits(value);

    private readonly record struct SceneBounds(Vector3 Min, Vector3 Max, Vector3 SphereCenter, float SphereRadius)
    {
        public SceneBounds(Vector3 min, Vector3 max)
            : this(min, max, (min + max) * 0.5f, Vector3.Distance(min, max) * 0.5f) { }

        public bool IsValid => Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z;

        public static SceneBounds FromCenter(Vector3 center, float radius)
            => new(center - new Vector3(radius), center + new Vector3(radius), center, radius);
    }

    private readonly record struct CpuNode(
        Vector3 Min,
        Vector3 Max,
        uint Left,
        uint Right,
        uint Start,
        uint Count,
        uint Parent,
        uint Flags);

    private readonly record struct TraversalResult(
        uint[] VisibleIds,
        uint[][] PerViewVisibleIds,
        uint OverflowFlags,
        uint QueueOverflowCount);
}
