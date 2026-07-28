using System.Diagnostics;
using System.Text;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Runs owned tools without a command shell and records their complete output.
/// </summary>
public sealed class SelfIterationProcessRunner
{
    public async Task<SelfIterationProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        string outputDirectory,
        string outputStem,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        string stdoutPath = Path.Combine(outputDirectory, $"{outputStem}.stdout.log");
        string stderrPath = Path.Combine(outputDirectory, $"{outputStem}.stderr.log");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> entry in environment)
                startInfo.Environment[entry.Key] = entry.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{executable}'.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var outputReadSource = new CancellationTokenSource();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task stdoutTask = ReadOutputAsync(process.StandardOutput, stdout, outputReadSource.Token);
        Task stderrTask = ReadOutputAsync(process.StandardError, stderr, outputReadSource.Token);

        bool timedOut = false;
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(
                    standardInput.AsMemory(),
                    timeoutSource.Token);
                await process.StandardInput.FlushAsync(timeoutSource.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await CancelOutputReadsAsync(process, stdoutTask, stderrTask, outputReadSource);
            throw;
        }

        await CompleteOutputReadsAsync(process, stdoutTask, stderrTask, outputReadSource);
        stopwatch.Stop();
        string capturedStdout = stdout.ToString();
        string capturedStderr = stderr.ToString();
        await File.WriteAllTextAsync(stdoutPath, capturedStdout, CancellationToken.None);
        await File.WriteAllTextAsync(stderrPath, capturedStderr, CancellationToken.None);

        return new SelfIterationProcessResult
        {
            ExitCode = timedOut ? -1 : process.ExitCode,
            TimedOut = timedOut,
            Duration = stopwatch.Elapsed,
            StandardOutputPath = stdoutPath,
            StandardErrorPath = stderrPath,
            StandardOutput = capturedStdout,
            StandardError = capturedStderr,
        };
    }

    private static async Task ReadOutputAsync(
        StreamReader reader,
        StringBuilder destination,
        CancellationToken token)
    {
        char[] buffer = new char[4096];
        try
        {
            while (true)
            {
                int characterCount = await reader.ReadAsync(buffer.AsMemory(), token);
                if (characterCount == 0)
                    return;

                destination.Append(buffer, 0, characterCount);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
        }
        catch (IOException) when (token.IsCancellationRequested)
        {
        }
    }

    private static async Task CompleteOutputReadsAsync(
        Process process,
        Task stdoutTask,
        Task stderrTask,
        CancellationTokenSource outputReadSource)
    {
        Task outputTasks = Task.WhenAll(stdoutTask, stderrTask);
        try
        {
            await outputTasks.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // Start-Process can leak the launcher's redirected handles into a detached
            // editor. The launcher is already done, so retain its complete output and
            // stop waiting for an unrelated long-lived child to close those handles.
            await CancelOutputReadsAsync(process, stdoutTask, stderrTask, outputReadSource);
        }
    }

    private static async Task CancelOutputReadsAsync(
        Process process,
        Task stdoutTask,
        Task stderrTask,
        CancellationTokenSource outputReadSource)
    {
        outputReadSource.Cancel();
        process.StandardOutput.Close();
        process.StandardError.Close();
        await Task.WhenAll(stdoutTask, stderrTask);
    }
}
