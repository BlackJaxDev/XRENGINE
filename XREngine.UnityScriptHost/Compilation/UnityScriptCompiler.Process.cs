using System.Diagnostics;
using System.Text;

namespace XREngine.UnityScriptHost.Compilation;

public sealed partial class UnityScriptCompiler
{
    private static async Task<int> RunCompilerAsync(string dotnetRoot, string compiler, string responsePath,
        string output, string name, CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(output, "temp");
        Directory.CreateDirectory(temporary);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            WorkingDirectory = output,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment.Clear();
        foreach (string key in new[] { "SystemRoot", "WINDIR" })
            if (Environment.GetEnvironmentVariable(key) is string value)
                start.Environment[key] = value;
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["DOTNET_CLI_HOME"] = temporary;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        foreach (string argument in new[] { "exec", "--fx-version", Environment.Version.ToString(), compiler, "/noconfig", "@" + responsePath })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new IOException("The installed C# compiler did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        Task<string> stdout = ReadCompilerOutputAsync(process.StandardOutput, timeout.Token);
        Task<string> stderr = ReadCompilerOutputAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string log = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(output, "logs", name + ".txt"), log,
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested)
                throw new IOException("C# compilation exceeded its time limit.");
            throw;
        }
    }

    private static async Task<string> ReadCompilerOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int limit = 1024 * 1024;
        var output = new StringBuilder();
        var buffer = new char[4096];
        bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            int available = Math.Max(0, limit - output.Length);
            output.Append(buffer, 0, Math.Min(available, count));
            truncated |= count > available;
        }
        if (truncated)
            output.AppendLine("Compiler output was truncated at the diagnostic character limit.");
        return output.ToString();
    }
}
