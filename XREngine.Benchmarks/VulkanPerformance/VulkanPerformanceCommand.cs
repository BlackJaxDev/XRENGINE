using System.Text.Json;

namespace XREngine.Benchmarks;

/// <summary>
/// Command-line entry point for Vulkan performance evidence evaluation.
/// </summary>
public static class VulkanPerformanceCommand
{
    public static int Run(string[] args)
    {
        try
        {
            string workspaceRoot = ResolveWorkspaceRoot();
            if (args.Contains(
                    "--self-test",
                    StringComparer.OrdinalIgnoreCase))
            {
                return VulkanPerformanceFixtureTests.Run(workspaceRoot);
            }

            string contractPath = GetOption(args, "--contract")
                ?? Path.Combine(
                    workspaceRoot,
                    "XREngine.Benchmarks",
                    "VulkanPerformance",
                    "vulkan-performance-cohorts.json");
            string runManifestPath = GetOption(args, "--run-manifest")
                ?? throw new ArgumentException(
                    "--run-manifest <path> is required.");
            string outputPath = GetOption(args, "--out")
                ?? Path.Combine(
                    Path.GetDirectoryName(runManifestPath) ?? workspaceRoot,
                    "evaluation.json");
            string? baselinePath = GetOption(args, "--baseline");
            bool acceptBaseline = args.Contains(
                "--accept-baseline",
                StringComparer.OrdinalIgnoreCase);

            contractPath = ResolvePath(workspaceRoot, contractPath);
            runManifestPath = ResolvePath(workspaceRoot, runManifestPath);
            outputPath = ResolvePath(workspaceRoot, outputPath);
            if (!string.IsNullOrWhiteSpace(baselinePath))
                baselinePath = ResolvePath(workspaceRoot, baselinePath);

            VulkanPerformanceContract contract =
                VulkanPerformanceContract.Load(contractPath);
            VulkanPerformanceRunManifest run =
                VulkanPerformanceRunManifest.Load(runManifestPath);
            VulkanPerformanceEvaluationReport? baseline =
                baselinePath is not null && File.Exists(baselinePath)
                    ? VulkanPerformanceEvaluationReport.Load(baselinePath)
                    : null;

            VulkanPerformanceEvaluator evaluator = new(
                contract,
                run,
                baseline);
            VulkanPerformanceEvaluationReport report =
                evaluator.Evaluate(acceptBaseline);

            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(
                outputPath,
                JsonSerializer.Serialize(
                    report,
                    VulkanPerformanceJson.Options));

            Console.WriteLine(
                $"Vulkan performance evaluation: {report.Status}");
            Console.WriteLine(
                $"Profile: {report.ProfileMode}; " +
                $"clean comparison: {report.CleanComparisonSuitable}; " +
                $"observer overhead: {report.ExpectedObserverOverhead}");
            foreach (VulkanPerformanceCohortReport cohort in report.Cohorts)
            {
                Console.WriteLine(
                    $"- {cohort.Id}: p95={cohort.BudgetMetricP95Median:F3} ms, " +
                    $"budget={cohort.BudgetMilliseconds:F3} ms, " +
                    $"variance={cohort.BudgetMetricRunVariancePercent:F2}%, " +
                    $"missed={cohort.MissedBudgetFrameCount}/{cohort.FrameSampleCount}");
            }
            foreach (VulkanPerformanceIssue issue in report.Issues)
            {
                Console.Error.WriteLine(
                    $"[{issue.Code}] {issue.Cohort}: {issue.Message}");
            }
            Console.WriteLine($"Report: {outputPath}");

            if (acceptBaseline)
            {
                if (string.IsNullOrWhiteSpace(baselinePath))
                    throw new ArgumentException(
                        "--accept-baseline requires --baseline <path>.");
                if (report.Issues.Count != 0)
                {
                    Console.Error.WriteLine(
                        "Baseline was not replaced because the candidate evaluation failed.");
                    return 1;
                }

                string? baselineDirectory =
                    Path.GetDirectoryName(baselinePath);
                if (!string.IsNullOrWhiteSpace(baselineDirectory))
                    Directory.CreateDirectory(baselineDirectory);
                File.Copy(outputPath, baselinePath, overwrite: true);
                Console.WriteLine($"Accepted baseline: {baselinePath}");
            }

            return report.Issues.Count == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Vulkan performance evaluation failed: {exception.Message}");
            return 2;
        }
    }

    private static string ResolveWorkspaceRoot()
        => TryFindWorkspaceRoot(Directory.GetCurrentDirectory())
        ?? TryFindWorkspaceRoot(AppContext.BaseDirectory)
        ?? throw new DirectoryNotFoundException(
            "Could not locate XRENGINE.slnx.");

    private static string? TryFindWorkspaceRoot(string startPath)
    {
        DirectoryInfo? directory = new(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "XRENGINE.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static string ResolvePath(string workspaceRoot, string path)
        => Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(workspaceRoot, path));

    private static string? GetOption(string[] args, string optionName)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(
                    optionName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }
}
