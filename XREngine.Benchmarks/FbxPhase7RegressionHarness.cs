using System.Diagnostics;
using System.Text.Json;
using XREngine.Fbx;

namespace XREngine.Benchmarks;

public static class FbxPhase7RegressionHarness
{
    private static readonly IReadOnlyDictionary<string, FbxPhase7Budget> Budgets = new Dictionary<string, FbxPhase7Budget>(StringComparer.Ordinal)
    {
        ["synthetic-static-scene-ascii"] = new(100.0, 1_500_000),
        ["synthetic-phase4-skinned-animation-ascii"] = new(150.0, 4_500_000),
    };

    public static int Run(string[] args)
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        string manifestPath = Path.Combine(workspaceRoot, FbxPhase0Decisions.CorpusManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string outputPath = GetOption(args, "--out")
            ?? Path.Combine(workspaceRoot, "Build", "Reports", "fbx-phase7-regression.json");
        int iterations = GetIntOption(args, "--iterations", 24);

        FbxCorpusManifest manifest = FbxCorpusManifest.Load(manifestPath);
        FbxCorpusEntry[] entries = manifest.Entries
            .Where(static entry => entry.Availability == FbxCorpusAvailability.CheckedIn && entry.IncludeInPerformanceBaseline)
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length == 0)
        {
            Console.Error.WriteLine("No checked-in FBX performance-baseline entries were found in the corpus manifest.");
            return 1;
        }

        List<FbxPhase7WorkloadResult> workloads = new(entries.Length);
        foreach (FbxCorpusEntry entry in entries)
        {
            if (!Budgets.TryGetValue(entry.Id, out FbxPhase7Budget? budget))
            {
                Console.Error.WriteLine($"No Phase 7 performance budget is defined for '{entry.Id}'.");
                return 1;
            }

            string assetPath = Path.Combine(workspaceRoot, entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
            FbxPhase7WorkloadResult result = MeasureWorkload(entry, assetPath, iterations, budget);
            workloads.Add(result);
        }

        FbxPhase7ParallelResult parallel = MeasureParallel(entries, workspaceRoot, iterations);
        FbxPhase7RegressionReport report = new(
            GeneratedUtc: DateTime.UtcNow,
            Iterations: iterations,
            Workloads: workloads,
            Parallel: parallel);

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, FbxCorpusJson.SerializerOptions));

        Console.WriteLine($"FBX phase 7 regression report written to {outputPath}");
        foreach (FbxPhase7WorkloadResult workload in workloads)
        {
            Console.WriteLine($"- {workload.AssetId}: avgMs={workload.AverageMilliseconds:F3}, allocBytes/iter={workload.AllocatedBytesPerIteration}, withinBudget={workload.WithinBudget}");
        }
        Console.WriteLine($"- parallel: sequentialMs={parallel.SequentialMilliseconds:F3}, parallelMs={parallel.ParallelMilliseconds:F3}, speedup={parallel.Speedup:F3}");

        return workloads.All(static workload => workload.WithinBudget) ? 0 : 1;
    }

    private static FbxPhase7WorkloadResult MeasureWorkload(FbxCorpusEntry entry, string assetPath, int iterations, FbxPhase7Budget budget)
    {
        FullRoundTrip(assetPath);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
            FullRoundTrip(assetPath);
        stopwatch.Stop();
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        double averageMilliseconds = stopwatch.Elapsed.TotalMilliseconds / iterations;
        long allocatedBytesPerIteration = (allocatedAfter - allocatedBefore) / iterations;
        bool withinBudget = averageMilliseconds <= budget.MaxAverageMilliseconds
            && allocatedBytesPerIteration <= budget.MaxAllocatedBytesPerIteration;

        return new FbxPhase7WorkloadResult(
            AssetId: entry.Id,
            AverageMilliseconds: averageMilliseconds,
            AllocatedBytesPerIteration: allocatedBytesPerIteration,
            Budget: budget,
            WithinBudget: withinBudget);
    }

    private static FbxPhase7ParallelResult MeasureParallel(FbxCorpusEntry[] entries, string workspaceRoot, int iterations)
    {
        List<string> assetPaths = entries
            .Select(entry => Path.Combine(workspaceRoot, entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar)))
            .ToList();

        Stopwatch sequentialStopwatch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            foreach (string assetPath in assetPaths)
                FullRoundTrip(assetPath);
        }
        sequentialStopwatch.Stop();

        Stopwatch parallelStopwatch = Stopwatch.StartNew();
        Parallel.For(
            0,
            iterations,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
            },
            _ =>
            {
                foreach (string assetPath in assetPaths)
                    FullRoundTrip(assetPath);
            });
        parallelStopwatch.Stop();

        double sequentialMilliseconds = sequentialStopwatch.Elapsed.TotalMilliseconds;
        double parallelMilliseconds = parallelStopwatch.Elapsed.TotalMilliseconds;
        double speedup = parallelMilliseconds > 0.0
            ? sequentialMilliseconds / parallelMilliseconds
            : 0.0;

        return new FbxPhase7ParallelResult(sequentialMilliseconds, parallelMilliseconds, speedup);
    }

    private static void FullRoundTrip(string assetPath)
    {
        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(assetPath);
        FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural);
        FbxGeometryDocument geometry = FbxGeometryParser.Parse(structural, semantic);
        FbxDeformerDocument deformers = FbxDeformerParser.Parse(structural, semantic);
        FbxAnimationDocument animations = FbxAnimationParser.Parse(structural, semantic, deformers);
        byte[] binary = FbxBinaryExporter.Export(semantic, geometry, deformers, animations);
        using FbxStructuralDocument reparsed = FbxStructuralParser.Parse(binary);
        if (reparsed.Nodes.Count == 0)
            throw new InvalidDataException($"FBX roundtrip produced no structural nodes for '{assetPath}'.");
    }

    private static string ResolveWorkspaceRoot()
        => TryFindWorkspaceRoot(Directory.GetCurrentDirectory())
        ?? TryFindWorkspaceRoot(AppContext.BaseDirectory)
        ?? throw new DirectoryNotFoundException("Could not locate the workspace root from the current directory or benchmark base directory.");

    private static string? TryFindWorkspaceRoot(string startPath)
    {
        DirectoryInfo? directory = new(startPath);
        if (!directory.Exists)
            return null;

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static string? GetOption(string[] args, string optionName)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(optionName, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }

    private static int GetIntOption(string[] args, string optionName, int defaultValue)
    {
        string? text = GetOption(args, optionName);
        return int.TryParse(text, out int parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }
}

public sealed record FbxPhase7Budget(
    double MaxAverageMilliseconds,
    long MaxAllocatedBytesPerIteration);

public sealed record FbxPhase7WorkloadResult(
    string AssetId,
    double AverageMilliseconds,
    long AllocatedBytesPerIteration,
    FbxPhase7Budget Budget,
    bool WithinBudget);

public sealed record FbxPhase7ParallelResult(
    double SequentialMilliseconds,
    double ParallelMilliseconds,
    double Speedup);

public sealed record FbxPhase7RegressionReport(
    DateTime GeneratedUtc,
    int Iterations,
    IReadOnlyList<FbxPhase7WorkloadResult> Workloads,
    FbxPhase7ParallelResult Parallel);
