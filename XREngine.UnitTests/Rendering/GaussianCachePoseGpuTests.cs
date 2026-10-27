using System.Numerics;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Gaussian;
using XREngine.Rendering.Models.Gaussian.Baking.Build;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Runs the two gaussian cache compute shaders (driver pose, then cut and splat pose) on a real OpenGL context, with the
/// bindings and uniforms of <see cref="GaussianCacheComponent"/>, and compares the result with
/// <see cref="GaussianCachePoseReference"/>.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class GaussianCachePoseGpuTests : GpuTestBase
{
    private const float FrameRate = GaussianCacheTestSphere.FrameRate;
    private const float PositionTolerance = 1e-4f;
    private const float LodThresholdPixels = 2.0f;

    /// <summary>A small chunk limit, so that the leaf level arrives in more than one chunk.</summary>
    private const int SplitChunkBytes = 16_384;

    private static readonly Vector2 Viewport = new(1920.0f, 1080.0f);
    private static readonly Matrix4x4 Projection = Matrix4x4.CreatePerspectiveFieldOfView(40.0f * MathF.PI / 180.0f, Viewport.X / Viewport.Y, 0.05f, 2_000.0f);

    [Test]
    public void GpuLevel_IsStd430_32Bytes()
        => Unsafe.SizeOf<GaussianCacheGpuLevel>().ShouldBe(32);

    [Test, Category("Hardware")]
    public void GpuPose_MatchesCpuReference()
    {
        GaussianCacheTestSphere sphere = GaussianCacheTestSphere.Shared;
        GaussianCacheBuildResult complete = sphere.Build();
        GaussianCacheBuildResult split = sphere.Build(GaussianCacheLoopMode.Loop, SplitChunkBytes);
        int firstLeafChunk = Array.FindIndex(split.Root.Header.Chunks, info => info.Kind == GaussianCacheChunkKind.SplatLevel && info.SplatLevel == 2);

        // Collapse every driver vertex of frame 8, so that every bound splat uses the rest-frame rule.
        GaussianCache collapsed = GaussianCacheTestSphere.Load(complete);
        DriverFrameStore frames = collapsed.Runtime.Frames;
        int frameStart = 8 * frames.VertexCount * 3;
        for (int vertex = 1; vertex < frames.VertexCount; vertex++)
            frames.Positions.AsSpan(frameStart, 3).CopyTo(frames.Positions.AsSpan(frameStart + vertex * 3, 3));

        // Use the nearest camera distance at which the cut of the complete cache mixes all three levels.
        GaussianCache mixed = GaussianCacheTestSphere.Load(complete);
        float mixedDistance = MixedCutDistance(mixed, 37.3f / FrameRate);

        (string Name, GaussianCache Cache, float Frame, float Distance)[] scenarios =
        [
            ("complete, between frames", mixed, 37.3f, mixedDistance),
            ("first leaf chunk only, leaf motion at T0", GaussianCacheTestSphere.Load(split, firstLeafChunk + 1), 21.7f, 1.0f),
            ("collapsed frame 8", collapsed, 8.0f, mixedDistance),
        ];

        RunWithGLContext(gl =>
        {
            RequireHardwareCompute(gl);
            foreach ((string name, GaussianCache cache, float frame, float distance) in scenarios)
            {
                float time = frame / FrameRate;
                Matrix4x4 view = Camera(distance);
                List<GaussianSplatPose> expected = [];
                GaussianCachePoseReference.Compute(cache, time, view, Projection, Viewport, LodThresholdPixels, expected);
                TestContext.Out.WriteLine($"{name}: CPU {Histogram(expected.Select(pose => pose.Level))}");
                expected.Count.ShouldBeGreaterThan(0, name);

                GpuResult result = RunPasses(gl, cache, time, view, totalCapacity: true);
                result.Overflow.ShouldBe(0u, name);
                result.Visible.ShouldBe((uint)expected.Count, name);
                CompareWithReference(cache, expected, result.Splats, name);
            }
        }, timeoutMs: 120_000);
    }

    /// <summary>
    /// Returns the camera distance of a series (5 m to 640 m, factor 1.5) at which the reference cut chooses nodes of the
    /// most levels. The first distance with the most levels wins. At least two levels are necessary.
    /// </summary>
    private static float MixedCutDistance(GaussianCache cache, float time)
    {
        List<GaussianSplatPose> poses = [];
        float best = 0.0f;
        int bestLevels = 1;
        for (float distance = 5.0f; distance <= 640.0f; distance *= 1.5f)
        {
            GaussianCachePoseReference.Compute(cache, time, Camera(distance), Projection, Viewport, LodThresholdPixels, poses);
            int levels = poses.Select(pose => pose.Level).Distinct().Count();
            if (levels > bestLevels)
            {
                best = distance;
                bestLevels = levels;
            }
        }

        bestLevels.ShouldBeGreaterThan(1, "No camera distance gives a cut with nodes of more than one level.");
        TestContext.Out.WriteLine($"Mixed cut at {best:F1} m with nodes of {bestLevels} levels.");
        return best;
    }

    [Test, Category("Hardware")]
    public void GpuPose_OverCapacity_CountsOverflow()
    {
        GaussianCache cache = GaussianCacheTestSphere.Load(GaussianCacheTestSphere.Shared.Build());
        float time = 12.5f / FrameRate;
        Matrix4x4 view = Camera(3.0f);
        List<GaussianSplatPose> expected = [];
        GaussianCachePoseReference.Compute(cache, time, view, Projection, Viewport, LodThresholdPixels, expected);
        int capacity = expected.Count - 100;

        RunWithGLContext(gl =>
        {
            RequireHardwareCompute(gl);
            GpuResult result = RunPasses(gl, cache, time, view, totalCapacity: false, capacity);
            result.Visible.ShouldBe((uint)expected.Count);
            result.Overflow.ShouldBe(100u);
            result.Splats.Length.ShouldBe(capacity);
        }, timeoutMs: 120_000);
    }

    [Test, Category("Hardware")]
    public void GpuPose_ClearedLevelTable_ChoosesNothing()
    {
        // When the component switches caches, it writes a level table with no ready level before the first table of the
        // new cache arrives. The passes must then choose nothing, even over fully loaded records and frames.
        GaussianCache cache = GaussianCacheTestSphere.Load(GaussianCacheTestSphere.Shared.Build());
        float time = 12.5f / FrameRate;
        Matrix4x4 view = Camera(3.0f);

        RunWithGLContext(gl =>
        {
            RequireHardwareCompute(gl);
            GpuResult result = RunPasses(gl, cache, time, view, totalCapacity: true, clearLevelTable: true);
            result.Visible.ShouldBe(0u);
            result.Overflow.ShouldBe(0u);
            result.Splats.ShouldBeEmpty();
        }, timeoutMs: 120_000);
    }

    private readonly record struct GpuResult(uint Visible, uint Overflow, GaussianSplatGpuSplat[] Splats);

    /// <summary>
    /// Uploads the decoded data of a cache as the component does, and runs both compute passes. With
    /// <paramref name="clearLevelTable"/>, the level table has no ready level, as the component writes it at a cache switch.
    /// </summary>
    private static GpuResult RunPasses(GL gl, GaussianCache cache, float time, Matrix4x4 view, bool totalCapacity, int capacity = 0, bool clearLevelTable = false)
    {
        GaussianCacheHeader header = cache.Header;
        GaussianCacheRuntimeData runtime = cache.Runtime;
        DriverFrameStore frames = runtime.Frames;
        GaussianCacheGpuLevel[] levels = new GaussianCacheGpuLevel[runtime.LevelRecords.Length];
        if (!clearLevelTable)
            GaussianCacheGpuLevel.Fill(cache, levels);
        GaussianCacheSplatRecord[] records = [.. runtime.LevelRecords.SelectMany(level => level)];
        int[] parents = [.. runtime.ParentIndex.SelectMany(level => level)];
        uint[] indices = [.. runtime.DriverLevelIndices.SelectMany(level => level)];
        ushort[] positions = new ushort[(frames.Positions.Length + 1) & ~1];
        frames.Positions.CopyTo(positions, 0);
        if (totalCapacity)
            capacity = records.Length;

        GridQuantizer grid = new(header.Bounds, header.GridBits);
        uint driverPose = CompileComputeProgram(gl, GaussianCacheComponent.DriverPoseShaderPath);
        uint splats = CompileComputeProgram(gl, GaussianCacheComponent.SplatsShaderPath);
        uint recordBuffer = CreateStorage<GaussianCacheSplatRecord>(gl, records);
        uint parentBuffer = CreateStorage<int>(gl, parents);
        uint indexBuffer = CreateStorage<uint>(gl, indices.Length > 0 ? indices : [0u]);
        uint frameBuffer = CreateStorage<ushort>(gl, positions.Length > 0 ? positions : [0, 0]);
        uint poseBuffer = CreateStorage(gl, (nuint)(Math.Max(frames.VertexCount, 1) * 16));
        uint levelBuffer = CreateStorage<GaussianCacheGpuLevel>(gl, levels);
        uint splatBuffer = CreateStorage(gl, (nuint)(capacity * Unsafe.SizeOf<GaussianSplatGpuSplat>()));
        uint visibleBuffer = CreateStorage<uint>(gl, [0u, 0u, 0u, 0u]);
        uint overflowBuffer = CreateStorage<uint>(gl, [0u, 0u, 0u, 0u]);
        try
        {
            float frame = GaussianCachePoseReference.FractionalFrame(header, time);
            if (frames.VertexCount > 0)
            {
                gl.UseProgram(driverPose);
                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianCacheComponent.FramesBinding, frameBuffer);
                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianCacheComponent.DriverPoseBinding, poseBuffer);
                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianCacheComponent.LevelsBinding, levelBuffer);
                gl.Uniform1(gl.GetUniformLocation(driverPose, GaussianCacheComponent.DriverVertexCountUniform), (uint)frames.VertexCount);
                gl.Uniform1(gl.GetUniformLocation(driverPose, GaussianCacheComponent.DriverLevelCountUniform), (uint)frames.DriverLevelCount);
                gl.Uniform1(gl.GetUniformLocation(driverPose, GaussianCacheComponent.FrameCountUniform), frames.FrameCount);
                gl.Uniform1(gl.GetUniformLocation(driverPose, GaussianCacheComponent.FractionalFrameUniform), frame);
                gl.Uniform1(gl.GetUniformLocation(driverPose, GaussianCacheComponent.LoopModeUniform), (int)header.LoopMode);
                SetVector(gl, driverPose, GaussianCacheComponent.GridOriginUniform, grid.Origin);
                SetVector(gl, driverPose, GaussianCacheComponent.GridStepUniform, grid.CellSize);
                gl.DispatchCompute(GaussianCacheComponent.GetGroupCount((uint)frames.VertexCount), 1u, 1u);
                gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
            }

            gl.UseProgram(splats);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianSplatRenderer.SplatsBinding, splatBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianSplatRenderer.VisibleCountBinding, visibleBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianSplatRenderer.OverflowBinding, overflowBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianCacheComponent.RecordsBinding, recordBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianCacheComponent.ParentsBinding, parentBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianCacheComponent.DriverIndicesBinding, indexBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianCacheComponent.FramesBinding, frameBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianCacheComponent.DriverPoseBinding, poseBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GaussianCacheComponent.LevelsBinding, levelBuffer);
            gl.Uniform1(gl.GetUniformLocation(splats, GaussianCacheComponent.TotalNodeCountUniform), (uint)records.Length);
            gl.Uniform1(gl.GetUniformLocation(splats, GaussianCacheComponent.LevelCountUniform), (uint)levels.Length);
            gl.Uniform1(gl.GetUniformLocation(splats, GaussianCacheComponent.SplatCapacityUniform), (uint)capacity);
            SetMatrix(gl, splats, GaussianCacheComponent.ModelViewUniform, view);
            gl.Uniform1(gl.GetUniformLocation(splats, GaussianCacheComponent.FocalPixelsUniform), GaussianCachePoseReference.FocalPixels(Projection, Viewport.Y));
            gl.Uniform1(gl.GetUniformLocation(splats, GaussianCacheComponent.SigmaScaleUniform), GaussianCachePoseReference.SigmaScale(view));
            gl.Uniform1(gl.GetUniformLocation(splats, GaussianCacheComponent.PerspectiveUniform), GaussianCachePoseReference.IsPerspective(Projection) ? 1 : 0);
            gl.Uniform1(gl.GetUniformLocation(splats, GaussianCacheComponent.LodThresholdUniform), LodThresholdPixels);
            gl.Uniform1(gl.GetUniformLocation(splats, GaussianCacheComponent.ColorScaleUniform), header.ColorScale);
            SetVector(gl, splats, GaussianCacheComponent.GridOriginUniform, grid.Origin);
            SetVector(gl, splats, GaussianCacheComponent.GridStepUniform, grid.CellSize);
            gl.DispatchCompute(GaussianCacheComponent.GetGroupCount((uint)records.Length), 1u, 1u);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.BufferUpdateBarrierBit);
            gl.Finish();
            gl.GetError().ShouldBe(GLEnum.NoError);

            uint visible = ReadStorage<uint>(gl, visibleBuffer, 1)[0];
            uint overflow = ReadStorage<uint>(gl, overflowBuffer, 1)[0];
            int written = (int)Math.Min(visible, (uint)capacity);
            return new GpuResult(visible, overflow, ReadStorage<GaussianSplatGpuSplat>(gl, splatBuffer, written));
        }
        finally
        {
            foreach (uint buffer in new[] { recordBuffer, parentBuffer, indexBuffer, frameBuffer, poseBuffer, levelBuffer, splatBuffer, visibleBuffer, overflowBuffer })
                gl.DeleteBuffer(buffer);
            gl.DeleteProgram(driverPose);
            gl.DeleteProgram(splats);
        }
    }

    /// <summary>Checks that the GPU chose the same nodes as the reference, with the same poses.</summary>
    private static void CompareWithReference(GaussianCache cache, List<GaussianSplatPose> expected, GaussianSplatGpuSplat[] actual, string scenario)
    {
        int[] firstRecord = new int[cache.Runtime.LevelRecords.Length];
        for (int level = 1; level < firstRecord.Length; level++)
            firstRecord[level] = firstRecord[level - 1] + cache.Runtime.LevelRecords[level - 1].Length;

        Dictionary<int, GaussianSplatGpuSplat> byNode = [];
        foreach (GaussianSplatGpuSplat splat in actual)
            byNode.TryAdd((int)BitConverter.SingleToUInt32Bits(splat.ScalePad.W), splat).ShouldBeTrue($"{scenario}: a node is written two times");

        float worstPosition = 0.0f;
        float worstDot = 1.0f;
        float worstColor = 0.0f;
        foreach (GaussianSplatPose pose in expected)
        {
            byNode.TryGetValue(firstRecord[pose.Level] + pose.Node, out GaussianSplatGpuSplat splat)
                .ShouldBeTrue($"{scenario}: the GPU did not choose level {pose.Level} node {pose.Node}");
            Vector3 position = new(splat.CenterOpacity.X, splat.CenterOpacity.Y, splat.CenterOpacity.Z);
            Quaternion rotation = new(splat.Rotation.X, splat.Rotation.Y, splat.Rotation.Z, splat.Rotation.W);
            worstPosition = MathF.Max(worstPosition, Vector3.Distance(position, pose.Position));
            worstDot = MathF.Min(worstDot, MathF.Abs(Quaternion.Dot(rotation, pose.Rotation)));
            new Vector3(splat.ScalePad.X, splat.ScalePad.Y, splat.ScalePad.Z).ShouldBe(pose.Scale);
            Vector4 color = new(splat.LinearColor.X, splat.LinearColor.Y, splat.LinearColor.Z, splat.CenterOpacity.W);
            worstColor = MathF.Max(worstColor, Vector4.Distance(color, pose.LinearColor));
        }

        TestContext.Out.WriteLine($"{scenario}: GPU {actual.Length} splats, worst position {worstPosition:E3} m, rotation dot {worstDot:F7}, color {worstColor:E3}");
        worstPosition.ShouldBeLessThanOrEqualTo(PositionTolerance, scenario);
        worstDot.ShouldBeGreaterThanOrEqualTo(1.0f - 1e-4f, scenario);
        worstColor.ShouldBeLessThanOrEqualTo(1e-4f, scenario);
    }

    /// <summary>Looks at the sphere center from a distance, from the side and a little above.</summary>
    private static Matrix4x4 Camera(float distance)
        => Matrix4x4.CreateLookAt(Vector3.Normalize(new Vector3(1.0f, 0.4f, 0.8f)) * distance, Vector3.Zero, Vector3.UnitY);

    private static string Histogram(IEnumerable<int> levels)
        => string.Join(", ", levels.GroupBy(level => level).OrderBy(group => group.Key).Select(group => $"L{group.Key}={group.Count()}"));

    private static void RequireHardwareCompute(GL gl)
    {
        AssertHardwareComputeOrInconclusive(gl);
        string vendor = gl.GetStringS(StringName.Vendor) ?? string.Empty;
        string renderer = gl.GetStringS(StringName.Renderer) ?? string.Empty;
        if (vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"The gaussian cache pose tests need a hardware OpenGL driver. Vendor='{vendor}', Renderer='{renderer}'.");
        }

        TestContext.Out.WriteLine($"OpenGL: {vendor} {renderer}");
    }

    private static uint CompileComputeProgram(GL gl, string relativePath)
    {
        uint shader = CompileComputeShader(gl, LoadShaderSource(relativePath));
        try
        {
            return CreateComputeProgram(gl, shader);
        }
        finally
        {
            gl.DeleteShader(shader);
        }
    }

    private static unsafe void SetMatrix(GL gl, uint program, string name, Matrix4x4 value)
        => gl.UniformMatrix4(gl.GetUniformLocation(program, name), 1u, false, (float*)&value);

    private static void SetVector(GL gl, uint program, string name, Vector3 value)
        => gl.Uniform3(gl.GetUniformLocation(program, name), value.X, value.Y, value.Z);

    private static unsafe uint CreateStorage<T>(GL gl, ReadOnlySpan<T> data) where T : unmanaged
    {
        uint buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        fixed (T* pointer = data)
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof(T)), pointer, BufferUsageARB.DynamicCopy);
        return buffer;
    }

    private static unsafe uint CreateStorage(GL gl, nuint byteCount)
    {
        uint buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        gl.BufferData(BufferTargetARB.ShaderStorageBuffer, Math.Max(byteCount, 16u), null, BufferUsageARB.DynamicCopy);
        return buffer;
    }

    private static unsafe T[] ReadStorage<T>(GL gl, uint buffer, int count) where T : unmanaged
    {
        T[] result = new T[count];
        if (count == 0)
            return result;
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        fixed (T* pointer = result)
            gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(count * sizeof(T)), pointer);
        return result;
    }
}
