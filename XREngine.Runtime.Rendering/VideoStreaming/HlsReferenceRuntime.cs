using FFmpeg.AutoGen;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

public static class HlsReferenceRuntime
{
    private sealed record FfmpegRuntimeProfile(int AvformatMajor, string[] RequiredDlls);

    private static readonly object StreamingEngineInitLock = new();
    private static bool _streamingEngineInitialized;
    private static string? _resolvedFfmpegPath;
    private static readonly FfmpegRuntimeProfile[] SupportedProfiles =
    [
        new(62,
        [
            "avcodec-62.dll",
            "avdevice-62.dll",
            "avfilter-11.dll",
            "avformat-62.dll",
            "avutil-60.dll",
            "swresample-6.dll",
            "swscale-9.dll"
        ]),
        new(61,
        [
            "avcodec-61.dll",
            "avdevice-61.dll",
            "avfilter-10.dll",
            "avformat-61.dll",
            "avutil-59.dll",
            "swresample-5.dll",
            "swscale-8.dll"
        ])
    ];

    /// <summary>
    /// Mapping from FFmpeg.AutoGen library logical names to the actual DLL names
    /// for each supported profile.  Used by the <see cref="NativeLibrary"/>
    /// resolver fallback when <c>SetDllDirectory</c> is unavailable.
    /// </summary>
    private static readonly Dictionary<string, string[]> LibraryNameVariants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["avcodec"]    = ["avcodec-62", "avcodec-61"],
        ["avdevice"]   = ["avdevice-62", "avdevice-61"],
        ["avfilter"]   = ["avfilter-11", "avfilter-10"],
        ["avformat"]   = ["avformat-62", "avformat-61"],
        ["avutil"]     = ["avutil-60", "avutil-59"],
        ["swresample"] = ["swresample-6", "swresample-5"],
        ["swscale"]    = ["swscale-9", "swscale-8"],
    };

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
                // --- Step 1: Resolve the FFmpeg folder ---
                LogInfo("Streaming FFmpeg init: resolving path...");
                (string? ffmpegPath, FfmpegRuntimeProfile? profile) = ResolveFfmpegPath();
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    ffmpegPath = AppContext.BaseDirectory;
                    LogInfo($"Streaming FFmpeg: no explicit folder found, falling back to BaseDirectory='{ffmpegPath}'");
                }
                else
                {
                    LogInfo($"Streaming FFmpeg: resolved folder='{ffmpegPath}', profile=avformat-{profile?.AvformatMajor.ToString() ?? "?"}");
                }

                // --- Step 2: Validate the folder ---
                if (!ValidateFfmpegFolder(ffmpegPath, profile))
                {
                    LogError($"Streaming FFmpeg folder validation failed for '{ffmpegPath}'.");
                    return false;
                }
                _resolvedFfmpegPath = Path.GetFullPath(ffmpegPath);

                // --- Step 3: Make the DLLs discoverable ---
                AddDirectoryToProcessPath(ffmpegPath);
                LogInfo("Streaming FFmpeg: added to PATH.");

                if (!ConfigureNativeDllSearchPath(ffmpegPath))
                {
                    LogInfo("Streaming FFmpeg: SetDllDirectory unavailable; will rely on PATH + NativeLibrary resolver.");
                }
                else
                {
                    LogInfo("Streaming FFmpeg: SetDllDirectory succeeded.");
                }

                // Register a NativeLibrary resolver so .NET can find the
                // versioned FFmpeg DLLs even if SetDllDirectory failed.
                RegisterNativeLibraryResolver();

                // --- Step 4: Set FFmpeg.AutoGen root path and initialize bindings ---
                LogInfo($"Streaming FFmpeg: setting RootPath='{ffmpegPath}'");
                ffmpeg.RootPath = ffmpegPath;

                // Use a deterministic resolver that probes the resolved FFmpeg
                // folder first. This avoids platform/runtime quirks inside the
                // default resolver path and ensures delegates are bound from the
                // exact runtime profile we validated.
                DynamicallyLoadedBindings.FunctionResolver = new PathFirstFunctionResolver(ffmpegPath);
                DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;

                // FFmpeg.AutoGen v8 wires all function delegates to throw
                // NotSupportedException until DynamicallyLoadedBindings.Initialize()
                // is called.  This resolves every delegate against the native DLLs.
                LogInfo("Streaming FFmpeg: calling DynamicallyLoadedBindings.Initialize()...");
                try
                {
                    DynamicallyLoadedBindings.Initialize();
                }
                catch (Exception initEx)
                {
                    LogError($"Streaming FFmpeg: DynamicallyLoadedBindings.Initialize() failed: {initEx.GetType().Name}: {initEx.Message}");
                    LogError($"  Stack: {initEx.StackTrace}");
                    if (initEx.InnerException is not null)
                        LogError($"  Inner: {initEx.InnerException.GetType().Name}: {initEx.InnerException.Message}");
                    LogDirectoryContents(_resolvedFfmpegPath);
                    return false;
                }

                // --- Step 5: Validate by calling avformat_version() ---
                LogInfo("Streaming FFmpeg: calling avformat_version()...");
                uint version;
                try
                {
                    version = ffmpeg.avformat_version();
                }
                catch (TypeInitializationException tie)
                {
                    // The static constructor of the 'ffmpeg' type failed.
                    // Unwrap to the real exception for a useful message.
                    Exception inner = tie.InnerException ?? tie;
                    LogError($"Streaming FFmpeg: ffmpeg type initializer failed: {inner.GetType().Name}: {inner.Message}");
                    LogError($"  Inner stack: {inner.StackTrace}");
                    return false;
                }
                catch (DllNotFoundException dnf)
                {
                    LogError($"Streaming FFmpeg: native DLL not found: {dnf.Message}");
                    LogError($"  Resolved folder was: '{_resolvedFfmpegPath}'");
                    LogDirectoryContents(_resolvedFfmpegPath);
                    return false;
                }
                catch (BadImageFormatException bif)
                {
                    LogError($"Streaming FFmpeg: architecture mismatch (x86 vs x64?): {bif.Message}");
                    return false;
                }

                int major = (int)(version >> 16);
                if (!IsSupportedAvformatMajor(major))
                {
                    LogError($"Unexpected avformat major version {major}. Supported majors: {string.Join(", ", SupportedProfiles.Select(p => p.AvformatMajor))}.");
                    return false;
                }

                LogInfo($"Streaming FFmpeg validated: folder='{ffmpegPath}', avformat={major}.{(version >> 8) & 0xFF}.{version & 0xFF}");

                // --- Step 6: Initialize network ---
                ffmpeg.avformat_network_init();
                LogInfo("Streaming FFmpeg: avformat_network_init() done.");

                _streamingEngineInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialize streaming FFmpeg: {ex.GetType().Name}: {ex.Message}");
                LogError($"  Stack: {ex.StackTrace}");
                if (ex.InnerException is not null)
                    LogError($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                return false;
            }
        }
    }

    public static IMediaStreamSession CreateSession()
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

    private static (string? Path, FfmpegRuntimeProfile? Profile) ResolveFfmpegPath()
    {
        string?[] candidates =
        [
            TryLocateAsset(Path.Combine("Build", "Dependencies", "FFmpeg", "HlsReference", "win-x64")),
            TryLocateAsset(Path.Combine("Build", "Submodules", "Flyleaf", "FFmpeg")),
            AppContext.BaseDirectory
        ];

        foreach (string? candidate in candidates)
        {
            FfmpegRuntimeProfile? profile = DetectProfile(candidate);
            if (profile is not null)
                return (candidate, profile);
        }

        return (null, null);
    }

    private static FfmpegRuntimeProfile? DetectProfile(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !Directory.Exists(ffmpegPath))
            return null;

        foreach (FfmpegRuntimeProfile profile in SupportedProfiles)
        {
            bool allPresent = true;
            foreach (string dllName in profile.RequiredDlls)
            {
                string dllPath = Path.Combine(ffmpegPath, dllName);
                if (!File.Exists(dllPath))
                {
                    allPresent = false;
                    break;
                }
            }

            if (allPresent)
                return profile;
        }

        return null;
    }

    private static bool ValidateFfmpegFolder(string? ffmpegPath, FfmpegRuntimeProfile? expectedProfile)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !Directory.Exists(ffmpegPath))
        {
            LogError($"Streaming FFmpeg path is missing: '{ffmpegPath}'.");
            return false;
        }

        FfmpegRuntimeProfile? profile = expectedProfile ?? DetectProfile(ffmpegPath);
        if (profile is null)
        {
            LogError($"Streaming FFmpeg path '{ffmpegPath}' did not match any supported runtime profile.");
            foreach (FfmpegRuntimeProfile supported in SupportedProfiles)
                LogError($"Supported profile avformat-{supported.AvformatMajor}: {string.Join(", ", supported.RequiredDlls)}");
            return false;
        }

        bool allPresent = true;
        foreach (string dllName in profile.RequiredDlls)
        {
            string dllPath = Path.Combine(ffmpegPath, dllName);
            if (File.Exists(dllPath))
                continue;

            allPresent = false;
            LogError($"Missing required streaming FFmpeg binary: {dllPath}");
        }

        return allPresent;
    }

    private static bool IsSupportedAvformatMajor(int major)
        => SupportedProfiles.Any(profile => profile.AvformatMajor == major);

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

        try
        {
            if (SetDllDirectory(directory))
                return true;

            int error = Marshal.GetLastWin32Error();
            LogWarning($"SetDllDirectory failed for '{directory}' with Win32 error {error}.");
            return false;
        }
        catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            LogWarning($"SetDllDirectory not supported in this runtime for '{directory}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Registers a <see cref="NativeLibrary"/> import resolver for the
    /// FFmpeg.AutoGen assembly.  This covers the case where
    /// <c>SetDllDirectory</c> is unavailable (e.g. .NET 10 single-file
    /// publish) by probing <see cref="_resolvedFfmpegPath"/> for the
    /// versioned DLL names that FFmpeg.AutoGen expects.
    /// </summary>
    private static bool _nativeResolverRegistered;
    private static void RegisterNativeLibraryResolver()
    {
        if (_nativeResolverRegistered)
            return;

        if (string.IsNullOrWhiteSpace(_resolvedFfmpegPath))
            return;

        Assembly? ffmpegAssembly = typeof(ffmpeg).Assembly;
        if (ffmpegAssembly is null)
            return;

        try
        {
            NativeLibrary.SetDllImportResolver(ffmpegAssembly, FfmpegDllImportResolver);
            _nativeResolverRegistered = true;
            LogInfo("Streaming FFmpeg: NativeLibrary resolver registered.");
        }
        catch (InvalidOperationException)
        {
            // Resolver already registered (another code path beat us to it).
            _nativeResolverRegistered = true;
        }
        catch (Exception ex)
        {
            LogWarning($"Streaming FFmpeg: failed to register NativeLibrary resolver: {ex.Message}");
        }
    }

    private static IntPtr FfmpegDllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Try default resolution first.
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out IntPtr handle))
            return handle;

        string baseName = Path.GetFileNameWithoutExtension(libraryName);

        // Check if the requested library is one of our known FFmpeg libraries.
        if (!LibraryNameVariants.TryGetValue(baseName, out string[]? variants))
            return IntPtr.Zero;

        string root = _resolvedFfmpegPath ?? AppContext.BaseDirectory;
        foreach (string variant in variants)
        {
            string candidate = Path.Combine(root, variant + ".dll");
            if (NativeLibrary.TryLoad(candidate, out handle))
            {
                LogInfo($"Streaming FFmpeg: resolved '{libraryName}' -> '{candidate}'");
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static void LogDirectoryContents(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            LogError($"  Directory does not exist: '{directory}'");
            return;
        }

        try
        {
            string[] files = Directory.GetFiles(directory, "*.dll");
            LogError($"  Directory '{directory}' contains {files.Length} DLL(s):");
            foreach (string file in files.Take(20))
                LogError($"    {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            LogError($"  Could not enumerate directory '{directory}': {ex.Message}");
        }
    }

    private sealed class PathFirstFunctionResolver(string rootPath) : IFunctionResolver
    {
        private readonly string _rootPath = Path.GetFullPath(rootPath);
        private readonly IFunctionResolver _fallbackResolver = FunctionResolverFactory.Create();
        private readonly ConcurrentDictionary<string, IntPtr> _libraryHandles = new(StringComparer.OrdinalIgnoreCase);

        T IFunctionResolver.GetFunctionDelegate<T>(string libraryName, string functionName, bool throwOnError)
        {
            IntPtr libraryHandle = _libraryHandles.GetOrAdd(libraryName, static (name, self) => self.LoadLibrary(name), this);
            if (libraryHandle != IntPtr.Zero && NativeLibrary.TryGetExport(libraryHandle, functionName, out IntPtr functionPointer))
                return (T)(object)Marshal.GetDelegateForFunctionPointer(functionPointer, typeof(T));

            return _fallbackResolver.GetFunctionDelegate<T>(libraryName, functionName, throwOnError);
        }

        private IntPtr LoadLibrary(string libraryName)
        {
            foreach (string candidate in EnumerateCandidates(libraryName))
            {
                if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                {
                    LogInfo($"Streaming FFmpeg resolver: loaded '{libraryName}' from '{candidate}'");
                    return handle;
                }
            }

            LogWarning($"Streaming FFmpeg resolver: unable to load library '{libraryName}' from '{_rootPath}'");
            return IntPtr.Zero;
        }

        private IEnumerable<string> EnumerateCandidates(string libraryName)
        {
            string fileName = Path.GetFileName(libraryName);
            bool hasExtension = Path.HasExtension(fileName);

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                yield return Path.Combine(_rootPath, fileName);
                if (!hasExtension)
                    yield return Path.Combine(_rootPath, fileName + ".dll");
            }

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            if (LibraryNameVariants.TryGetValue(baseName, out string[]? variants))
            {
                foreach (string variant in variants)
                    yield return Path.Combine(_rootPath, variant + ".dll");
            }
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetDllDirectoryW")]
    private static extern bool SetDllDirectory(string? lpPathName);

    private static void LogInfo(string message)
        => Trace.WriteLine(message);

    private static void LogWarning(string message)
        => Trace.TraceWarning(message);

    private static void LogError(string message)
        => Trace.TraceError(message);
}

