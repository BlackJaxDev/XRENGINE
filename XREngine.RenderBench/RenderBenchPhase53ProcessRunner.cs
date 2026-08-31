using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace XREngine.RenderBench;

/// <summary>
/// Runs bounded, windowless correctness children without sharing renderer globals,
/// native ownership, or warmed process state between repetitions.
/// </summary>
internal static class RenderBenchPhase53ProcessRunner
{
    public static async Task<RenderBenchPhase53ChildResult> RunChildAsync(
        RenderBenchOptions options,
        string lane,
        string depth,
        int repeat,
        string? cacheRoot = null)
    {
        string output = Path.Combine(options.OutputDirectory, $"{depth}-repeat-{repeat}-{lane}");
        Directory.CreateDirectory(output);
        ProcessStartInfo start = new(Environment.ProcessPath ??
            throw new InvalidOperationException("No executable process path."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        string[] arguments =
        [
            "--output-dir", output,
            "--scenario", options.Scenario ?? throw new ArgumentException("A Phase 5.3 scenario is required."),
            "--scenario-lane", lane,
            "--scenario-depth", depth,
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
        if (cacheRoot is not null)
        {
            start.ArgumentList.Add("--scenario-cache-root");
            start.ArgumentList.Add(cacheRoot);
        }

        Console.WriteLine($"Starting windowless {options.Scenario}/{depth}/repeat-{repeat}/{lane}.");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Phase 5.3 child.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // This exact process is owned by this invocation; no editor/process discovery is involved.
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw new TimeoutException($"The Phase 5.3 {lane} child exceeded five minutes; its owned process was stopped.");
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "stdout.log"), await stdout.ConfigureAwait(false)).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(output, "stderr.log"), await stderr.ConfigureAwait(false)).ConfigureAwait(false);
        }

        string resultPath = Path.Combine(output, "scenario-result.json");
        if (!File.Exists(resultPath))
            throw new InvalidOperationException($"The Phase 5.3 child exited {process.ExitCode} without {resultPath}.");
        return new(resultPath, process.ExitCode);
    }
}
