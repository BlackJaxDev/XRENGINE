using System.Runtime.InteropServices;
using System.Text;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Dynamically loads <c>MotionBricksBridge.Native.dll</c> and exposes session creation
/// and explicit diagnostic reporting for NVIDIA TensorRT SOMA inference.
/// </summary>
public static partial class MotionBricksTensorRtBridge
{
    public const string LibraryFileName = "MotionBricksBridge.Native.dll";

    private static readonly object SyncRoot = new();

    private static NativeApi? _api;
    private static nint _libraryHandle;
    private static string _loadError = string.Empty;

    /// <summary>
    /// Returns true when <c>MotionBricksBridge.Native.dll</c> is loaded and compiled against
    /// the NVIDIA TensorRT and CUDA SDKs.
    /// </summary>
    public static bool IsAvailable(out string? error)
    {
        if (!EnsureLoaded(out error))
            return false;

        bool available = _api!.IsBackendAvailable() != 0;
        error = available
            ? null
            : "MotionBricksBridge.Native.dll is loaded, but its TensorRT backend is disabled. "
              + "Stage TensorRT with Tools/Dependencies/Get-TensorRT.ps1, install CUDA, and rebuild "
              + "Build/Native/MotionBricksBridge/MotionBricksBridge.vcxproj.";
        return available;
    }

    /// <summary>
    /// Returns the loaded TensorRT runtime library version integer, or 0 when unavailable.
    /// </summary>
    public static int GetTensorRtVersion()
    {
        if (!EnsureLoaded(out _))
            return 0;

        return _api!.GetTensorRtVersion();
    }

    /// <summary>
    /// Creates and configures a TensorRT SOMA session. Call
    /// <see cref="MotionBricksTensorRtSession.TryBuildOrLoadEngine"/> before inference.
    /// </summary>
    public static bool TryCreateSession(
        MotionBricksSessionConfig config,
        out MotionBricksTensorRtSession? session,
        out string? error)
    {
        session = null;
        if (config is null)
        {
            error = "MotionBricks session configuration is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.OnnxModelPath))
        {
            error = "MotionBricks session OnnxModelPath is required. Run Tools/Dependencies/Get-MotionBricks.ps1 to stage SOMA weights.";
            return false;
        }

        if (!EnsureLoaded(out error))
            return false;

        if (_api!.CreateSession(out nint sessionHandle) != EMotionBricksBridgeResult.Success || sessionHandle == nint.Zero)
        {
            error = $"{LibraryFileName} failed to allocate a native session.";
            return false;
        }

        nint onnxPathPtr = nint.Zero;
        nint cacheDirPtr = nint.Zero;
        try
        {
            onnxPathPtr = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(config.OnnxModelPath));
            cacheDirPtr = string.IsNullOrWhiteSpace(config.PlanCacheDirectory)
                ? nint.Zero
                : Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(config.PlanCacheDirectory));

            var nativeConfig = new NativeSessionConfig
            {
                OnnxModelPath = onnxPathPtr,
                PlanCacheDir = cacheDirPtr,
                EnableFp16 = config.EnableFp16 ? 1 : 0,
                EnableTf32 = config.EnableTf32 ? 1 : 0,
                BuilderOptimizationLevel = config.BuilderOptimizationLevel,
                ForceRebuild = config.ForceRebuild ? 1 : 0,
                WorkspaceSizeBytes = config.WorkspaceSizeBytes,
            };

            EMotionBricksBridgeResult result = _api.ConfigureSession(sessionHandle, ref nativeConfig);
            if (result != EMotionBricksBridgeResult.Success)
            {
                error = GetLastError(sessionHandle, $"{LibraryFileName} failed to configure the session ({result}).");
                _api.DestroySession(sessionHandle);
                return false;
            }
        }
        finally
        {
            if (onnxPathPtr != nint.Zero)
                Marshal.FreeCoTaskMem(onnxPathPtr);
            if (cacheDirPtr != nint.Zero)
                Marshal.FreeCoTaskMem(cacheDirPtr);
        }

