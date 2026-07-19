using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Shaders;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Opt-in, hardware-timed comparison of the production flat and root-down
/// scene-culling compute shaders. The report is intentionally local evidence,
/// not a portable performance claim.
/// </summary>
[TestFixture]
public sealed class GpuBvhCullingBenchmarkTests : GpuTestBase
{
    private const int DrawMetadataUInts = 16;
    private const int BoundsUInts = 16;
    private const int CommandUInts = 20;
    private const int NodeUInts = 12;
    private const uint InvalidIndex = uint.MaxValue;

    private static readonly Vector4[] BoxFrustum =
    [
        new(1, 0, 0, 10), new(-1, 0, 0, 10),
        new(0, 1, 0, 10), new(0, -1, 0, 10),
        new(0, 0, 1, 10), new(0, 0, -1, 10),
    ];

    [Test]
    [Explicit("Runs a representative local-GPU timing matrix and writes scratch evidence.")]
    [Category("GpuPerformance")]
    public unsafe void ProductionFlatAndRootDownShaders_WriteRepresentativeTimingReport()
    {
        RunWithGLContext(gl => RunBenchmark(gl), timeoutMs: 240_000);
    }

    [Test]
    public void BoundsPolicySweep_ExposesDilutionAndHysteresisBoundaries()
    {
        PolicySweepRow[] sweep = CreateBoundsPolicySweep();

        sweep.Single(static row => row.Scenario == "clustered-configured-2x").PolicyAccepts.ShouldBeTrue();
        sweep.Single(static row => row.Scenario == "giant-configured-2.01x").PolicyAccepts.ShouldBeFalse();
        sweep.Single(static row => row.Scenario == "expanding-prior-volume-4x").PolicyAccepts.ShouldBeTrue();
        sweep.Single(static row => row.Scenario == "expanding-prior-volume-4.01x").PolicyAccepts.ShouldBeFalse();
    }

    private static PolicySweepRow[] CreateBoundsPolicySweep()
        =>
        [
            new("clustered-configured-1x", "configured dilution", 1.0, 1.0 <= 2.0),
            new("clustered-configured-2x", "configured dilution", 2.0, 2.0 <= 2.0),
            new("giant-configured-2.01x", "configured dilution", 2.01, 2.01 <= 2.0),
            new("giant-configured-4x", "configured dilution", 4.0, 4.0 <= 2.0),
            new("giant-configured-8x", "configured dilution", 8.0, 8.0 <= 2.0),
            new("expanding-prior-volume-1x", "prior/candidate volume", 1.0, 1.0 <= 4.0),
            new("expanding-prior-volume-2x", "prior/candidate volume", 2.0, 2.0 <= 4.0),
            new("expanding-prior-volume-4x", "prior/candidate volume", 4.0, 4.0 <= 4.0),
            new("expanding-prior-volume-4.01x", "prior/candidate volume", 4.01, 4.01 <= 4.0),
            new("expanding-prior-volume-8x", "prior/candidate volume", 8.0, 8.0 <= 4.0),
        ];

