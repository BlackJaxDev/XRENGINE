using System.Runtime.InteropServices;

namespace XREngine.Runtime.Diagnostics.Native;

/// <summary>Owns the directory for one native log run.</summary>
public sealed class NativeDebugLogSession : IRuntimeDebugLogSession
{
    private const int MaxRunDirectoryCount = 3;
    private readonly NativeDebugLogBackend _backend;
    private readonly RuntimeDebugLogSessionOptions _options;
    private string? _sessionId;
    private string? _logRunDirectory;

    internal NativeDebugLogSession(NativeDebugLogBackend backend, RuntimeDebugLogSessionOptions options)
    {
        _backend = backend;
        _options = options;
        _sessionId = options.SessionId;
    }

    public bool HasSessionId => _sessionId is not null;

    public void SetSessionIdIfAbsent(string sessionId)
        => _sessionId ??= sessionId;

    public string EnsureRunDirectory()
    {
        if (_logRunDirectory is not null)
            return _logRunDirectory;

        string rootDirectory = _backend.GetLogsRootDirectory();
        string buildFolder = SanitizePathSegment(GetBuildIdentifier());
        string platformFolder = SanitizePathSegment(GetPlatformIdentifier());

        string runsRoot = Path.Combine(rootDirectory, buildFolder, platformFolder);
        if (!NativeDebugLogBackend.TryCreateDirectory(runsRoot))
            runsRoot = rootDirectory;

        _sessionId ??= _options.CreateSessionId();
        string runDirectory = Path.Combine(runsRoot, _sessionId);
        if (!NativeDebugLogBackend.TryCreateDirectory(runDirectory))
        {
            string fallback = Path.Combine(rootDirectory, _sessionId);
            NativeDebugLogBackend.TryCreateDirectory(fallback);
            runDirectory = fallback;
        }

        EnforceRunDirectoryLimit(runsRoot);

        _logRunDirectory = runDirectory;
        return runDirectory;
    }

    public IRuntimeDebugTextLog OpenTextLog(string fileName)
    {
        string logsDirectory = EnsureRunDirectory();
        string filePath = Path.Combine(logsDirectory, fileName);
        return NativeDebugTextLog.Open(filePath);
    }

    public void Dispose() { }

    private static string GetBuildIdentifier()
    {
        try
        {
            DirectoryInfo baseDir = new(AppContext.BaseDirectory);
            string? tfm = baseDir.Name;
            string? configuration = baseDir.Parent?.Name;

            if (!string.IsNullOrWhiteSpace(configuration) && !string.IsNullOrWhiteSpace(tfm))
                return $"{configuration}_{tfm}";

            if (!string.IsNullOrWhiteSpace(tfm))
                return tfm;

            return configuration ?? AppDomain.CurrentDomain.FriendlyName ?? "UnknownBuild";
        }
        catch
        {
            return AppDomain.CurrentDomain.FriendlyName ?? "UnknownBuild";
        }
    }

    private static string GetPlatformIdentifier()
    {
        try
        {
            string arch = RuntimeInformation.ProcessArchitecture.ToString();
            string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
                        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
                        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                        RuntimeInformation.OSDescription;

            return $"{os}_{arch}".ToLowerInvariant();
        }
        catch
        {
            return "unknown_platform";
        }
    }

    private static string SanitizePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return "unknown";

        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(segment
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static void EnforceRunDirectoryLimit(string runsRoot)
    {
        try
        {
            Directory.CreateDirectory(runsRoot);
            DirectoryInfo rootInfo = new(runsRoot);
            DirectoryInfo[] runDirectories = rootInfo.GetDirectories();

            if (runDirectories.Length <= MaxRunDirectoryCount)
                return;

            foreach (DirectoryInfo dir in runDirectories
                .OrderByDescending(d => d.CreationTimeUtc)
                .Skip(MaxRunDirectoryCount))
            {
                try
                {
                    dir.Delete(true);
                }
                catch
                {
                    // Cleanup can fail when another process owns a run.
                }
            }
        }
        catch
        {
            // Skip retention when the run directories cannot be enumerated.
        }
    }
}
