using System.Diagnostics;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Runtime.Media.FFmpeg;

/// <summary>Owns optional yt-dlp discovery and process execution for desktop media sources.</summary>
internal sealed class YtDlpStreamUrlResolver : IYouTubeStreamUrlResolver
{
    public async Task<string> ResolveAsync(string source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        cancellationToken.ThrowIfCancellationRequested();
        string executable = FindYtDlpExecutable()
            ?? throw new InvalidOperationException("YouTube URL detected, but yt-dlp was not found. Install yt-dlp and ensure it is on PATH or copied beside the app executable as 'yt-dlp.exe'.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--no-warnings");
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--skip-download");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("best[acodec!=none][vcodec!=none]");
        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add(source);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start yt-dlp process.");

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        }
        string stdout = outputTask.Result;
        string stderr = errorTask.Result;

        if (process.ExitCode != 0)
        {
            string error = string.IsNullOrWhiteSpace(stderr)
                ? $"yt-dlp exited with code {process.ExitCode}."
                : stderr.Trim();
            throw new InvalidOperationException($"yt-dlp failed to resolve YouTube URL: {error}");
        }

        string? directUrl = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(directUrl))
            throw new InvalidOperationException("yt-dlp did not return a playable URL for this YouTube source.");

        return directUrl;
    }

    private static string? FindYtDlpExecutable()
    {
        string[] pathCandidates = OperatingSystem.IsWindows()
            ? ["yt-dlp.exe", "yt-dlp"]
            : ["yt-dlp"];

        foreach (string candidate in pathCandidates)
        {
            if (IsExecutableOnPath(candidate))
                return candidate;
        }

        string baseDir = AppContext.BaseDirectory;
        string[] localCandidates = OperatingSystem.IsWindows()
            ?
            [
                Path.Combine(baseDir, "yt-dlp.exe"),
                Path.Combine(baseDir, "Plugins", "YoutubeDL", "yt-dlp.exe"),
                Path.Combine(baseDir, "YoutubeDL", "yt-dlp.exe")
            ]
            :
            [
                Path.Combine(baseDir, "yt-dlp"),
                Path.Combine(baseDir, "Plugins", "YoutubeDL", "yt-dlp")
            ];

        return localCandidates.FirstOrDefault(File.Exists);
    }

    private static bool IsExecutableOnPath(string executable)
    {
        string? path = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, executable)))
                    return true;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return false;
    }

}
