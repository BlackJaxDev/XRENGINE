using System.Diagnostics;

namespace XREngine.Editor.WindowRecording;

/// <summary>
/// Finds an ffmpeg executable that can record a window. The window recorder needs the
/// <c>gfxcapture</c> filter (Windows Graphics Capture, ffmpeg 8.0 or later) to read the
/// window and the <c>libx264</c> encoder to write H.264. The locator runs each candidate
/// once with <c>-filters</c> and <c>-encoders</c> and keeps the first one that has both.
/// </summary>
internal static class EditorVideoFfmpegLocator
{
    /// <summary>The longest time to wait for one ffmpeg capability query.</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    private static readonly object Sync = new();
    private static string? _resolvedPath;
    private static string? _resolvedRawPipePath;
    private static readonly Dictionary<string, bool> NvencSupportCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tests once whether <paramref name="ffmpegPath"/> can encode H.264 using hardware NVIDIA
    /// NVENC (<c>h264_nvenc</c>) on the active GPU/driver and caches the result for the session.
    /// </summary>
    public static bool SupportsNvenc(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            return false;

        lock (Sync)
        {
            if (NvencSupportCache.TryGetValue(ffmpegPath, out bool cached))
                return cached;
        }

        bool supported = ProbeNvencHardwareEncoder(ffmpegPath);
        lock (Sync)
            NvencSupportCache[ffmpegPath] = supported;
        return supported;
    }

