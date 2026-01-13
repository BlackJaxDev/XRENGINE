using Silk.NET.OpenXR;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

public unsafe partial class OpenXRAPI
{
    // OpenXR loader resolution
    //
    // Silk.NET (and downstream packages) P/Invoke into the OpenXR loader (openxr_loader.dll).
    // In some deployment scenarios (self-contained builds, non-standard working dirs, etc.) the
    // default DLL resolution fails. We install a DLL import resolver to provide a few known
    // fallbacks before giving up.

    private static int _nativeResolverInitialized;

    private static string? TryGetOpenXRActiveRuntime()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return TryGetOpenXRActiveRuntimeWindows();
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? TryGetOpenXRActiveRuntimeWindows()
    {
        const string keyPath = @"SOFTWARE\\Khronos\\OpenXR\\1";
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue("ActiveRuntime") as string;
    }

    private static void EnsureOpenXRLoaderResolutionConfigured()
    {
        if (Interlocked.Exchange(ref _nativeResolverInitialized, 1) != 0)
            return;

        var openXRAssembly = typeof(XR).Assembly;
        var entryAssembly = Assembly.GetEntryAssembly();
        var executingAssembly = Assembly.GetExecutingAssembly();

        NativeLibrary.SetDllImportResolver(openXRAssembly, ResolveOpenXRNative);
        NativeLibrary.SetDllImportResolver(executingAssembly, ResolveOpenXRNative);
        if (entryAssembly is not null)
            NativeLibrary.SetDllImportResolver(entryAssembly, ResolveOpenXRNative);
    }

    private static nint ResolveOpenXRNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        static bool IsOpenXRLoaderName(string name)
        {
            return name.Contains("openxr", StringComparison.OrdinalIgnoreCase)
                || name.Equals("openxr_loader", StringComparison.OrdinalIgnoreCase)
                || name.Equals("openxr_loader.dll", StringComparison.OrdinalIgnoreCase);
        }

        if (!IsOpenXRLoaderName(libraryName))
            return IntPtr.Zero;

        // Try default resolution first.
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        string[] candidateNames =
        [
            libraryName,
            "openxr_loader",
            "openxr_loader.dll",
        ];

        foreach (var name in candidateNames)
        {
            if (TryLoadFromKnownLocations(name, out handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static bool TryLoadFromKnownLocations(string libraryFileName, out IntPtr handle)
    {
        handle = IntPtr.Zero;

        var baseDir = AppContext.BaseDirectory;
        if (TryLoadFromDirectory(baseDir, libraryFileName, out handle))
            return true;

        var runtimesDir = Path.Combine(baseDir, "runtimes", "win-x64", "native");
        if (TryLoadFromDirectory(runtimesDir, libraryFileName, out handle))
            return true;

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] maybeDirs =
        [
            Path.Combine(pf86, "Steam", "steamapps", "common", "SteamVR", "bin", "win64"),
            Path.Combine(pf, "Oculus", "Support", "oculus-runtime"),
        ];

        foreach (var dir in maybeDirs)
        {
            if (TryLoadFromDirectory(dir, libraryFileName, out handle))
                return true;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryLoadFromDirectory(dir, libraryFileName, out handle))
                    return true;
            }
        }

        return false;
    }

    private static bool TryLoadFromDirectory(string directory, string libraryFileName, out IntPtr handle)
    {
        handle = IntPtr.Zero;

        if (string.IsNullOrWhiteSpace(directory))
            return false;

        string candidatePath;
        try
        {
            candidatePath = Path.Combine(directory, libraryFileName);
        }
        catch
        {
            return false;
        }

        if (!File.Exists(candidatePath))
            return false;

        return NativeLibrary.TryLoad(candidatePath, out handle);
    }
}
