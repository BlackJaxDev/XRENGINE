using System.Diagnostics;
using System.Text.Json;
using System.Reflection;

namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>Runs one stable CPU matrix work-item as a headless editor command.</summary>
public static class PhysicsChainCpuBenchmarkCommand
{
    public const string WorkIndexPrefix = "--physics-chain-cpu-benchmark-work-index=";
    public const string EnvironmentPrefix = "--physics-chain-benchmark-environment=";

    public static bool TryRun(string[] args)
    {
        string? workIndexValue = FindValue(args, WorkIndexPrefix);
        if (workIndexValue is null)
            return false;

        try
        {
            if (!long.TryParse(workIndexValue, out long workIndex) || workIndex < 0L)
                throw new ArgumentException($"{WorkIndexPrefix} requires a non-negative stable work index.");
            string environmentPath = FindValue(args, EnvironmentPrefix)
                ?? throw new ArgumentException($"{EnvironmentPrefix} requires a benchmark-environment JSON path.");
            string? buildConfiguration = typeof(PhysicsChainCpuBenchmarkCommand).Assembly
                .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
            if (!string.Equals(buildConfiguration, "Release", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Physics-chain benchmark evidence must be captured from a Release build.");
            string configuredRunRoot = Environment.GetEnvironmentVariable(
                PhysicsChainBenchmarkResultWriter.RunRootEnvironmentVariable)
                ?? throw new InvalidOperationException(
                    $"{PhysicsChainBenchmarkResultWriter.RunRootEnvironmentVariable} must name a bounded validation run root.");

            var policy = new PhysicsChainBenchmarkRunPolicy();
            PhysicsChainBenchmarkWorkItem workItem = FindWorkItem(policy, workIndex);
            ValidateSupported(workItem.MatrixCase);
            PhysicsChainBenchmarkEnvironment environment = ReadEnvironment(environmentPath);
            if (environment.RenderBackend != workItem.MatrixCase.RenderBackend)
                throw new InvalidOperationException("Environment and matrix-case render backends do not match.");

            var runner = new PhysicsChainBenchmarkRunner(new PhysicsChainBenchmarkConfiguration());
            var scenario = new PhysicsChainCpuBenchmarkScenario();
            var processState = new PhysicsChainBenchmarkProcessState(
                Debugger.IsAttached,
                ValidationLayersEnabled: false,
                VerbosePerChainLoggingEnabled: false,
                DebugDrawingEnabled: false,
                EditorOnlyInstrumentationEnabled: false);
            PhysicsChainBenchmarkEvidence evidence = runner.Run(
                scenario,
                workItem,
                policy,
                environment,
                processState);
            string path = PhysicsChainBenchmarkEvidenceWriter.Write(
                Environment.CurrentDirectory,
                configuredRunRoot,
                evidence);
            Console.WriteLine($"Physics-chain CPU benchmark evidence: {path}");
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Physics-chain CPU benchmark failed: {ex.Message}");
            Environment.ExitCode = 1;
        }

        return true;
    }

    private static PhysicsChainBenchmarkWorkItem FindWorkItem(
        PhysicsChainBenchmarkRunPolicy policy,
        long stableIndex)
    {
        foreach (PhysicsChainBenchmarkWorkItem workItem in PhysicsChainBenchmarkSweep.Enumerate(policy))
            if (workItem.StableIndex == stableIndex)
                return workItem;
        throw new ArgumentOutOfRangeException(nameof(stableIndex), "Stable work index exceeds the benchmark sweep.");
    }

    private static void ValidateSupported(in PhysicsChainBenchmarkCase matrixCase)
    {
        if (matrixCase.ExecutionMode is not (PhysicsChainBenchmarkExecutionMode.CpuStrict
            or PhysicsChainBenchmarkExecutionMode.CpuQualityTiered))
            throw new NotSupportedException("This headless command supports CPU benchmark work-items only.");
        if (matrixCase.RenderingMode is PhysicsChainBenchmarkRenderingMode.IdenticalInstancedMeshes
            or PhysicsChainBenchmarkRenderingMode.DiverseSkinnedRenderers)
            throw new NotSupportedException("Rendered mesh work-items require the live editor benchmark bridge.");
    }

    private static PhysicsChainBenchmarkEnvironment ReadEnvironment(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Benchmark environment JSON was not found.", fullPath);
        return JsonSerializer.Deserialize<PhysicsChainBenchmarkEnvironment>(File.ReadAllText(fullPath))
            ?? throw new InvalidDataException("Benchmark environment JSON was empty or invalid.");
    }

    private static string? FindValue(string[] args, string prefix)
    {
        for (int i = 0; i < args.Length; ++i)
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return args[i][prefix.Length..];
        return null;
    }
}
