using System.Runtime.InteropServices;

namespace XREngine.Components
{
    public static partial class Audio2Face3DNativeBridge
    {
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
}