    private static bool ProbeNvencHardwareEncoder(string ffmpegPath)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            string[] args =
            [
                "-hide_banner",
                "-loglevel", "error",
                "-f", "lavfi",
                "-i", "color=c=black:s=256x256:d=0.05",
                "-frames:v", "1",
                "-c:v", "h264_nvenc",
                "-preset", "p4",
                "-pix_fmt", "yuv420p",
                "-f", "null",
                "-",
            ];
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            using Process? process = Process.Start(startInfo);
            if (process is null)
                return false;

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)QueryTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return false;
            }
            stderrTask.Wait(TimeSpan.FromSeconds(1));
            stdoutTask.Wait(TimeSpan.FromSeconds(1));
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns an ffmpeg executable with <c>gfxcapture</c> and <c>libx264</c>. The search
    /// order is the <c>XRE_FFMPEG_PATH</c> environment variable (a file or a folder), the
    /// portable copy that <c>Tools/Dependencies/Get-PortableFfmpeg.ps1</c> extracts into a
    /// source checkout, and then <c>ffmpeg</c> on <c>PATH</c>. A found path is kept for the
    /// rest of the session. A failed search is not kept, so a new ffmpeg is found on the
    /// next try without an editor restart.
    /// </summary>
    /// <param name="failure">The reason that no candidate was usable, or null on success.</param>
    public static string? TryResolve(out string? failure)
    {
        lock (Sync)
        {
            if (_resolvedPath is not null && File.Exists(_resolvedPath))
            {
                failure = null;
                return _resolvedPath;
            }

            List<string> rejected = [];
            foreach (string candidate in EnumerateCandidates())
            {
                if (!SupportsWindowRecording(candidate, out string? reason))
                {
                    rejected.Add($"'{candidate}': {reason}");
                    continue;
                }

                _resolvedPath = candidate;
                _resolvedRawPipePath ??= candidate;
                failure = null;
                return candidate;
            }

            failure = rejected.Count == 0
                ? $"No ffmpeg executable was found. Set {XREngineEnvironmentVariables.FfmpegPath} or put ffmpeg 8.0 or later on PATH."
                : $"No ffmpeg candidate has both gfxcapture and libx264 ({string.Join("; ", rejected)}). Use ffmpeg 8.0 or later, for example from Tools/Dependencies/Get-PortableFfmpeg.ps1.";
            return null;
        }
    }

    /// <summary>
    /// Returns an ffmpeg executable with <c>libx264</c> for raw-pixel stdin piping (used by the
    /// in-process renderer readback backend, which does not require <c>gfxcapture</c>).
    /// </summary>
    /// <param name="failure">The reason that no candidate was usable, or null on success.</param>
    public static string? TryResolveForRawPipe(out string? failure)
    {
        lock (Sync)
        {
            if (_resolvedRawPipePath is not null && File.Exists(_resolvedRawPipePath))
            {
                failure = null;
                return _resolvedRawPipePath;
            }

            if (_resolvedPath is not null && File.Exists(_resolvedPath))
            {
                _resolvedRawPipePath = _resolvedPath;
                failure = null;
                return _resolvedPath;
            }

            List<string> rejected = [];
            foreach (string candidate in EnumerateCandidates())
            {
                if (!SupportsRawPipeEncoding(candidate, out string? reason))
                {
                    rejected.Add($"'{candidate}': {reason}");
                    continue;
                }

                _resolvedRawPipePath = candidate;
                failure = null;
                return candidate;
            }

            failure = rejected.Count == 0
                ? $"No ffmpeg executable was found. Set {XREngineEnvironmentVariables.FfmpegPath} or put ffmpeg on PATH."
                : $"No ffmpeg candidate has libx264 ({string.Join("; ", rejected)}).";
            return null;
        }
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        string? configured = XREnvironment.GetValue(XREngineEnvironmentVariables.FfmpegPath);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string configuredPath = Directory.Exists(configured)
                ? Path.Combine(configured, ExecutableName)
                : configured;
            if (File.Exists(configuredPath) && seen.Add(Path.GetFullPath(configuredPath)))
                yield return Path.GetFullPath(configuredPath);
        }

        string? portableRoot = FindPortableFfmpegRoot();
        if (portableRoot is not null)
        {
            // The portable archive extracts into a versioned folder, so search below the root
            // and try the newest copy first.
            IEnumerable<FileInfo> portableCopies = new DirectoryInfo(portableRoot)
                .EnumerateFiles(ExecutableName, SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc);
            foreach (FileInfo file in portableCopies)
            {
                if (seen.Add(file.FullName))
                    yield return file.FullName;
            }
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            yield break;

        foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(directory, ExecutableName));
            }
            catch (Exception)
            {
                continue;
            }

            if (File.Exists(candidate) && seen.Add(candidate))
                yield return candidate;
        }
    }

    private static string ExecutableName
        => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>
    /// Walks up from the editor binary folder to the source checkout root (the folder with
    /// <c>XRENGINE.slnx</c>) and returns its portable ffmpeg folder when that folder exists.
    /// </summary>
    private static string? FindPortableFfmpegRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                continue;

            string portable = Path.Combine(directory.FullName, "Build", "_AgentValidation", "00000000-000000-shared", "tools", "ffmpeg");
            return Directory.Exists(portable) ? portable : null;
        }

        return null;
    }

    private static bool SupportsRawPipeEncoding(string ffmpegPath, out string? reason)
    {
        string? encoders = RunQuery(ffmpegPath, "-encoders", out reason);
        if (encoders is null)
            return false;
        if (!encoders.Contains("libx264", StringComparison.Ordinal))
        {
            reason = "the libx264 encoder is missing (it needs a GPL ffmpeg build)";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool SupportsWindowRecording(string ffmpegPath, out string? reason)
    {
        string? filters = RunQuery(ffmpegPath, "-filters", out reason);
        if (filters is null)
            return false;
        if (!filters.Contains("gfxcapture", StringComparison.Ordinal))
        {
            reason = "the gfxcapture filter is missing (it needs ffmpeg 8.0 or later on Windows)";
            return false;
        }

        string? encoders = RunQuery(ffmpegPath, "-encoders", out reason);
        if (encoders is null)
            return false;
        if (!encoders.Contains("libx264", StringComparison.Ordinal))
        {
            reason = "the libx264 encoder is missing (it needs a GPL ffmpeg build)";
            return false;
        }

        reason = null;
        return true;
    }

    private static string? RunQuery(string ffmpegPath, string query, out string? reason)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add(query);

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned no process.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(QueryTimeout))
            {
                process.Kill(entireProcessTree: true);
                reason = $"'ffmpeg {query}' did not finish in {QueryTimeout.TotalSeconds:0} s";
                return null;
            }

            reason = null;
            return output.Result + errors.Result;
        }
        catch (Exception ex)
        {
            reason = $"it cannot start ({ex.Message})";
            return null;
        }
    }
}
