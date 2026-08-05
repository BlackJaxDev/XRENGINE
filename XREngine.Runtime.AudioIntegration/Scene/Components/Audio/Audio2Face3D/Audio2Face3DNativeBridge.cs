using System.Runtime.InteropServices;
using System.Text;

namespace XREngine.Components
{
    public static partial class Audio2Face3DNativeBridge
    {
        public const string LibraryFileName = "Audio2XBridge.Native.dll";
        public const int DefaultInputSampleRate = 16000;

        private static readonly object SyncRoot = new();

        private static NativeApi? _api;
        private static nint _libraryHandle;
        private static string _loadError = string.Empty;

        public static bool IsAvailable(out string? error)
        {
            bool loaded = EnsureLoaded(out error);
            if (!loaded)
                return false;

            bool available = _api!.IsBackendAvailable() != 0;
            error = available ? null : "Audio2XBridge.Native is present, but its backend is not enabled. Build the native shim against the NVIDIA Audio2Face-3D SDK to enable live inference.";
            return available;
        }

        public static bool TryCreateSession(Audio2Face3DNativeBridgeSessionConfig config, out Audio2Face3DNativeBridgeSession? session, out string? error)
        {
            session = null;

            if (config is null)
            {
                error = "Audio2Face native bridge configuration is required.";
                return false;
            }

            if (!EnsureLoaded(out error))
                return false;

            if (_api!.CreateSession(out nint sessionHandle) != EAudio2XBridgeResult.Success || sessionHandle == nint.Zero)
            {
                error = "Audio2XBridge.Native failed to create a session.";
                return false;
            }

            var nativeConfig = new NativeSessionConfig
            {
                InputSampleRate = Math.Max(1, config.InputSampleRate),
                EnableEmotion = config.EnableEmotion,
                FaceModelPath = NormalizeOptionalPath(config.FaceModelPath),
                EmotionModelPath = NormalizeOptionalPath(config.EmotionModelPath),
            };

            EAudio2XBridgeResult configureResult = _api.ConfigureSession(sessionHandle, ref nativeConfig);
            if (configureResult != EAudio2XBridgeResult.Success)
            {
                error = GetLastError(sessionHandle, fallback: $"Audio2XBridge.Native failed to configure the session ({configureResult}).");
                _api.DestroySession(sessionHandle);
                return false;
            }

            session = new Audio2Face3DNativeBridgeSession(sessionHandle, _api);
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
                    if (!NativeLibrary.TryLoad(LibraryFileName, out _libraryHandle) && !NativeLibrary.TryLoad("Audio2XBridge.Native", out _libraryHandle))
                    {
                        _loadError = $"{LibraryFileName} was not found. Build Build/Native/Audio2XBridge/Audio2XBridge.vcxproj and copy the resulting DLL beside the editor executable to enable live Audio2Face inference.";
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

        private static string NormalizeOptionalPath(string path)
            => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

        internal static string GetLastError(nint sessionHandle, string fallback)
        {
            if (_api is null)
                return fallback;

            int requiredBytes = 0;
            EAudio2XBridgeResult result = _api.GetLastError(sessionHandle, null, 0, out requiredBytes);
            if (requiredBytes <= 1 || (result != EAudio2XBridgeResult.Success && result != EAudio2XBridgeResult.NoData))
                return fallback;

            byte[] buffer = new byte[requiredBytes];
            result = _api.GetLastError(sessionHandle, buffer, buffer.Length, out requiredBytes);
            if (result != EAudio2XBridgeResult.Success && result != EAudio2XBridgeResult.NoData)
                return fallback;

            int terminatorIndex = Array.IndexOf(buffer, (byte)0);
            int byteCount = terminatorIndex >= 0 ? terminatorIndex : buffer.Length;
            string message = Encoding.UTF8.GetString(buffer, 0, byteCount).Trim();
            return string.IsNullOrWhiteSpace(message) ? fallback : message;
        }
    }
}