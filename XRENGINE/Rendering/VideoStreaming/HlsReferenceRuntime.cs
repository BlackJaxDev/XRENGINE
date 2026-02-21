using FFmpeg.AutoGen;
using Silk.NET.OpenGL;
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

internal static class HlsReferenceRuntime
{
    private static readonly object StreamingEngineInitLock = new();
    private static bool _streamingEngineInitialized;
    private const int ExpectedAvformatMajor = 62;
    private static readonly string[] RequiredFfmpegDlls =
    [
        "avcodec-62.dll",
        "avformat-62.dll",
        "avutil-60.dll",
        "swresample-6.dll",
        "swscale-9.dll"
    ];

    public static bool EnsureStarted()
    {
        if (_streamingEngineInitialized)
            return true;

        lock (StreamingEngineInitLock)
        {
            if (_streamingEngineInitialized)
                return true;

            try
            {
                string? ffmpegPath = ResolveFfmpegPath();
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    // Fall back to output directory (DLLs are copied there by the build)
                    ffmpegPath = AppContext.BaseDirectory;
                }

                if (!ValidateFfmpegFolder(ffmpegPath))
                {
                    Debug.UIError($"Streaming FFmpeg folder validation failed for '{ffmpegPath}'.");
                    return false;
                }

                AddDirectoryToProcessPath(ffmpegPath);
                if (!ConfigureNativeDllSearchPath(ffmpegPath))
                {
                    Debug.UIError($"Failed to configure native DLL search path for '{ffmpegPath}'.");
                    return false;
                }

                // Point FFmpeg.AutoGen to the resolved path
                ffmpeg.RootPath = ffmpegPath;

                // Quick validation: can we call avformat_version()?
                uint version = ffmpeg.avformat_version();
                int major = (int)(version >> 16);
                if (major != ExpectedAvformatMajor)
                {
                    Debug.UIError($"Unexpected avformat major version {major}. Expected {ExpectedAvformatMajor}.");
                    return false;
                }

                Debug.Out($"Streaming FFmpeg validated: folder='{ffmpegPath}', avformat={major}.{(version >> 8) & 0xFF}.{version & 0xFF}");

                ffmpeg.avformat_network_init();

                _streamingEngineInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.UIError($"Failed to initialize streaming FFmpeg: {ex.Message}");
                return false;
            }
        }
    }

    public static IMediaStreamSession CreateSession(GL _)
        => new HlsMediaStreamSession();

    private static string? TryLocateAsset(string relativePath)
    {
        string? current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            string candidate = Path.Combine(current, relativePath);
            if (Directory.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(current);
            if (parent is null)
                break;

            current = parent.FullName;
        }

        return null;
    }

    private static string? ResolveFfmpegPath()
    {
        string?[] candidates =
        [
            TryLocateAsset(Path.Combine("Build", "Dependencies", "FFmpeg", "HlsReference", "win-x64")),
            AppContext.BaseDirectory
        ];

        foreach (string? candidate in candidates)
        {
            if (HasRequiredFfmpegDlls(candidate))
                return candidate;
        }

        return null;
    }

    private static bool HasRequiredFfmpegDlls(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !Directory.Exists(ffmpegPath))
            return false;

        foreach (string dllName in RequiredFfmpegDlls)
        {
            string dllPath = Path.Combine(ffmpegPath, dllName);
            if (!File.Exists(dllPath))
                return false;
        }

        return true;
    }

    private static bool ValidateFfmpegFolder(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !Directory.Exists(ffmpegPath))
        {
            Debug.UIError($"Streaming FFmpeg path is missing: '{ffmpegPath}'.");
            return false;
        }

        bool allPresent = true;
        foreach (string dllName in RequiredFfmpegDlls)
        {
            string dllPath = Path.Combine(ffmpegPath, dllName);
            if (File.Exists(dllPath))
                continue;

            allPresent = false;
            Debug.UIError($"Missing required streaming FFmpeg binary: {dllPath}");
        }

        return allPresent;
    }

    private static void AddDirectoryToProcessPath(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (string part in existing.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalized = Path.GetFullPath(part).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized, full, StringComparison.OrdinalIgnoreCase))
                return;
        }

        string updated = string.IsNullOrWhiteSpace(existing)
            ? full
            : full + Path.PathSeparator + existing;

        Environment.SetEnvironmentVariable("PATH", updated);
    }

    private static bool ConfigureNativeDllSearchPath(string? directory)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        if (SetDllDirectory(directory))
            return true;

        int error = Marshal.GetLastWin32Error();
        Debug.UIWarning($"SetDllDirectory failed for '{directory}' with Win32 error {error}.");
        return false;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetDllDirectoryW")]
    private static extern bool SetDllDirectory(string? lpPathName);
}
