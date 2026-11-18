using System.Diagnostics;
using System.Text;

namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Finds and runs a command-line shader tool in a separate process with a time limit.
/// </summary>
/// <remarks>
/// Shader source from third-party projects goes through these tools. A separate process and a
/// time limit keep a compiler crash or hang out of the editor process.
/// </remarks>
public sealed class ExternalTool
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private ExternalTool(string path) => Path = path;

    /// <summary>Gets the full path of the tool executable.</summary>
    public string Path { get; }

    /// <summary>
    /// Finds a tool from an override variable, the Vulkan SDK, or <c>PATH</c>, in that order.
    /// </summary>
    /// <param name="executableName">The executable name without extension, for example <c>slangc</c>.</param>
    /// <param name="overrideVariable">The environment variable that can name the executable path.</param>
    /// <returns>The tool, or null when no executable is found.</returns>
    public static ExternalTool? Find(string executableName, string overrideVariable)
    {
        string? explicitPath = Environment.GetEnvironmentVariable(overrideVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? new ExternalTool(System.IO.Path.GetFullPath(explicitPath)) : null;

        string fileName = OperatingSystem.IsWindows() ? executableName + ".exe" : executableName;
        string? vulkanSdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (!string.IsNullOrWhiteSpace(vulkanSdk))
        {
            foreach (string folder in new[] { "Bin", "bin" })
            {
                string candidate = System.IO.Path.Combine(vulkanSdk, folder, fileName);
                if (File.Exists(candidate))
                    return new ExternalTool(candidate);
            }
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (pathVariable is null)
            return null;

        foreach (string folder in pathVariable.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = System.IO.Path.Combine(folder.Trim(), fileName);
            if (File.Exists(candidate))
                return new ExternalTool(candidate);
        }

        return null;
    }

    /// <summary>Runs the tool and waits for it to exit.</summary>
    /// <param name="arguments">The arguments. Each entry is one argument; the runtime quotes it.</param>
    /// <param name="workingDirectory">The working directory of the process.</param>
    /// <param name="cancellationToken">Cancels the wait and kills the process.</param>
    /// <exception cref="TimeoutException">The tool did not exit within the time limit. The process is killed.</exception>
    public async Task<ExternalToolResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(Path)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{Path}'.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and the kill request.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"'{System.IO.Path.GetFileName(Path)}' did not exit within {DefaultTimeout.TotalSeconds} seconds.");
        }

        return new ExternalToolResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