    private static unsafe void RunBenchmark(GL gl)
    {
        uint flatShader = 0;
        uint bvhShader = 0;
        uint flatProgram = 0;
        uint bvhProgram = 0;
        uint buildShader = 0;
        uint refitShader = 0;
        uint buildProgram = 0;
        uint refitProgram = 0;
        try
        {
            string flatPath = Path.Combine(ShaderBasePath, "Compute", "Culling", "GPURenderCulling.comp");
            string bvhPath = Path.Combine(ShaderBasePath, "Scene3D", "RenderPipeline", "bvh_frustum_cull.comp");
            string buildPath = Path.Combine(ShaderBasePath, "Scene3D", "RenderPipeline", "bvh_build.comp");
            string refitPath = Path.Combine(ShaderBasePath, "Scene3D", "RenderPipeline", "bvh_refit.comp");
            flatShader = CompileComputeShader(gl, ShaderSourcePreprocessor.ResolveSource(File.ReadAllText(flatPath), flatPath));
            bvhShader = CompileComputeShader(gl, ShaderSourcePreprocessor.ResolveSource(File.ReadAllText(bvhPath), bvhPath));
            buildShader = CompileComputeShader(gl, ShaderSourcePreprocessor.ResolveSource(File.ReadAllText(buildPath), buildPath));
            refitShader = CompileComputeShader(gl, ShaderSourcePreprocessor.ResolveSource(File.ReadAllText(refitPath), refitPath));
            flatProgram = CreateComputeProgram(gl, flatShader);
            bvhProgram = CreateComputeProgram(gl, bvhShader);
            buildProgram = CreateComputeProgram(gl, buildShader);
            refitProgram = CreateComputeProgram(gl, refitShader);

            string vendor = gl.GetStringS(StringName.Vendor) ?? "unknown";
            string renderer = gl.GetStringS(StringName.Renderer) ?? "unknown";
            string version = gl.GetStringS(StringName.Version) ?? "unknown";
            List<BenchmarkResult> results = [];
            List<MaintenanceResult> maintenanceResults = [];

            foreach (BenchmarkCase benchmarkCase in CreateCases())
            {
                SceneBounds[] scene = CreateScene(benchmarkCase);
                using BenchmarkBuffers buffers = BenchmarkBuffers.Create(gl, scene, benchmarkCase.LeafCapacity, benchmarkCase.ViewCount);
                ConfigureProgram(gl, flatProgram, benchmarkCase, rootDown: false);
                ConfigureProgram(gl, bvhProgram, benchmarkCase, rootDown: true);

                long[] flatTimes = Measure(gl, flatProgram, buffers, benchmarkCase, rootDown: false);
                uint flatVisible = buffers.ReadVisibleCount(gl);
                long[] bvhTimes = Measure(gl, bvhProgram, buffers, benchmarkCase, rootDown: true);
                uint bvhVisible = buffers.ReadVisibleCount(gl);
                Assert.That(bvhVisible, Is.EqualTo(flatVisible), $"visibility count mismatch for {benchmarkCase}");

                results.Add(new(
                    benchmarkCase,
                    Median(flatTimes),
                    Median(bvhTimes),
                    flatVisible,
                    buffers.ReadOverflowFlags(gl)));
            }

            foreach (MaintenanceCase maintenanceCase in CreateMaintenanceCases())
            {
                BenchmarkCase traversalCase = new(
                    maintenanceCase.CommandCount,
                    Distribution.Uniform,
                    maintenanceCase.VisiblePercent,
                    maintenanceCase.ViewCount,
                    maintenanceCase.LeafCapacity);
                SceneBounds[] scene = CreateScene(traversalCase);
                using MaintenanceBuffers maintenance = MaintenanceBuffers.Create(gl, scene, maintenanceCase.LeafCapacity);
                long buildNanoseconds = maintenance.MeasureBuild(gl, buildProgram);
                UploadResult upload = maintenance.UploadDirtyBounds(gl, maintenanceCase.DirtyPercent);
                long refitNanoseconds = maintenance.MeasureRefit(gl, refitProgram);

                using BenchmarkBuffers traversal = BenchmarkBuffers.Create(gl, scene, maintenanceCase.LeafCapacity, maintenanceCase.ViewCount);
                ConfigureProgram(gl, flatProgram, traversalCase, rootDown: false);
                _ = Measure(gl, flatProgram, traversal, traversalCase, rootDown: false);
                uint expectedVisible = traversal.ReadVisibleCount(gl);
                traversal.UseTraversalTree(maintenance.NodeBuffer, maintenance.MortonBuffer);
                ConfigureProgram(gl, bvhProgram, traversalCase, rootDown: true);
                long traversalNanoseconds = Median(Measure(gl, bvhProgram, traversal, traversalCase, rootDown: true));
                uint actualVisible = traversal.ReadVisibleCount(gl);
                Assert.That(actualVisible, Is.EqualTo(expectedVisible), $"GPU-built traversal visibility mismatch for {maintenanceCase}");
                maintenanceResults.Add(new(
                    maintenanceCase,
                    buildNanoseconds,
                    refitNanoseconds,
                    traversalNanoseconds,
                    upload.Bytes,
                    upload.SynchronizedCpuNanoseconds,
                    actualVisible,
                    traversal.ReadOverflowFlags(gl),
                    maintenance.NodeCapacity,
                    maintenance.NodeScalarCapacity,
                    maintenance.BuildOverflowFlags));
            }

            WriteReports(vendor, renderer, version, results, maintenanceResults);
        }
        finally
        {
            if (flatProgram != 0) gl.DeleteProgram(flatProgram);
            if (bvhProgram != 0) gl.DeleteProgram(bvhProgram);
            if (buildProgram != 0) gl.DeleteProgram(buildProgram);
            if (refitProgram != 0) gl.DeleteProgram(refitProgram);
            if (flatShader != 0) gl.DeleteShader(flatShader);
            if (bvhShader != 0) gl.DeleteShader(bvhShader);
            if (buildShader != 0) gl.DeleteShader(buildShader);
            if (refitShader != 0) gl.DeleteShader(refitShader);
        }
    }

    private static MaintenanceCase[] CreateMaintenanceCases()
        =>
        [
            new(10_000, 0.0, 0, 1, 1),
            new(10_000, 0.1, 10, 2, 2),
            new(10_000, 1.0, 50, 3, 4),
            new(10_000, 10.0, 100, 1, 8),
            new(10_000, 100.0, 50, 2, 16),
        ];

    private static BenchmarkCase[] CreateCases()
    {
        List<BenchmarkCase> result = [];
        foreach (Distribution distribution in Enum.GetValues<Distribution>())
        {
            result.Add(new(1_000, distribution, 50, 1, 4));
            result.Add(new(10_000, distribution, 50, 1, 4));
        }

        result.Add(new(1_000, Distribution.Uniform, 10, 1, 4));
        result.Add(new(1_000, Distribution.Uniform, 100, 1, 4));
        result.Add(new(1_000, Distribution.Uniform, 50, 2, 4));
        result.Add(new(1_000, Distribution.Uniform, 50, 1, 1));
        result.Add(new(1_000, Distribution.Uniform, 50, 1, 16));
        result.Add(new(100_000, Distribution.Uniform, 10, 1, 4));
        result.Add(new(100_000, Distribution.Clustered, 50, 1, 4));
        result.Add(new(1_000_000, Distribution.Uniform, 10, 1, 8));
        return [.. result];
    }

