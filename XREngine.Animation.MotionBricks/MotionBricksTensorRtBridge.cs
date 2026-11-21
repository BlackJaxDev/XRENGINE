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
                if (!NativeLibrary.TryLoad(LibraryFileName, out _libraryHandle)
                    && !NativeLibrary.TryLoad("MotionBricksBridge.Native", out _libraryHandle))
                {
                    _loadError = $"{LibraryFileName} was not found. Build Build/Native/MotionBricksBridge/MotionBricksBridge.vcxproj "
                        + "and place the resulting DLL beside the executable to enable TensorRT MotionBricks inference.";
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
