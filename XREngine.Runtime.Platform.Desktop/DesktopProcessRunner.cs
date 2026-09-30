using System.Diagnostics;
using XREngine.Data;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Owns desktop tool processes and drains both redirected output streams while they run.</summary>
internal sealed class DesktopProcessRunner : IRuntimeProcessRunner
{
    public async Task<RuntimeProcessResult> RunAsync(RuntimeProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Executable);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = request.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
            startInfo.WorkingDirectory = request.WorkingDirectory;
        foreach (string argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start external tool '{request.Executable}'.");

        // Read concurrently, without cancelling the drains before the child exits.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            return new RuntimeProcessResult(process.ExitCode, standardOutput.Result, standardError.Result);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        }
    }
}
