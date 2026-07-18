using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;

namespace XREngine.Benchmarks;

[MemoryDiagnoser]
public sealed class CpuSceneBvhBenchmarks
{
    private CpuBvhRenderTree<BenchmarkItem> _tree = null!;
    private BenchmarkItem[] _items = null!;
    private Frustum _frustum;
    private CountingVisitor _visitor;
    private int _moveCursor;

    [Params(1_000, 10_000, 100_000)]
    public int ItemCount { get; set; }

    [Params(0.001, 0.01, 0.1, 1.0)]
    public double DirtyRatio { get; set; }

    [Params(1, 2, 4, 8, 16)]
    public int LeafCapacity { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _items = CpuSceneBvhWorkloads.Create(ItemCount, CpuSceneDistribution.Uniform, 20260717);
        _tree = new CpuBvhRenderTree<BenchmarkItem>(
            CpuSceneBvhWorkloads.SceneBounds,
            [.. _items],
            new CpuBvhOptions { LeafCapacity = LeafCapacity });
        _frustum = CpuSceneBvhWorkloads.CreateFrustum(0.5f);
    }

    [Benchmark]
    public CpuBvhDiagnostics Construct()
    {
        var tree = new CpuBvhRenderTree<BenchmarkItem>(
            CpuSceneBvhWorkloads.SceneBounds,
            [.. _items],
            new CpuBvhOptions { LeafCapacity = LeafCapacity });
        return tree.GetDiagnostics();
    }

    [Benchmark]
    public void CleanSwap() => _tree.Swap();

    [Benchmark]
    public CpuBvhDiagnostics Refit()
    {
        int count = Math.Max(1, (int)(ItemCount * DirtyRatio));
        CpuSceneBvhWorkloads.Move(_tree, _items, count, ref _moveCursor);
        _tree.Swap();
        return _tree.GetDiagnostics();
    }

    [Benchmark]
    public int FrustumTraversal()
    {
        _visitor.Count = 0;
        _tree.CollectVisible(_frustum, ref _visitor);
        return _visitor.Count;
    }

    private struct CountingVisitor : ICpuBvhVisitor<BenchmarkItem>
    {
        public int Count;
        public void Visit(BenchmarkItem item) => Count++;
    }
}

public static class CpuSceneBvhReportHarness
{
    private static readonly CpuSceneDistribution[] Distributions = Enum.GetValues<CpuSceneDistribution>();
    private static readonly double[] DirtyRatios = [0.0, 0.001, 0.01, 0.1, 1.0];
    private static readonly float[] VisibleRatios = [0.0f, 0.1f, 0.5f, 1.0f];
    private static readonly int[] ViewCounts = [1, 2, 4, 8];

    public static int Run(string[] args)
    {
        string output = GetOption(args, "--output") ?? Path.Combine(
            Directory.GetCurrentDirectory(),
            "Build",
            "_AgentValidation",
            "cpu-bvh-report.json");
        int[] counts = ParseCounts(GetOption(args, "--counts"));
        int[] leafCapacities = ParsePositiveValues(GetOption(args, "--leaf-capacities"), [8]);
        int seed = GetIntOption(args, "--seed", 20260717);
        var results = new List<CpuSceneBvhWorkloadResult>();

        foreach (int leafCapacity in leafCapacities)
        foreach (int count in counts)
        foreach (CpuSceneDistribution distribution in Distributions)
        {
            BenchmarkItem[] items = CpuSceneBvhWorkloads.Create(count, distribution, seed);
            long allocatedBefore = GC.GetTotalAllocatedBytes(true);
            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);
            long buildStart = Stopwatch.GetTimestamp();
            var tree = new CpuBvhRenderTree<BenchmarkItem>(
                CpuSceneBvhWorkloads.SceneBounds,
                [.. items],
                new CpuBvhOptions { LeafCapacity = leafCapacity });
            double buildMilliseconds = Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds;
            CpuBvhDiagnostics buildDiagnostics = tree.GetDiagnostics();
            results.Add(new CpuSceneBvhWorkloadResult(
                count,
                distribution,
                leafCapacity,
                "Build",
                0.0,
                0.0f,
                0,
                buildMilliseconds,
                GC.GetTotalAllocatedBytes(true) - allocatedBefore,
                GC.CollectionCount(0) - gen0,
                GC.CollectionCount(1) - gen1,
                GC.CollectionCount(2) - gen2,
                buildDiagnostics));

            int cursor = 0;
            foreach (double dirtyRatio in DirtyRatios)
            {
                int dirtyCount = (int)(count * dirtyRatio);
                if (dirtyRatio > 0.0)
                    dirtyCount = Math.Max(1, dirtyCount);
                long updateAllocatedBefore = GC.GetTotalAllocatedBytes(true);
                int updateGen0 = GC.CollectionCount(0);
                int updateGen1 = GC.CollectionCount(1);
                int updateGen2 = GC.CollectionCount(2);
                long updateStart = Stopwatch.GetTimestamp();
                CpuSceneBvhWorkloads.Move(tree, items, dirtyCount, ref cursor);
                tree.Swap();
                double updateMilliseconds = Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
                results.Add(new CpuSceneBvhWorkloadResult(
                    count,
                    distribution,
                    leafCapacity,
                    "Update",
                    dirtyRatio,
                    0.0f,
                    0,
                    updateMilliseconds,
                    GC.GetTotalAllocatedBytes(true) - updateAllocatedBefore,
                    GC.CollectionCount(0) - updateGen0,
                    GC.CollectionCount(1) - updateGen1,
                    GC.CollectionCount(2) - updateGen2,
                    tree.GetDiagnostics()));
            }

            foreach (float visibleRatio in VisibleRatios)
            foreach (int viewCount in ViewCounts)
            {
                Frustum frustum = CpuSceneBvhWorkloads.CreateFrustum(visibleRatio);
                var state = new TraversalState(tree, frustum, viewCount);
                Measure(
                    state,
                    count,
                    distribution,
                    leafCapacity,
                    "Traversal",
                    0.0,
                    visibleRatio,
                    viewCount,
                    static value => value.Traverse(),
                    results);
            }
        }

