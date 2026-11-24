using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace XREngine.Editor.Tools;

/// <summary>
/// Runs one dependency script or build process asynchronously with streaming console output
/// and safe process-tree cancellation without blocking the editor render thread.
/// </summary>
internal sealed class DependencyTaskRunner
{
    private const int MaxLogLines = 3000;

    private readonly object _sync = new();
    private readonly List<DependencyTaskLogLine> _lines = new(256);
    private Process? _activeProcess;
    private Stopwatch? _stopwatch;
    private bool _isRunning;
    private int? _lastExitCode;
    private string _activeTaskName = string.Empty;
    private Action? _onCompleted;

    public bool IsRunning
    {
        get { lock (_sync) return _isRunning; }
    }

    public int? LastExitCode
    {
        get { lock (_sync) return _lastExitCode; }
    }

    public string ActiveTaskName
    {
        get { lock (_sync) return _activeTaskName; }
    }

    public double ElapsedSeconds
    {
        get { lock (_sync) return _stopwatch?.Elapsed.TotalSeconds ?? 0.0; }
    }

    public bool TryStart(
        string taskDisplayName,
        string workingDirectory,
        string fileName,
        string arguments,
        string displayCommand,
        Action? onCompleted = null)
    {
        lock (_sync)
        {
            if (_isRunning)
                return false;

            _isRunning = true;
            _lastExitCode = null;
            _activeTaskName = taskDisplayName;
            _onCompleted = onCompleted;
            _stopwatch = Stopwatch.StartNew();
            AppendLineLocked(new DependencyTaskLogLine($"> {displayCommand}", IsError: false, IsCommand: true));
        }

        Task.Run(() => RunProcessCore(workingDirectory, fileName, arguments));
        return true;
    }

    public void Cancel()
    {
        Process? toKill;
        lock (_sync)
            toKill = _activeProcess;

        if (toKill is null)
            return;

        try
        {
            AppendLine(new DependencyTaskLogLine("Cancelling active task...", IsError: true, IsCommand: false));
            toKill.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppendLine(new DependencyTaskLogLine($"Cancel error: {ex.Message}", IsError: true, IsCommand: false));
        }
    }

    public void ClearLog()
    {
        lock (_sync)
            _lines.Clear();
    }

    public void CopyLogSnapshot(List<DependencyTaskLogLine> destination)
    {
        destination.Clear();
        lock (_sync)
            destination.AddRange(_lines);
    }

    private void RunProcessCore(string workingDirectory, string fileName, string arguments)
    {
        int exitCode = -1;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    AppendLine(new DependencyTaskLogLine(e.Data, IsError: false, IsCommand: false));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    AppendLine(new DependencyTaskLogLine(e.Data, IsError: true, IsCommand: false));
            };

            lock (_sync)
                _activeProcess = process;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            AppendLine(new DependencyTaskLogLine($"Failed to start process: {ex.Message}", IsError: true, IsCommand: false));
        }
        finally
        {
            Action? completionCallback;
            lock (_sync)
            {
                _activeProcess = null;
                _stopwatch?.Stop();
                _lastExitCode = exitCode;
                _isRunning = false;
                completionCallback = _onCompleted;
                _onCompleted = null;
                AppendLineLocked(new DependencyTaskLogLine(
                    exitCode == 0
                        ? $"Completed successfully (exit code 0) in {(_stopwatch?.Elapsed.TotalSeconds ?? 0.0):F1} s."
                        : $"Exited with code {exitCode} after {(_stopwatch?.Elapsed.TotalSeconds ?? 0.0):F1} s.",
                    IsError: exitCode != 0,
                    IsCommand: true));
            }

            completionCallback?.Invoke();
        }
    }

    private void AppendLine(DependencyTaskLogLine line)
    {
        lock (_sync)
            AppendLineLocked(line);
    }

    private void AppendLineLocked(DependencyTaskLogLine line)
    {
        if (_lines.Count >= MaxLogLines)
            _lines.RemoveAt(0);
        _lines.Add(line);
    }
}
