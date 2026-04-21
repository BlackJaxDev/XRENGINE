using System.Runtime.InteropServices;
using System.Text;

namespace XREngine.Components
{
    public enum EAudio2XBridgeResult
    {
        Success = 0,
        NoData = 1,
        InvalidArgument = 2,
        BackendUnavailable = 3,
        InternalError = 4,
    }

    public enum EAudio2Face3DNativePollResult
    {
        NoData,
        Success,
        Error,
    }

    public sealed class Audio2Face3DNativeBridgeSessionConfig
    {
        public int InputSampleRate { get; init; } = Audio2Face3DNativeBridge.DefaultInputSampleRate;
        public bool EnableEmotion { get; init; } = true;
        public string FaceModelPath { get; init; } = string.Empty;
        public string EmotionModelPath { get; init; } = string.Empty;
    }

    public static class Audio2Face3DNativeBridgeAudioConverter
    {
        public static short[] ConvertToPcm16Mono(byte[] audioData, int bitsPerSample, int sourceSampleRate, int targetSampleRate)
        {
            if (audioData is null)
                throw new ArgumentNullException(nameof(audioData));

            if (sourceSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));

            if (targetSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetSampleRate));

            int bytesPerSample = bitsPerSample switch
            {
                8 => 1,
                16 => 2,
                32 => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "Only 8-bit, 16-bit, and 32-bit mono PCM microphone buffers are supported."),
            };

            if (audioData.Length == 0)
                return [];

            if (audioData.Length % bytesPerSample != 0)
                throw new ArgumentException("Audio buffer length must align with the source sample size.", nameof(audioData));

            int sourceSampleCount = audioData.Length / bytesPerSample;
            float[] normalizedSamples = new float[sourceSampleCount];

            switch (bitsPerSample)
            {
                case 8:
                    for (int i = 0; i < sourceSampleCount; i++)
                        normalizedSamples[i] = (audioData[i] - 128.0f) / 128.0f;
                    break;
                case 16:
                    for (int i = 0; i < sourceSampleCount; i++)
                        normalizedSamples[i] = BitConverter.ToInt16(audioData, i * sizeof(short)) / 32768.0f;
                    break;
                case 32:
                    for (int i = 0; i < sourceSampleCount; i++)
                        normalizedSamples[i] = Math.Clamp(BitConverter.ToSingle(audioData, i * sizeof(float)), -1.0f, 1.0f);
                    break;
            }

            if (sourceSampleRate == targetSampleRate)
            {
                short[] directOutput = new short[sourceSampleCount];
                for (int i = 0; i < directOutput.Length; i++)
                    directOutput[i] = FloatToPcm16(normalizedSamples[i]);
                return directOutput;
            }

            int targetSampleCount = Math.Max(1, (int)Math.Round(sourceSampleCount * (double)targetSampleRate / sourceSampleRate, MidpointRounding.AwayFromZero));
            short[] resampledOutput = new short[targetSampleCount];
            double sourceSamplesPerTargetSample = (double)sourceSampleRate / targetSampleRate;

            for (int i = 0; i < targetSampleCount; i++)
            {
                double sourcePosition = i * sourceSamplesPerTargetSample;
                int sourceIndex0 = Math.Min((int)sourcePosition, sourceSampleCount - 1);
                int sourceIndex1 = Math.Min(sourceIndex0 + 1, sourceSampleCount - 1);
                float t = (float)(sourcePosition - sourceIndex0);
                float sample = normalizedSamples[sourceIndex0] + ((normalizedSamples[sourceIndex1] - normalizedSamples[sourceIndex0]) * t);
                resampledOutput[i] = FloatToPcm16(sample);
            }

            return resampledOutput;
        }

        private static short FloatToPcm16(float sample)
        {
            float clamped = Math.Clamp(sample, -1.0f, 1.0f);
            return (short)Math.Round(clamped * short.MaxValue, MidpointRounding.AwayFromZero);
        }
    }

    public static class Audio2Face3DNativeBridge
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

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeSessionConfig
        {
            public int InputSampleRate;

            [MarshalAs(UnmanagedType.I1)]
            public bool EnableEmotion;

            [MarshalAs(UnmanagedType.LPUTF8Str)]
            public string? FaceModelPath;

            [MarshalAs(UnmanagedType.LPUTF8Str)]
            public string? EmotionModelPath;
        }

        internal sealed class NativeApi
        {
            internal delegate EAudio2XBridgeResult CreateSessionDelegate(out nint sessionHandle);
            internal delegate void DestroySessionDelegate(nint sessionHandle);
            internal delegate EAudio2XBridgeResult ConfigureSessionDelegate(nint sessionHandle, ref NativeSessionConfig config);
            internal delegate EAudio2XBridgeResult SubmitPcm16MonoDelegate(nint sessionHandle, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] short[] samples, int sampleCount, int sampleRate);
            internal delegate EAudio2XBridgeResult GetLayoutDelegate(nint sessionHandle, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[]? utf8Buffer, int bufferCapacity, out int requiredBytes, out int count);
            internal delegate EAudio2XBridgeResult PollWeightsDelegate(nint sessionHandle, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] float[]? weights, int capacity, out int count);
            internal delegate EAudio2XBridgeResult GetLastErrorDelegate(nint sessionHandle, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[]? utf8Buffer, int bufferCapacity, out int requiredBytes);
            internal delegate int IsBackendAvailableDelegate();
            internal delegate int GetRequiredInputSampleRateDelegate();

            public required CreateSessionDelegate CreateSession { get; init; }
            public required DestroySessionDelegate DestroySession { get; init; }
            public required ConfigureSessionDelegate ConfigureSession { get; init; }
            public required SubmitPcm16MonoDelegate SubmitPcm16Mono { get; init; }
            public required GetLayoutDelegate GetBlendshapeLayout { get; init; }
            public required GetLayoutDelegate GetEmotionLayout { get; init; }
            public required PollWeightsDelegate PollBlendshapeWeights { get; init; }
            public required PollWeightsDelegate PollEmotionWeights { get; init; }
            public required GetLastErrorDelegate GetLastError { get; init; }
            public required IsBackendAvailableDelegate IsBackendAvailable { get; init; }
            public required GetRequiredInputSampleRateDelegate GetRequiredInputSampleRate { get; init; }

            public static bool TryCreate(nint libraryHandle, out NativeApi? api, out string? error)
            {
                api = null;

                if (!TryLoad(libraryHandle, "A2XBridge_CreateSession", out CreateSessionDelegate? createSession, out error)
                    || !TryLoad(libraryHandle, "A2XBridge_DestroySession", out DestroySessionDelegate? destroySession, out error)
                    || !TryLoad(libraryHandle, "A2XBridge_ConfigureSession", out ConfigureSessionDelegate? configureSession, out error)
                    || !TryLoad(libraryHandle, "A2XBridge_SubmitPcm16Mono", out SubmitPcm16MonoDelegate? submitPcm16Mono, out error)
                    || !TryLoad(libraryHandle, "A2XBridge_GetBlendshapeLayout", out GetLayoutDelegate? getBlendshapeLayout, out error)
                    || !TryLoad(libraryHandle, "A2XBridge_GetEmotionLayout", out GetLayoutDelegate? getEmotionLayout, out error)
                    || !TryLoad(libraryHandle, "A2XBridge_PollBlendshapeWeights", out PollWeightsDelegate? pollBlendshapeWeights, out error)
                    || !TryLoad(libraryHandle, "A2XBridge_PollEmotionWeights", out PollWeightsDelegate? pollEmotionWeights, out error)
                    || !TryLoad(libraryHandle, "A2XBridge_GetLastError", out GetLastErrorDelegate? getLastError, out error)
                    || !TryLoad(libraryHandle, "A2XBridge_IsBackendAvailable", out IsBackendAvailableDelegate? isBackendAvailable, out error)
                    || !TryLoad(libraryHandle, "A2XBridge_GetRequiredInputSampleRate", out GetRequiredInputSampleRateDelegate? getRequiredInputSampleRate, out error))
                {
                    return false;
                }

                api = new NativeApi
                {
                    CreateSession = createSession!,
                    DestroySession = destroySession!,
                    ConfigureSession = configureSession!,
                    SubmitPcm16Mono = submitPcm16Mono!,
                    GetBlendshapeLayout = getBlendshapeLayout!,
                    GetEmotionLayout = getEmotionLayout!,
                    PollBlendshapeWeights = pollBlendshapeWeights!,
                    PollEmotionWeights = pollEmotionWeights!,
                    GetLastError = getLastError!,
                    IsBackendAvailable = isBackendAvailable!,
                    GetRequiredInputSampleRate = getRequiredInputSampleRate!,
                };

                error = null;
                return true;
            }

            private static bool TryLoad<T>(nint libraryHandle, string exportName, out T? value, out string? error) where T : Delegate
            {
                value = null;
                if (!NativeLibrary.TryGetExport(libraryHandle, exportName, out nint exportHandle) || exportHandle == nint.Zero)
                {
                    error = $"Audio2XBridge.Native is missing the required export '{exportName}'.";
                    return false;
                }

                value = Marshal.GetDelegateForFunctionPointer<T>(exportHandle);
                error = null;
                return true;
            }
        }
    }

    public sealed class Audio2Face3DNativeBridgeSession : IDisposable
    {
        private readonly nint _sessionHandle;
        private readonly Audio2Face3DNativeBridge.NativeApi _api;
        private bool _disposed;
        private string[]? _blendshapeNames;
        private string[]? _emotionNames;

        internal Audio2Face3DNativeBridgeSession(nint sessionHandle, Audio2Face3DNativeBridge.NativeApi api)
        {
            _sessionHandle = sessionHandle;
            _api = api;
        }

        public bool TrySubmitPcm16(short[] samples, int sampleRate, out string? error)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (samples is null || samples.Length == 0)
            {
                error = null;
                return true;
            }

            EAudio2XBridgeResult result = _api.SubmitPcm16Mono(_sessionHandle, samples, samples.Length, sampleRate);
            if (result == EAudio2XBridgeResult.Success)
            {
                error = null;
                return true;
            }

            error = Audio2Face3DNativeBridge.GetLastError(_sessionHandle, $"Audio2XBridge.Native rejected audio submission ({result}).");
            return false;
        }

        public EAudio2Face3DNativePollResult PollBlendshapeFrame(out string[]? blendshapeNames, out float[]? weights, out string? error)
            => PollFrame(_api.GetBlendshapeLayout, _api.PollBlendshapeWeights, ref _blendshapeNames, out blendshapeNames, out weights, out error);

        public EAudio2Face3DNativePollResult PollEmotionFrame(out string[]? emotionNames, out float[]? weights, out string? error)
            => PollFrame(_api.GetEmotionLayout, _api.PollEmotionWeights, ref _emotionNames, out emotionNames, out weights, out error);

        public void Dispose()
        {
            if (_disposed)
                return;

            _api.DestroySession(_sessionHandle);
            _disposed = true;
        }

        private EAudio2Face3DNativePollResult PollFrame(
            Audio2Face3DNativeBridge.NativeApi.GetLayoutDelegate getLayout,
            Audio2Face3DNativeBridge.NativeApi.PollWeightsDelegate pollWeights,
            ref string[]? cachedNames,
            out string[]? names,
            out float[]? weights,
            out string? error)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            names = null;
            weights = null;

            if (!EnsureLayout(getLayout, ref cachedNames, out error) || cachedNames is null || cachedNames.Length == 0)
                return string.IsNullOrWhiteSpace(error) ? EAudio2Face3DNativePollResult.NoData : EAudio2Face3DNativePollResult.Error;

            float[] nextWeights = new float[cachedNames.Length];
            EAudio2XBridgeResult result = pollWeights(_sessionHandle, nextWeights, nextWeights.Length, out int count);
            if (result == EAudio2XBridgeResult.NoData)
            {
                error = null;
                return EAudio2Face3DNativePollResult.NoData;
            }

            if (result != EAudio2XBridgeResult.Success)
            {
                error = Audio2Face3DNativeBridge.GetLastError(_sessionHandle, $"Audio2XBridge.Native failed to poll weights ({result}).");
                return EAudio2Face3DNativePollResult.Error;
            }

            if (count != cachedNames.Length)
            {
                error = $"Audio2XBridge.Native returned {count} weights for a layout that expected {cachedNames.Length}.";
                return EAudio2Face3DNativePollResult.Error;
            }

            names = cachedNames;
            weights = nextWeights;
            error = null;
            return EAudio2Face3DNativePollResult.Success;
        }

        private bool EnsureLayout(
            Audio2Face3DNativeBridge.NativeApi.GetLayoutDelegate getLayout,
            ref string[]? cachedNames,
            out string? error)
        {
            if (cachedNames is not null)
            {
                error = null;
                return true;
            }

            EAudio2XBridgeResult sizeResult = getLayout(_sessionHandle, null, 0, out int requiredBytes, out int count);
            if (sizeResult != EAudio2XBridgeResult.Success && sizeResult != EAudio2XBridgeResult.NoData)
            {
                error = Audio2Face3DNativeBridge.GetLastError(_sessionHandle, $"Audio2XBridge.Native failed to resolve the output layout ({sizeResult}).");
                return false;
            }

            if (requiredBytes <= 1 || count <= 0)
            {
                cachedNames = [];
                error = null;
                return true;
            }

            byte[] buffer = new byte[requiredBytes];
            EAudio2XBridgeResult layoutResult = getLayout(_sessionHandle, buffer, buffer.Length, out requiredBytes, out count);
            if (layoutResult != EAudio2XBridgeResult.Success)
            {
                error = Audio2Face3DNativeBridge.GetLastError(_sessionHandle, $"Audio2XBridge.Native failed to fetch the output layout ({layoutResult}).");
                return false;
            }

            int terminatorIndex = Array.IndexOf(buffer, (byte)0);
            int byteCount = terminatorIndex >= 0 ? terminatorIndex : buffer.Length;
            string layout = Encoding.UTF8.GetString(buffer, 0, byteCount);
            cachedNames = [.. layout.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            error = null;
            return true;
        }
    }
}