        var report = new CpuSceneBvhReport(
            DateTime.UtcNow,
            Environment.MachineName,
            Environment.OSVersion.ToString(),
            Environment.Version.ToString(),
            seed,
            results);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"CPU scene-BVH report written to {Path.GetFullPath(output)} ({results.Count} workloads).");
        return 0;
    }

    private static void Measure<TState>(
        TState state,
        int count,
        CpuSceneDistribution distribution,
        int leafCapacity,
        string operation,
        double dirtyRatio,
        float visibleRatio,
        int viewCount,
        Action<TState> action,
        List<CpuSceneBvhWorkloadResult> results)
        where TState : IHasCpuBvhDiagnostics
    {
        action(state);
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long start = Stopwatch.GetTimestamp();
        action(state);
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        results.Add(new CpuSceneBvhWorkloadResult(
            count,
            distribution,
            leafCapacity,
            operation,
            dirtyRatio,
            visibleRatio,
            viewCount,
            elapsedMilliseconds,
            GC.GetTotalAllocatedBytes(true) - allocatedBefore,
            GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1,
            GC.CollectionCount(2) - gen2,
            state.GetDiagnostics()));
    }

    private static int[] ParseCounts(string? text)
        => ParsePositiveValues(text, [1_000, 10_000, 100_000]);

    private static int[] ParsePositiveValues(string? text, int[] fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        int[] values = [.. text.Split(',').Select(static value => int.Parse(value.Trim())).Where(static value => value > 0)];
        return values.Length == 0 ? fallback : values;
    }

    private static string? GetOption(string[] args, string option)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static int GetIntOption(string[] args, string option, int fallback)
        => int.TryParse(GetOption(args, option), out int value) ? value : fallback;

    private readonly struct TraversalState(
        CpuBvhRenderTree<BenchmarkItem> tree,
        Frustum frustum,
        int viewCount) : IHasCpuBvhDiagnostics
    {
        public void Traverse()
        {
            var visitor = new CountVisitor();
            for (int i = 0; i < viewCount; i++)
                tree.CollectVisible(frustum, ref visitor);
        }
        public CpuBvhDiagnostics GetDiagnostics() => tree.GetDiagnostics();
    }

    private struct CountVisitor : ICpuBvhVisitor<BenchmarkItem>
    {
        public int Count;
        public void Visit(BenchmarkItem item) => Count++;
    }
}

internal interface IHasCpuBvhDiagnostics
{
    CpuBvhDiagnostics GetDiagnostics();
}

internal sealed class BenchmarkTreeState(CpuBvhRenderTree<BenchmarkItem> tree) : IHasCpuBvhDiagnostics
{
    public CpuBvhRenderTree<BenchmarkItem> Tree { get; } = tree;
    public CpuBvhDiagnostics GetDiagnostics() => Tree.GetDiagnostics();
}

