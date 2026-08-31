using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace XREngine.RenderBench;

/// <summary>
/// Runs correctness cohorts in fresh processes so reference and Hi-Z histories,
/// renderer globals, and native allocations cannot leak between comparisons.
/// </summary>
public static class RenderBenchScenarioRunner
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    public static async Task<int> RunAsync(RenderBenchOptions options)
    {
        try
        {
            if (options.Scenario is not ("phase52-visibility" or "phase52-buffers" or "phase52-all"))
                throw new ArgumentException("The Phase 5.2 runner cannot execute a different scenario family.");
            if (options.ScenarioLane is not null)
                return RenderBenchScenarioLane.Run(options);

            List<string> children = [];
            List<string> failures = [];
            List<RenderBenchScenarioResult> results = [];
            List<RenderBenchVisibilityAnalysisSummary> visibility = [];
            string[] depths = options.ScenarioDepth == "both" ? ["normal", "reversed"] : [options.ScenarioDepth];
            foreach (string depth in depths)
            {
                for (int repeat = 0; repeat < options.ScenarioRepeats; repeat++)
                {
                    if (options.Scenario is "phase52-visibility" or "phase52-all")
                    {
                        foreach (string workload in GetVisibilityWorkloads(options))
                        {
                            List<RenderBenchScenarioResult> comparison = [];
                            foreach (string lane in new[] { "eligibility", "disabled", "hiz" })
                            {
                                (string path, RenderBenchScenarioResult result) = await RunChildAsync(options, depth, repeat, lane, workload).ConfigureAwait(false);
                                children.Add(path);
                                results.Add(result);
                                comparison.Add(result);
                            }
                            RenderBenchVisibilityAnalysisSummary? analysis = RenderBenchScenarioAnalysis.ValidateVisibility(comparison, failures);
                            if (analysis is not null)
                                visibility.Add(analysis);
                        }
                    }
                    if (options.Scenario is "phase52-buffers" or "phase52-all")
                    {
                        (string path, RenderBenchScenarioResult result) = await RunChildAsync(options, depth, repeat, "buffers", RenderBenchScenarioWorkloads.Default).ConfigureAwait(false);
                        children.Add(path);
                        results.Add(result);
                        if (result.Status != "passed" || !result.InFlightLifetimeProven)
                            failures.Add($"{depth}/repeat-{repeat}/buffers: {result.Failure ?? "in-flight lifetime not proven"}");
                    }
                }
            }

            RenderBenchColdRepeatAnalysisSummary coldRepeats = RenderBenchScenarioAnalysis.ValidateColdRepeats(results, failures);
            if (coldRepeats.Applicable && !coldRepeats.Passed)
                failures.Add("The visibility cold-repeat evidence did not satisfy deterministic repeat acceptance.");
            RenderBenchScenarioResult summary = new()
            {
                Scenario = options.Scenario!, Lane = "matrix", Depth = options.ScenarioDepth, Workload = options.ScenarioWorkload,
                Width = options.Width, Height = options.Height,
                Status = failures.Count == 0 ? "passed" : "failed",
                Failure = failures.Count == 0 ? null : failures[0], Failures = [.. failures],
                ChildResults = [.. children], DiagnosticReadbacks = options.Scenario != "phase52-buffers",
                VisibilityAnalysis = [.. visibility], ColdRepeatAnalysis = coldRepeats,
                InFlightLifetimeProven = results.Any(static result => result.Lane == "buffers") &&
                    results.Where(static result => result.Lane == "buffers").All(static result => result.InFlightLifetimeProven),
            };
            string summaryPath = Path.Combine(options.OutputDirectory, "scenario-result.json");
            WriteResult(summaryPath, summary);
            Console.WriteLine($"Scenario {summary.Status}: {summaryPath}");
            foreach (string failure in failures)
                Console.Error.WriteLine(failure);
            return failures.Count == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            WriteResult(Path.Combine(options.OutputDirectory, "scenario-result.json"), new RenderBenchScenarioResult
            {
                Scenario = options.Scenario!, Lane = options.ScenarioLane ?? "matrix", Depth = options.ScenarioDepth,
                Width = options.Width, Height = options.Height, Failure = exception.ToString(),
            });
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<(string Path, RenderBenchScenarioResult Result)> RunChildAsync(
        RenderBenchOptions options, string depth, int repeat, string lane, string workload)
    {
        string output = Path.Combine(options.OutputDirectory, $"{depth}-repeat-{repeat}-{workload}-{lane}");
        Directory.CreateDirectory(output);
        ProcessStartInfo start = new(Environment.ProcessPath ?? throw new InvalidOperationException("No executable process path."))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        string[] arguments =
        [
            "--output-dir", output, "--scenario", lane == "buffers" ? "phase52-buffers" : "phase52-visibility",
            "--scenario-lane", lane, "--scenario-depth", depth,
            "--scenario-workload", workload,
            "--scenario-frames", options.ScenarioFrames.ToString(CultureInfo.InvariantCulture),
            "--width", options.Width.ToString(CultureInfo.InvariantCulture),
            "--height", options.Height.ToString(CultureInfo.InvariantCulture),
            "--frame-slots", options.FrameSlots.ToString(CultureInfo.InvariantCulture),
            "--depth-format", options.DepthFormat.ToString(),
            "--fixed-step", options.FixedStepSeconds.ToString("R", CultureInfo.InvariantCulture),
            "--random-seed", options.RandomSeed.ToString(CultureInfo.InvariantCulture),
        ];
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        if (options.ScenarioTiming)
            start.ArgumentList.Add("--scenario-timing");
        Console.WriteLine($"Starting windowless cohort {depth}/repeat-{repeat}/{workload}/{lane}.");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start scenario child.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Math.Max(180, options.ScenarioFrames * 5)));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // This process was created by this coordinator and owns no desktop/editor session.
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw new TimeoutException($"Headless cohort {lane} exceeded its bounded runtime; owned child stopped.");
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "stdout.log"), await stdout.ConfigureAwait(false)).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(output, "stderr.log"), await stderr.ConfigureAwait(false)).ConfigureAwait(false);
        }
        string path = Path.Combine(output, "scenario-result.json");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Cohort {lane} exited {process.ExitCode} without evidence: {output}");
        RenderBenchScenarioResult result = JsonSerializer.Deserialize<RenderBenchScenarioResult>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Invalid child evidence: {path}");
        if (process.ExitCode != 0 && result.Status == "passed")
            result = result with { Status = "failed", Failure = $"Child exited {process.ExitCode} despite a passing payload." };
        return (path, result);
    }

    internal static void WriteResult(string path, RenderBenchScenarioResult result)
        => File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));

    private static IReadOnlyList<string> GetVisibilityWorkloads(RenderBenchOptions options)
        => options.ScenarioWorkload == "all" ? RenderBenchScenarioWorkloads.Matrix : [options.ScenarioWorkload];
}