        session = new MotionBricksTensorRtSession(sessionHandle, _api);
        error = null;
        return true;
    }

    private static bool EnsureLoaded(out string? error)
    {
        lock (SyncRoot)
        {
            if (_api is not null)
            {
                error = null;
                return true;
            }

            if (_libraryHandle == nint.Zero)
            {
                if (!TryLoadNativeBridge(out _libraryHandle))
                {
                    _loadError = $"{LibraryFileName} was not found or failed to load TensorRT/CUDA dependencies. "
                        + "Stage TensorRT with Tools/Dependencies/Get-TensorRT.ps1, build "
                        + "Build/Native/MotionBricksBridge/MotionBricksBridge.vcxproj, and ensure the native DLLs "
                        + "sit beside the executable.";
                    error = _loadError;
                    return false;
                }
            }

            if (!NativeApi.TryCreate(_libraryHandle, out _api, out string? apiError))
            {
                _loadError = string.IsNullOrWhiteSpace(apiError)
                    ? $"{LibraryFileName} is missing one or more required exports."
                    : apiError;
                error = _loadError;
                return false;
            }

            error = null;
            return true;
        }
    }

    private static bool TryLoadNativeBridge(out nint handle)
    {
        string? envPath = Environment.GetEnvironmentVariable("MOTIONBRICKS_BRIDGE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && TryLoadCandidateFile(envPath, out handle))
            return true;

        string baseDir = AppContext.BaseDirectory;
        if (TryLoadCandidateFile(Path.Combine(baseDir, LibraryFileName), out handle))
            return true;
        if (TryLoadCandidateFile(Path.Combine(baseDir, "runtimes", "win-x64", "native", LibraryFileName), out handle))
            return true;

        // Walk upward from AppContext.BaseDirectory to find the local repository native build output.
        DirectoryInfo? current = new DirectoryInfo(baseDir);
        for (int depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            string sharedTools = Path.Combine(
                current.FullName,
                "Build",
                "_AgentValidation",
                "00000000-000000-shared",
                "tools");
            if (TryLoadCandidateFile(Path.Combine(sharedTools, "Release", "MotionBricksBridge", LibraryFileName), out handle))
                return true;
            if (TryLoadCandidateFile(Path.Combine(sharedTools, "Debug", "MotionBricksBridge", LibraryFileName), out handle))
                return true;
        }

        return NativeLibrary.TryLoad(LibraryFileName, out handle)
            || NativeLibrary.TryLoad("MotionBricksBridge.Native", out handle);
    }

    private static bool TryLoadCandidateFile(string fullPath, out nint handle)
    {
        handle = nint.Zero;
        if (!File.Exists(fullPath))
            return false;

        string? directory = Path.GetDirectoryName(fullPath);
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(directory))
        {
            // Preload co-located CUDA/TensorRT runtime DLLs before loading the bridge so Windows
            // resolves dependent imports without modifying the global process PATH.
            PreloadOptionalDependency(directory, "cudart64_12.dll");
            PreloadOptionalDependency(directory, "cublasLt64_12.dll");
            PreloadOptionalDependency(directory, "cublas64_12.dll");
            PreloadOptionalDependency(directory, "nvrtc-builtins64_129.dll");
            PreloadOptionalDependency(directory, "nvrtc64_120_0.dll");
            PreloadOptionalDependency(directory, "nvinfer_10.dll");
            PreloadOptionalDependency(directory, "nvinfer_builder_resource_10.dll");
            PreloadOptionalDependency(directory, "nvonnxparser_10.dll");
        }

        return NativeLibrary.TryLoad(fullPath, out handle);
    }

    private static void PreloadOptionalDependency(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (File.Exists(path))
            NativeLibrary.TryLoad(path, out _);
    }

    internal static string GetLastError(nint sessionHandle, string fallback)
    {
        if (_api is null)
            return fallback;

        EMotionBricksBridgeResult probe = _api.GetLastError(sessionHandle, null, 0, out int requiredBytes);
        if (requiredBytes <= 1
            || (probe != EMotionBricksBridgeResult.Success && probe != EMotionBricksBridgeResult.NoData))
        {
            return fallback;
        }

        byte[] buffer = new byte[requiredBytes];
        EMotionBricksBridgeResult read = _api.GetLastError(sessionHandle, buffer, buffer.Length, out _);
        if (read != EMotionBricksBridgeResult.Success && read != EMotionBricksBridgeResult.NoData)
            return fallback;

        int nullIndex = Array.IndexOf(buffer, (byte)0);
        int byteCount = nullIndex >= 0 ? nullIndex : buffer.Length;
        string message = Encoding.UTF8.GetString(buffer, 0, byteCount).Trim();
        return string.IsNullOrWhiteSpace(message) ? fallback : message;
    }
}