internal static class CpuSceneBvhWorkloads
{
    public static readonly AABB SceneBounds = AABB.FromCenterSize(Vector3.Zero, new Vector3(20_000.0f));

    public static BenchmarkItem[] Create(int count, CpuSceneDistribution distribution, int seed)
    {
        var random = new Random(seed + (int)distribution * 7919 + count);
        var items = new BenchmarkItem[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 center = distribution switch
            {
                CpuSceneDistribution.Clustered => Clustered(random, i),
                CpuSceneDistribution.IdenticalCentroid => Vector3.Zero,
                CpuSceneDistribution.LongThin => new Vector3(RandomRange(random, 5000.0f), RandomRange(random, 20.0f), RandomRange(random, 20.0f)),
                CpuSceneDistribution.GiantPlusSmall when i == 0 => Vector3.Zero,
                _ => RandomVector(random, 5000.0f),
            };
            Vector3 size = distribution switch
            {
                CpuSceneDistribution.LongThin => new Vector3(40.0f, 0.5f, 0.5f),
                CpuSceneDistribution.GiantPlusSmall when i == 0 => new Vector3(9000.0f),
                _ => new Vector3(0.5f + random.NextSingle() * 8.0f),
            };
            items[i] = new BenchmarkItem(i, AABB.FromCenterSize(center, size));
        }
        return items;
    }

    public static void Move(CpuBvhRenderTree<BenchmarkItem> tree, BenchmarkItem[] items, int count, ref int cursor)
    {
        for (int i = 0; i < count; i++)
        {
            BenchmarkItem item = items[cursor++ % items.Length];
            AABB bounds = item.LocalCullingVolume!.Value;
            item.LocalCullingVolume = AABB.FromCenterSize(bounds.Center + new Vector3(0.125f, -0.0625f, 0.03125f), bounds.Size);
            item.OctreeNode!.QueueItemMoved(item);
        }
    }

    public static Frustum CreateFrustum(float visibleRatio)
    {
        float far = visibleRatio switch
        {
            <= 0.0f => 1.0f,
            <= 0.1f => 900.0f,
            <= 0.5f => 3500.0f,
            _ => 12_000.0f,
        };
        Vector3 position = visibleRatio <= 0.0f ? new Vector3(100_000.0f) : new Vector3(0.0f, 0.0f, 5500.0f);
        return new Frustum(120.0f, 1.0f, 0.1f, far, -Vector3.UnitZ, Vector3.UnitY, position);
    }

    private static Vector3 Clustered(Random random, int index)
    {
        int cluster = index & 7;
        Vector3 anchor = new((cluster & 1) * 2400.0f - 1200.0f, (cluster & 2) * 1200.0f - 1200.0f, (cluster & 4) * 600.0f - 1200.0f);
        return anchor + RandomVector(random, 120.0f);
    }

    private static Vector3 RandomVector(Random random, float extent)
        => new(RandomRange(random, extent), RandomRange(random, extent), RandomRange(random, extent));
    private static float RandomRange(Random random, float extent)
        => (random.NextSingle() * 2.0f - 1.0f) * extent;
}

public enum CpuSceneDistribution
{
    Uniform,
    Clustered,
    IdenticalCentroid,
    LongThin,
    GiantPlusSmall,
}

public sealed class BenchmarkItem(int id, AABB bounds) : IOctreeItem
{
    public int Id { get; } = id;
    public bool ShouldRender => true;
    public IRenderableBase? Owner => null;
    public AABB? LocalCullingVolume { get; set; } = bounds;
    public Matrix4x4 CullingOffsetMatrix { get; set; } = Matrix4x4.Identity;
    public OctreeNodeBase? OctreeNode { get; set; }
}

public sealed record CpuSceneBvhWorkloadResult(
    int ItemCount,
    CpuSceneDistribution Distribution,
    int LeafCapacity,
    string Operation,
    double DirtyRatio,
    float VisibleRatio,
    int ViewCount,
    double ElapsedMilliseconds,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    CpuBvhDiagnostics Diagnostics);

public sealed record CpuSceneBvhReport(
    DateTime GeneratedUtc,
    string MachineName,
    string OperatingSystem,
    string RuntimeVersion,
    int Seed,
    IReadOnlyList<CpuSceneBvhWorkloadResult> Workloads);