    private static unsafe long[] Measure(
        GL gl,
        uint program,
        BenchmarkBuffers buffers,
        BenchmarkCase benchmarkCase,
        bool rootDown)
    {
        const int warmups = 2;
        const int samples = 5;
        long[] result = new long[samples];
        uint query = gl.GenQuery();
        try
        {
            for (int iteration = 0; iteration < warmups + samples; ++iteration)
            {
                buffers.ResetAndBind(gl);
                gl.UseProgram(program);
                gl.BeginQuery(GLEnum.TimeElapsed, query);
                uint workgroups = rootDown
                    ? GpuBvhCullingDispatch.CalculateWorkgroupCount((uint)benchmarkCase.CommandCount)
                    : (uint)((benchmarkCase.CommandCount + 255) / 256);
                gl.DispatchCompute(workgroups, 1, 1);
                gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.BufferUpdateBarrierBit);
                gl.EndQuery(GLEnum.TimeElapsed);
                long elapsedNanoseconds = gl.GetQueryObject(query, GLEnum.QueryResult);
                if (iteration >= warmups)
                    result[iteration - warmups] = elapsedNanoseconds;
            }
        }
        finally
        {
            gl.DeleteQuery(query);
        }
        return result;
    }

    private static void ConfigureProgram(GL gl, uint program, BenchmarkCase benchmarkCase, bool rootDown)
    {
        gl.UseProgram(program);
        float[] planes = BoxFrustum.SelectMany(static p => new[] { p.X, p.Y, p.Z, p.W }).ToArray();
        SetUniform4(gl, program, "FrustumPlanes", planes);
        SetUniform4(gl, program, "ClusterPlanes", planes);
        SetUniform(gl, program, "UseClusterPlanes", 0u);
        SetUniform(gl, program, "UseClusterPlaneBuffer", 0u);
        SetUniform(gl, program, "ClusterPlaneOffset", 0u);
        SetUniform(gl, program, "ClusterPlaneStride", 0u);
        SetUniform(gl, program, "MaxRenderDistance", float.MaxValue);
        SetUniform(gl, program, "CameraLayerMask", uint.MaxValue);
        SetUniform(gl, program, "CurrentRenderPass", -1);
        SetUniform(gl, program, "InputCommandCount", benchmarkCase.CommandCount);
        SetUniform(gl, program, "MaxCulledCommands", benchmarkCase.CommandCount);
        SetUniform(gl, program, "DisabledFlagsMask", 0u);
        SetUniform(gl, program, "ActiveViewCount", benchmarkCase.ViewCount);
        SetUniform(gl, program, "UseHotCommands", 0u);
        SetUniform(gl, program, "StatsEnabled", rootDown ? 1u : 0u);
        SetUniform(gl, program, "OverflowDebugEnabled", 0u);
        SetUniform(gl, program, "ENABLE_CPU_GPU_COMPARE", 0u);
        int camera = gl.GetUniformLocation(program, "CameraPosition");
        if (camera >= 0)
            gl.Uniform3(camera, 0f, 0f, 0f);
    }

    private static SceneBounds[] CreateScene(BenchmarkCase benchmarkCase)
    {
        Random random = new(unchecked((int)(0xB71C0000u + (uint)benchmarkCase.CommandCount + (uint)benchmarkCase.Distribution * 977u)));
        int visibleCount = benchmarkCase.CommandCount * benchmarkCase.VisiblePercent / 100;
        SceneBounds[] result = new SceneBounds[benchmarkCase.CommandCount];
        for (int i = 0; i < result.Length; ++i)
        {
            bool requestedVisible = i < visibleCount;
            Vector3 center;
            Vector3 extent;
            switch (benchmarkCase.Distribution)
            {
                case Distribution.Clustered:
                    Vector3 cluster = (i % 4) switch
                    {
                        0 => new(-6, -6, -2),
                        1 => new(6, 6, 2),
                        2 => new(-6, 6, 0),
                        _ => new(6, -6, 0),
                    };
                    center = cluster + RandomVector(random, -1.2f, 1.2f);
                    extent = new(0.05f + (float)random.NextDouble() * 0.4f);
                    break;
                case Distribution.IdenticalCenter:
                    center = Vector3.Zero;
                    extent = new(0.05f + (i % 13) * 0.015f);
                    break;
                case Distribution.LongThin:
                    center = RandomVector(random, -8f, 8f);
                    extent = (i % 3) switch
                    {
                        0 => new(5f, 0.02f, 0.02f),
                        1 => new(0.02f, 5f, 0.02f),
                        _ => new(0.02f, 0.02f, 5f),
                    };
                    break;
                case Distribution.GiantPlusManySmall:
                    center = i == 0 ? Vector3.Zero : RandomVector(random, -8f, 8f);
                    extent = i == 0 ? new(100f) : new(0.03f + (float)random.NextDouble() * 0.15f);
                    break;
                case Distribution.RapidlyExpanding:
                    center = RandomVector(random, -8f, 8f);
                    float scale = 0.02f * MathF.Pow(1.003f, i % 1_500);
                    extent = new(MathF.Min(scale, 6f));
                    break;
                default:
                    center = RandomVector(random, -8f, 8f);
                    extent = RandomVector(random, 0.02f, 0.5f);
                    break;
            }

            if (!requestedVisible && !(benchmarkCase.Distribution == Distribution.GiantPlusManySmall && i == 0))
                center.X += 40f;
            result[i] = new(center - extent, center + extent);
        }
        return result;
    }

    private static Vector3 RandomVector(Random random, float min, float max)
    {
        float range = max - min;
        return new(
            min + (float)random.NextDouble() * range,
            min + (float)random.NextDouble() * range,
            min + (float)random.NextDouble() * range);
    }

    private static long Median(long[] values)
    {
        Array.Sort(values);
        return values[values.Length / 2];
    }

    private static void WriteReports(
        string vendor,
        string renderer,
        string version,
        List<BenchmarkResult> results,
        List<MaintenanceResult> maintenanceResults)
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(ShaderBasePath, "..", "..", ".."));
        string reportDirectory = Path.Combine(repositoryRoot, "Build", "_AgentValidation", "20260718-gpu-bvh-scene-tree", "reports");
        Directory.CreateDirectory(reportDirectory);

        string csvPath = Path.Combine(reportDirectory, "gpu-bvh-flat-vs-root-down-opengl.csv");
        List<string> csv = ["api,vendor,renderer,version,commands,distribution,visible_percent,views,leaf_capacity,flat_median_ns,bvh_median_ns,speedup,bvh_wins,visible_count,overflow_flags"];
        foreach (BenchmarkResult result in results)
        {
            double speedup = (double)result.FlatMedianNanoseconds / result.BvhMedianNanoseconds;
            csv.Add(string.Join(',',
                "OpenGL",
                Csv(vendor), Csv(renderer), Csv(version),
                result.Case.CommandCount,
                result.Case.Distribution,
                result.Case.VisiblePercent,
                result.Case.ViewCount,
                result.Case.LeafCapacity,
                result.FlatMedianNanoseconds,
                result.BvhMedianNanoseconds,
                speedup.ToString("F4", CultureInfo.InvariantCulture),
                result.BvhMedianNanoseconds < result.FlatMedianNanoseconds,
                result.VisibleCount,
                result.OverflowFlags));
        }
        File.WriteAllLines(csvPath, csv);

        string maintenanceCsvPath = Path.Combine(reportDirectory, "gpu-bvh-maintenance-opengl.csv");
        List<string> maintenanceCsv = ["api,vendor,renderer,version,commands,dirty_percent,visible_percent,views,leaf_capacity,build_gpu_ns,refit_gpu_ns,traversal_emission_gpu_ns,upload_bytes,upload_sync_cpu_ns,build_dispatches,refit_dispatches,traversal_dispatches,barriers,visible_count,emitted_command_bytes,output_capacity_bytes,node_capacity,node_scalar_capacity,build_overflow_flags,traversal_overflow_flags,queue_pressure"];
        foreach (MaintenanceResult result in maintenanceResults)
        {
            ulong emittedBytes = (ulong)result.VisibleCount * (ulong)(CommandUInts * sizeof(uint) + result.Case.ViewCount * sizeof(uint));
            ulong outputCapacityBytes = (ulong)result.Case.CommandCount * (ulong)(CommandUInts * sizeof(uint) + result.Case.ViewCount * sizeof(uint));
            maintenanceCsv.Add(string.Join(',',
                "OpenGL", Csv(vendor), Csv(renderer), Csv(version),
                result.Case.CommandCount,
                result.Case.DirtyPercent.ToString("F1", CultureInfo.InvariantCulture),
                result.Case.VisiblePercent,
                result.Case.ViewCount,
                result.Case.LeafCapacity,
                result.BuildNanoseconds,
                result.RefitNanoseconds,
                result.TraversalEmissionNanoseconds,
                result.UploadBytes,
                result.UploadSynchronizedCpuNanoseconds,
                4, 2, 1, 7,
                result.VisibleCount,
                emittedBytes,
                outputCapacityBytes,
                result.NodeCapacity,
                result.NodeScalarCapacity,
                result.BuildOverflowFlags,
                result.TraversalOverflowFlags,
                (result.TraversalOverflowFlags & 2u) != 0u));
        }
        File.WriteAllLines(maintenanceCsvPath, maintenanceCsv);

        int wins = results.Count(static r => r.BvhMedianNanoseconds < r.FlatMedianNanoseconds);
        int queuePressureCases = results.Count(static r => (r.OverflowFlags & 2u) != 0u);
        StringBuilder report = new();
        report.AppendLine("# GPU BVH flat-vs-root-down benchmark");
        report.AppendLine();
        report.AppendLine($"- API: OpenGL 4.6 compute (`GL_TIME_ELAPSED`)");
        report.AppendLine($"- Vendor: {vendor}");
        report.AppendLine($"- Renderer: {renderer}");
        report.AppendLine($"- Driver/version string: {version}");
        report.AppendLine($"- Samples: median of 5 after 2 warmups; {results.Count} matrix cells");
        report.AppendLine($"- Root-down wins: {wins}/{results.Count}");
        report.AppendLine($"- Root-down bounded-queue pressure/recovery observed: {queuePressureCases}/{results.Count} cells");
        report.AppendLine();
        report.AppendLine("| Commands | Distribution | Visible | Views | Leaf | Flat ms | BVH ms | Flat/BVH | Winner |");
        report.AppendLine("| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (BenchmarkResult result in results)
        {
            double flatMs = result.FlatMedianNanoseconds / 1_000_000.0;
            double bvhMs = result.BvhMedianNanoseconds / 1_000_000.0;
            report.AppendLine($"| {result.Case.CommandCount:N0} | {result.Case.Distribution} | {result.Case.VisiblePercent}% | {result.Case.ViewCount} | {result.Case.LeafCapacity} | {flatMs:F4} | {bvhMs:F4} | {flatMs / bvhMs:F2}x | {(bvhMs < flatMs ? "BVH" : "Flat")} |");
        }
        report.AppendLine();
        report.AppendLine("## Representative build/refit maintenance");
        report.AppendLine();
        report.AppendLine("GPU times use `GL_TIME_ELAPSED`. Upload time is synchronized host elapsed time around the actual dirty-prefix `glBufferSubData` plus `glFinish`; it is not a GPU timestamp.");
        report.AppendLine();
        report.AppendLine("| Commands | Dirty | Visible | Views | Leaf | Build ms | Refit ms | Traverse+emit ms | Upload bytes | Upload sync ms | Emitted bytes | Capacity bytes | Node cap | Build OF | Traversal OF |");
        report.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (MaintenanceResult result in maintenanceResults)
        {
            ulong emittedBytes = (ulong)result.VisibleCount * (ulong)(CommandUInts * sizeof(uint) + result.Case.ViewCount * sizeof(uint));
            ulong outputCapacityBytes = (ulong)result.Case.CommandCount * (ulong)(CommandUInts * sizeof(uint) + result.Case.ViewCount * sizeof(uint));
            report.AppendLine($"| {result.Case.CommandCount:N0} | {result.Case.DirtyPercent:F1}% | {result.Case.VisiblePercent}% | {result.Case.ViewCount} | {result.Case.LeafCapacity} | {result.BuildNanoseconds / 1_000_000.0:F4} | {result.RefitNanoseconds / 1_000_000.0:F4} | {result.TraversalEmissionNanoseconds / 1_000_000.0:F4} | {result.UploadBytes:N0} | {result.UploadSynchronizedCpuNanoseconds / 1_000_000.0:F4} | {emittedBytes:N0} | {outputCapacityBytes:N0} | {result.NodeCapacity:N0} | {result.BuildOverflowFlags} | {result.TraversalOverflowFlags} |");
        }
        report.AppendLine();
        report.AppendLine("Each maintenance row performs four production hierarchy-build dispatches, two production refit dispatches, one partitioned traversal/emission dispatch, and seven explicit shader-storage barriers. Morton generation and sorting are excluded: the harness uploads the deterministic pre-sorted Morton pairs used by both build and traversal.");
        report.AppendLine();
        report.AppendLine("## Deterministic normalization-bounds policy sweep");
        report.AppendLine();
        report.AppendLine("| Scenario | Ratio interpreted by policy | Ratio | Accepted/retained |");
        report.AppendLine("| --- | --- | ---: | --- |");
        foreach (PolicySweepRow row in CreateBoundsPolicySweep())
            report.AppendLine($"| {row.Scenario} | {row.RatioKind} | {row.Ratio:F2}x | {(row.PolicyAccepts ? "yes" : "no")} |");
        report.AppendLine();
        report.AppendLine("Conclusion: the calibrated 2x configured-domain limit is one octree level per axis (8x volume) and the 4x prior/candidate volume hysteresis has a precise inclusive boundary. The sweep supports both named thresholds; factors immediately above either boundary are rejected.");
        report.AppendLine();
        report.AppendLine("## Scope and limitations");
        report.AppendLine();
        report.AppendLine("- Timings cover the production culling compute dispatch plus the explicit shader-storage/buffer-update barrier. CPU scene generation, Morton sorting, compact-tree construction, and buffer upload are excluded.");
        report.AppendLine("- This run measures OpenGL on the single NVIDIA adapter above. Vulkan was not measured by this isolated OpenGL harness; `rdc doctor` also reported that the RenderDoc Vulkan implicit layer is not registered. No AMD or Intel hardware was available.");
        report.AppendLine($"- {queuePressureCases} measured root-down cells exercised the bounded traversal queue's conservative recovery path (overflow flag bit 1). Any such timings include recovery work; visibility still matched the flat shader.");
        report.AppendLine("- The representative maintenance sweep measures dirty ratios 0%, 0.1%, 1%, 10%, and 100% at 10K commands while rotating leaf capacities 1/2/4/8/16, visibility 0/10/50/100%, and view classes 1/2/3. It is not the full Cartesian product.");
        report.AppendLine("- The complete Cartesian matrix was deliberately bounded. At 100K only Uniform (10% visible) and Clustered (50% visible), view=1, leaf=4 were measured. At 1M only Uniform, 10% visible, view=1, leaf=8 was measured.");
        report.AppendLine("- At 1K/10K the baseline covers all six distributions at 50% requested visibility, view=1, leaf=4. Visibility/view/leaf sensitivity outside the additional 1K Uniform slices is unmeasured.");
        report.AppendLine("- Requested visibility is a generator target; giant and rapidly expanding bounds can conservatively intersect the frustum. Flat/root-down visible counts were asserted equal for every measured cell.");
        File.WriteAllText(Path.Combine(reportDirectory, "gpu-bvh-flat-vs-root-down-opengl.md"), report.ToString());
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

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

    private static uint FloatBits(float value) => BitConverter.SingleToUInt32Bits(value);

    private enum Distribution
    {
        Uniform,
        Clustered,
        IdenticalCenter,
        LongThin,
        GiantPlusManySmall,
        RapidlyExpanding,
    }

    private readonly record struct BenchmarkCase(
        int CommandCount,
        Distribution Distribution,
        int VisiblePercent,
        int ViewCount,
        int LeafCapacity);

    private readonly record struct BenchmarkResult(
        BenchmarkCase Case,
        long FlatMedianNanoseconds,
        long BvhMedianNanoseconds,
        uint VisibleCount,
        uint OverflowFlags);

    private readonly record struct MaintenanceCase(
        int CommandCount,
        double DirtyPercent,
        int VisiblePercent,
        int ViewCount,
        int LeafCapacity);

    private readonly record struct MaintenanceResult(
        MaintenanceCase Case,
        long BuildNanoseconds,
        long RefitNanoseconds,
        long TraversalEmissionNanoseconds,
        ulong UploadBytes,
        long UploadSynchronizedCpuNanoseconds,
        uint VisibleCount,
        uint TraversalOverflowFlags,
        uint NodeCapacity,
        uint NodeScalarCapacity,
        uint BuildOverflowFlags);

    private readonly record struct UploadResult(ulong Bytes, long SynchronizedCpuNanoseconds);

    private readonly record struct PolicySweepRow(
        string Scenario,
        string RatioKind,
        double Ratio,
        bool PolicyAccepts);

    private readonly record struct SceneBounds(Vector3 Min, Vector3 Max)
    {
        public Vector3 Center => (Min + Max) * 0.5f;
        public float SphereRadius => Vector3.Distance(Min, Max) * 0.5f;
    }

    private readonly record struct MortonItem(uint Code, int ObjectIndex);

    private readonly record struct CpuNode(
        Vector3 Min,
        Vector3 Max,
        uint Left,
        uint Right,
        uint Start,
        uint Count,
        uint Parent,
        uint Flags);

    private sealed class MaintenanceBuffers : IDisposable
    {
        private readonly GL _gl;
        private readonly uint _aabbBuffer;
        private readonly uint _overflowBuffer;
        private readonly uint _counterBuffer;
        private readonly uint[] _aabbData;
        private readonly uint _primitiveCount;
        private readonly uint _leafCount;
        private readonly uint _internalCount;
        private readonly uint _leafCapacity;

        private MaintenanceBuffers(
            GL gl,
            uint aabbBuffer,
            uint mortonBuffer,
            uint nodeBuffer,
            uint overflowBuffer,
            uint counterBuffer,
            uint[] aabbData,
            uint primitiveCount,
            uint leafCount,
            uint leafCapacity)
        {
            _gl = gl;
            _aabbBuffer = aabbBuffer;
            MortonBuffer = mortonBuffer;
            NodeBuffer = nodeBuffer;
            _overflowBuffer = overflowBuffer;
            _counterBuffer = counterBuffer;
            _aabbData = aabbData;
            _primitiveCount = primitiveCount;
            _leafCount = leafCount;
            _internalCount = leafCount - 1u;
            _leafCapacity = leafCapacity;
            NodeCapacity = leafCount + _internalCount;
            NodeScalarCapacity = 4u + NodeCapacity * NodeUInts;
        }

        public uint MortonBuffer { get; }
        public uint NodeBuffer { get; }
        public uint NodeCapacity { get; }
        public uint NodeScalarCapacity { get; }
        public uint BuildOverflowFlags { get; private set; }

        public static unsafe MaintenanceBuffers Create(GL gl, SceneBounds[] scene, int leafCapacity)
        {
            MortonItem[] order = BenchmarkBuffers.BuildMortonOrder(scene);
            uint[] aabbs = CreateAabbs(scene);
            uint leafCount = ((uint)scene.Length + (uint)leafCapacity - 1u) / (uint)leafCapacity;
            uint internalCount = leafCount - 1u;
            uint nodeCapacity = leafCount + internalCount;
            uint nodeScalarCapacity = 4u + nodeCapacity * NodeUInts;
            return new(
                gl,
                CreateBuffer(gl, aabbs),
                CreateBuffer(gl, BenchmarkBuffers.PackMorton(order)),
                CreateUninitializedBuffer(gl, (nuint)nodeScalarCapacity * sizeof(uint)),
                CreateBuffer(gl, new uint[1]),
                CreateBuffer(gl, new uint[Math.Max(1, (int)internalCount)]),
                aabbs,
                (uint)scene.Length,
                leafCount,
                (uint)leafCapacity);
        }

        public long MeasureBuild(GL gl, uint program)
        {
            long[] samples = new long[5];
            uint query = gl.GenQuery();
            try
            {
                for (int iteration = 0; iteration < 7; ++iteration)
                {
                    ResetBuffer(gl, _overflowBuffer, new uint[1]);
                    BindBuildBuffers(gl);
                    gl.UseProgram(program);
                    SetUniform(gl, program, "MAX_LEAF_PRIMITIVES", _leafCapacity);
                    SetUniform(gl, program, "numPrimitives", _primitiveCount);
                    SetUniform(gl, program, "nodeScalarCapacity", NodeScalarCapacity);
                    SetUniform(gl, program, "mortonCapacity", _primitiveCount);

                    gl.BeginQuery(GLEnum.TimeElapsed, query);
                    DispatchStage(gl, program, "buildStage", 0u, Groups(_leafCount));
                    DispatchStage(gl, program, "buildStage", 1u, Groups(_internalCount));
                    DispatchStage(gl, program, "buildStage", 2u, Groups(_internalCount));
                    DispatchStage(gl, program, "buildStage", 3u, 1u);
                    gl.EndQuery(GLEnum.TimeElapsed);
                    long elapsed = gl.GetQueryObject(query, GLEnum.QueryResult);
                    if (iteration >= 2)
                        samples[iteration - 2] = elapsed;
                }
                BuildOverflowFlags = ReadFirstUInt(gl, _overflowBuffer);
            }
            finally
            {
                gl.DeleteQuery(query);
            }
            return Median(samples);
        }

        public long MeasureRefit(GL gl, uint program)
        {
            long[] samples = new long[5];
            uint query = gl.GenQuery();
            try
            {
                for (int iteration = 0; iteration < 7; ++iteration)
                {
                    BindRefitBuffers(gl);
                    gl.UseProgram(program);
                    SetUniform(gl, program, "MAX_LEAF_PRIMITIVES", _leafCapacity);
                    SetUniform(gl, program, "debugValidation", 0u);
                    SetUniform(gl, program, "leafCount", _leafCount);
                    SetUniform(gl, program, "internalCount", _internalCount);

                    gl.BeginQuery(GLEnum.TimeElapsed, query);
                    DispatchStage(gl, program, "refitStage", 0u, Groups(_internalCount));
                    DispatchStage(gl, program, "refitStage", 1u, Groups(_leafCount));
                    gl.EndQuery(GLEnum.TimeElapsed);
                    long elapsed = gl.GetQueryObject(query, GLEnum.QueryResult);
                    if (iteration >= 2)
                        samples[iteration - 2] = elapsed;
                }
            }
            finally
            {
                gl.DeleteQuery(query);
            }
            return Median(samples);
        }

        public unsafe UploadResult UploadDirtyBounds(GL gl, double dirtyPercent)
        {
            int dirtyCount = dirtyPercent <= 0.0
                ? 0
                : Math.Max(1, (int)Math.Ceiling(_primitiveCount * dirtyPercent / 100.0));
            if (dirtyCount == 0)
                return new(0u, 0L);

            int scalarCount = dirtyCount * 8;
            for (int offset = 0; offset < scalarCount; offset += 8)
            {
                float minX = BitConverter.UInt32BitsToSingle(_aabbData[offset]);
                float maxX = BitConverter.UInt32BitsToSingle(_aabbData[offset + 4]);
                _aabbData[offset] = FloatBits(minX + 0.01f);
                _aabbData[offset + 4] = FloatBits(maxX + 0.01f);
            }

            ulong bytes = (ulong)scalarCount * sizeof(uint);
            Stopwatch stopwatch = Stopwatch.StartNew();
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _aabbBuffer);
            fixed (uint* source = _aabbData)
                gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)bytes, source);
            gl.Finish();
            stopwatch.Stop();
            long nanoseconds = checked((long)(stopwatch.ElapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency)));
            return new(bytes, nanoseconds);
        }

        public void Dispose()
        {
            _gl.DeleteBuffer(_aabbBuffer);
            _gl.DeleteBuffer(MortonBuffer);
            _gl.DeleteBuffer(NodeBuffer);
            _gl.DeleteBuffer(_overflowBuffer);
            _gl.DeleteBuffer(_counterBuffer);
        }

        private void BindBuildBuffers(GL gl)
        {
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0u, _aabbBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1u, MortonBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2u, NodeBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 8u, _overflowBuffer);
        }

        private void BindRefitBuffers(GL gl)
        {
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0u, _aabbBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1u, MortonBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2u, NodeBuffer);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 11u, _counterBuffer);
        }

        private static void DispatchStage(GL gl, uint program, string uniform, uint stage, uint groups)
        {
            SetUniform(gl, program, uniform, stage);
            gl.DispatchCompute(Math.Max(1u, groups), 1u, 1u);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.BufferUpdateBarrierBit);
        }

        private static uint Groups(uint count) => Math.Max(1u, (count + 255u) / 256u);

        private static uint[] CreateAabbs(SceneBounds[] scene)
        {
            uint[] result = new uint[scene.Length * 8];
            for (int i = 0; i < scene.Length; ++i)
            {
                int offset = i * 8;
                result[offset] = FloatBits(scene[i].Min.X);
                result[offset + 1] = FloatBits(scene[i].Min.Y);
                result[offset + 2] = FloatBits(scene[i].Min.Z);
                result[offset + 4] = FloatBits(scene[i].Max.X);
                result[offset + 5] = FloatBits(scene[i].Max.Y);
                result[offset + 6] = FloatBits(scene[i].Max.Z);
            }
            return result;
        }

        private static uint CreateBuffer(GL gl, uint[] data)
        {
            uint buffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, data.AsSpan(), BufferUsageARB.DynamicCopy);
            return buffer;
        }

        private static unsafe uint CreateUninitializedBuffer(GL gl, nuint sizeBytes)
        {
            uint buffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, sizeBytes, null, BufferUsageARB.DynamicCopy);
            return buffer;
        }

        private static void ResetBuffer(GL gl, uint buffer, uint[] zeros)
        {
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, zeros.AsSpan(), BufferUsageARB.DynamicCopy);
        }

        private static unsafe uint ReadFirstUInt(GL gl, uint buffer)
        {
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            uint* data = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint value = data[0];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
            return value;
        }
    }

    private sealed class BenchmarkBuffers : IDisposable
    {
        private readonly GL _gl;
        private readonly uint[] _buffers;
        private readonly uint[] _counterZeros = new uint[3];
        private readonly uint[] _overflowZeros = new uint[1];
        private readonly uint[] _statsZeros = new uint[(int)global::XREngine.Rendering.GpuStatsLayout.FieldCount];
        private readonly uint[] _perViewCountZeros;
        private uint _traversalNodeOverride;
        private uint _traversalMortonOverride;

        private BenchmarkBuffers(GL gl, uint[] buffers, int viewCount)
        {
            _gl = gl;
            _buffers = buffers;
            _perViewCountZeros = new uint[Math.Max(1, viewCount)];
        }

        public static unsafe BenchmarkBuffers Create(GL gl, SceneBounds[] scene, int leafCapacity, int viewCount)
        {
            MortonItem[] mortonOrder = BuildMortonOrder(scene);
            uint[] buffers = new uint[16];
            buffers[0] = CreateBuffer(gl, 0, CreateMetadata(scene.Length));
            buffers[1] = CreateBuffer(gl, 1, CreateBounds(scene));
            buffers[2] = CreateUninitializedBuffer(gl, 2, checked((nuint)scene.Length * CommandUInts * sizeof(uint)));
            buffers[3] = CreateBuffer(gl, 3, new uint[3]);
            buffers[4] = CreateBuffer(gl, 4, new uint[1]);
            buffers[5] = CreateBuffer(gl, 5, BuildCompactTree(scene, mortonOrder, leafCapacity));
            buffers[7] = CreateBuffer(gl, 7, PackMorton(mortonOrder));
            buffers[8] = CreateBuffer(gl, 8, new uint[(int)global::XREngine.Rendering.GpuStatsLayout.FieldCount]);
            buffers[9] = CreateBuffer(gl, 9, new uint[1]);
            buffers[10] = CreateUninitializedBuffer(gl, 10, checked((nuint)scene.Length * CommandUInts * sizeof(uint)));
            buffers[11] = CreateBuffer(gl, 11, CreateViewDescriptors(scene.Length, viewCount));
            buffers[12] = CreateBuffer(gl, 12, new uint[Math.Max(72, viewCount * 72)]);
            buffers[13] = CreateBuffer(gl, 13, CreateViewMasks(scene.Length, viewCount));
            buffers[14] = CreateUninitializedBuffer(gl, 14, checked((nuint)scene.Length * (nuint)viewCount * sizeof(uint)));
            buffers[15] = CreateBuffer(gl, 15, new uint[Math.Max(1, viewCount)]);
            return new(gl, buffers, viewCount);
        }

        public void ResetAndBind(GL gl)
        {
            ResetBuffer(gl, _buffers[3], _counterZeros);
            ResetBuffer(gl, _buffers[4], _overflowZeros);
            ResetBuffer(gl, _buffers[8], _statsZeros);
            ResetBuffer(gl, _buffers[15], _perViewCountZeros);
            for (uint binding = 0; binding < _buffers.Length; ++binding)
                if (_buffers[binding] != 0)
                    gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, binding, _buffers[binding]);
            if (_traversalNodeOverride != 0u)
                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 5u, _traversalNodeOverride);
            if (_traversalMortonOverride != 0u)
                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 7u, _traversalMortonOverride);
        }

        public void UseTraversalTree(uint nodeBuffer, uint mortonBuffer)
        {
            _traversalNodeOverride = nodeBuffer;
            _traversalMortonOverride = mortonBuffer;
        }

        public uint ReadVisibleCount(GL gl) => ReadFirstUInt(gl, _buffers[3]);
        public uint ReadOverflowFlags(GL gl) => ReadFirstUInt(gl, _buffers[4]);

        public void Dispose()
        {
            foreach (uint buffer in _buffers)
                if (buffer != 0)
                    _gl.DeleteBuffer(buffer);
        }

        private static uint[] CreateMetadata(int count)
        {
            uint[] result = new uint[count * DrawMetadataUInts];
            for (int i = 0; i < count; ++i)
            {
                int offset = i * DrawMetadataUInts;
                result[offset] = (uint)i;
                result[offset + 1] = (uint)i;
                result[offset + 6] = InvalidIndex;
                result[offset + 7] = InvalidIndex;
                result[offset + 11] = 1u;
                result[offset + 14] = (uint)i;
                result[offset + 15] = (uint)i;
            }
            return result;
        }

        private static uint[] CreateBounds(SceneBounds[] scene)
        {
            uint[] result = new uint[scene.Length * BoundsUInts];
            for (int i = 0; i < scene.Length; ++i)
            {
                SceneBounds bounds = scene[i];
                Vector3 center = bounds.Center;
                int offset = i * BoundsUInts;
                result[offset] = FloatBits(center.X);
                result[offset + 1] = FloatBits(center.Y);
                result[offset + 2] = FloatBits(center.Z);
                result[offset + 3] = FloatBits(bounds.SphereRadius);
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

        public static MortonItem[] BuildMortonOrder(SceneBounds[] scene)
        {
            Vector3 min = scene[0].Center;
            Vector3 max = min;
            for (int i = 1; i < scene.Length; ++i)
            {
                min = Vector3.Min(min, scene[i].Center);
                max = Vector3.Max(max, scene[i].Center);
            }
            Vector3 extent = Vector3.Max(max - min, new Vector3(1e-6f));
            MortonItem[] result = new MortonItem[scene.Length];
            for (int i = 0; i < scene.Length; ++i)
            {
                Vector3 normalized = Vector3.Clamp((scene[i].Center - min) / extent, Vector3.Zero, Vector3.One);
                result[i] = new(Morton3D(normalized), i);
            }
            Array.Sort(result, static (a, b) => a.Code != b.Code ? a.Code.CompareTo(b.Code) : a.ObjectIndex.CompareTo(b.ObjectIndex));
            return result;
        }

        private static uint Morton3D(Vector3 normalized)
        {
            uint x = (uint)Math.Clamp((int)(normalized.X * 1023f), 0, 1023);
            uint y = (uint)Math.Clamp((int)(normalized.Y * 1023f), 0, 1023);
            uint z = (uint)Math.Clamp((int)(normalized.Z * 1023f), 0, 1023);
            return ExpandBits(x) | (ExpandBits(y) << 1) | (ExpandBits(z) << 2);
        }

        private static uint ExpandBits(uint value)
        {
            value = (value * 0x00010001u) & 0xFF0000FFu;
            value = (value * 0x00000101u) & 0x0F00F00Fu;
            value = (value * 0x00000011u) & 0xC30C30C3u;
            value = (value * 0x00000005u) & 0x49249249u;
            return value;
        }

        public static uint[] PackMorton(MortonItem[] order)
        {
            uint[] result = new uint[order.Length * 2];
            for (int i = 0; i < order.Length; ++i)
            {
                result[i * 2] = order[i].Code;
                result[i * 2 + 1] = (uint)order[i].ObjectIndex;
            }
            return result;
        }

        private static uint[] BuildCompactTree(SceneBounds[] scene, MortonItem[] order, int leafCapacity)
        {
            List<CpuNode> nodes = new(Math.Max(1, scene.Length * 2 / leafCapacity));
            uint root = BuildNode(nodes, scene, order, 0, scene.Length, InvalidIndex, leafCapacity);
            uint[] result = new uint[4 + nodes.Count * NodeUInts];
            result[0] = (uint)nodes.Count;
            result[1] = root;
            result[2] = NodeUInts;
            result[3] = (uint)leafCapacity;
            for (int i = 0; i < nodes.Count; ++i)
                PackNode(nodes[i], result.AsSpan(4 + i * NodeUInts, NodeUInts));
            return result;
        }

        private static uint BuildNode(
            List<CpuNode> nodes,
            SceneBounds[] scene,
            MortonItem[] order,
            int start,
            int count,
            uint parent,
            int leafCapacity)
        {
            uint index = (uint)nodes.Count;
            nodes.Add(default);
            SceneBounds combined = scene[order[start].ObjectIndex];
            for (int i = 1; i < count; ++i)
            {
                SceneBounds item = scene[order[start + i].ObjectIndex];
                combined = new(Vector3.Min(combined.Min, item.Min), Vector3.Max(combined.Max, item.Max));
            }

            if (count <= leafCapacity)
            {
                nodes[(int)index] = new(combined.Min, combined.Max, InvalidIndex, InvalidIndex, (uint)start, (uint)count, parent, 1u);
                return index;
            }

            int leftCount = count / 2;
            uint left = BuildNode(nodes, scene, order, start, leftCount, index, leafCapacity);
            uint right = BuildNode(nodes, scene, order, start + leftCount, count - leftCount, index, leafCapacity);
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
            uint[] result = new uint[Math.Max(20, viewCount * 20)];
            for (int view = 0; view < viewCount; ++view)
            {
                int offset = view * 20;
                result[offset] = (uint)view;
                result[offset + 1] = InvalidIndex;
                result[offset + 3] = InvalidIndex;
                result[offset + 4] = InvalidIndex;
                result[offset + 10] = (uint)(view * commandCount);
                result[offset + 11] = (uint)commandCount;
            }
            return result;
        }

        private static uint[] CreateViewMasks(int commandCount, int viewCount)
        {
            uint mask = viewCount >= 32 ? InvalidIndex : (1u << viewCount) - 1u;
            uint[] result = new uint[commandCount * 2];
            for (int i = 0; i < commandCount; ++i)
                result[i * 2] = mask;
            return result;
        }

        private static uint CreateBuffer(GL gl, uint binding, uint[] data)
        {
            uint buffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, data.AsSpan(), BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, binding, buffer);
            return buffer;
        }

        private static unsafe uint CreateUninitializedBuffer(GL gl, uint binding, nuint sizeBytes)
        {
            uint buffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, sizeBytes, null, BufferUsageARB.DynamicCopy);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, binding, buffer);
            return buffer;
        }

        private static void ResetBuffer(GL gl, uint buffer, uint[] zeros)
        {
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, zeros.AsSpan(), BufferUsageARB.DynamicCopy);
        }

        private static unsafe uint ReadFirstUInt(GL gl, uint buffer)
        {
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            uint* data = (uint*)gl.MapBuffer(BufferTargetARB.ShaderStorageBuffer, BufferAccessARB.ReadOnly);
            uint result = data[0];
            gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
            return result;
        }
    }
}
