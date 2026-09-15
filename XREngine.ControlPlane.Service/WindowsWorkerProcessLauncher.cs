using System.Diagnostics;
using System.Text.Json;

namespace XREngine.ControlPlane.Service;

/// <summary>
/// Launches and manages worker processes on Windows, handling their configuration, environment, and resource limits.
/// </summary>
/// <param name="options">The local service options containing configuration and user information.</param>
/// <param name="agentStore">The local host agent store for recording and managing worker executions.</param>
internal sealed class WindowsWorkerProcessLauncher(LocalServiceOptions options, LocalHostAgentStore agentStore) : IWorkerProcessLauncher
{
    /// <summary>
    /// Starts the specified worker execution by launching the associated process, setting up its environment, and recording its initial state.
    /// </summary>
    /// <param name="execution">The worker execution to start.</param>
    public void Start(WorkerExecution execution)
    {
        string configurationPath = Path.Combine(execution.Directory, "worker.json");
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(execution.Launch, ServiceJson.Options));
        var start = new ProcessStartInfo(options.ServerExecutable)
        {
            WorkingDirectory = execution.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // A managed launch is hermetic with respect to legacy networking/world overrides.
        foreach (string key in start.Environment.Keys.ToArray())
            if (key.StartsWith("XRE_WORLD_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("XRE_SESSION_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("XRE_REALTIME_JOIN_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("XRE_MANAGED_", StringComparison.OrdinalIgnoreCase)
                || key == "XRE_NET_MODE")
                start.Environment.Remove(key);
        foreach (LocalApiUser user in options.Users)
            start.Environment.Remove(user.TokenEnvironmentVariable);
        start.Environment["XRE_MANAGED_WORKER_CONFIG_FILE"] = configurationPath;
        execution.Job = new WindowsWorkerJob(execution.Launch.ResourceLimits.CpuPercentLimit,
            execution.Launch.ResourceLimits.MemoryBytesLimit,
            $"XREngine.Managed.{execution.Launch.InstanceId}.{execution.Launch.Generation:N}", !options.PreserveWorkersOnAgentCrash);
        execution.Process = WindowsContainedProcess.Start(start, execution.Job, out StreamReader stdout, out StreamReader stderr);
        execution.StandardOutputTask = DrainLogAsync(stdout, Path.Combine(execution.Directory, "stdout.log"));
        execution.StandardErrorTask = DrainLogAsync(stderr, Path.Combine(execution.Directory, "stderr.log"));
        File.WriteAllText(Path.Combine(execution.Directory, "session.json"), JsonSerializer.Serialize(new
        {
            processId = execution.Process.Id,
            processStartTimeUtc = execution.Process.StartTime.ToUniversalTime(),
            instanceId = execution.Launch.InstanceId,
            generation = execution.Launch.Generation,
            executable = options.ServerExecutable,
            createdUtc = execution.CreatedUtc,
        }, ServiceJson.Options));
        agentStore.RecordStarted(execution);
    }

    /// <summary>
    /// Drains the log from the specified stream reader to the specified file path asynchronously.
    /// </summary>
    /// <param name="reader">The stream reader to read log data from.</param>
    /// <param name="path">The file path to write the log data to.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private static async Task DrainLogAsync(StreamReader reader, string path)
    {
        using var ownedReader = reader;
        // Continue consuming after the on-disk budget is reached so a noisy worker cannot block on stdout.
        await using var output = new StreamWriter(path, append: false);
        long written = 0;
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            if (written >= 16 * 1024 * 1024)
                continue;
            await output.WriteAsync(buffer.AsMemory(0, count));
            written += count;
            await output.FlushAsync();
        }
    }
}
