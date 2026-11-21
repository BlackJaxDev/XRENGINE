using System.Runtime.InteropServices;

namespace XREngine.Animation.MotionBricks;

public static partial class MotionBricksTensorRtBridge
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSessionConfig
    {
        public nint OnnxModelPath;
        public nint PlanCacheDir;
        public int EnableFp16;
        public int EnableTf32;
        public int BuilderOptimizationLevel;
        public int ForceRebuild;
        public long WorkspaceSizeBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeSomaInputView
    {
        public float* GlobalRootValues;
        public int GlobalRootValuesCount;
        public int* HasGlobalRootValues;
        public int HasGlobalRootValuesCount;
        public float* LocalRootValues;
        public int LocalRootValuesCount;
        public int* HasLocalRootValues;
        public int HasLocalRootValuesCount;
        public float* LocalPoses;
        public int LocalPosesCount;
        public int* HasLocalPoses;
        public int HasLocalPosesCount;
        public int* AllowedPredNumTokens;
        public int AllowedPredNumTokensCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeSomaOutputView
    {
        public float* PredGlobalMotions;
        public int PredGlobalMotionsCount;
        public int* PredNumTokens;
        public int PredNumTokensCount;
    }

    internal sealed class NativeApi
    {
        internal delegate int IsBackendAvailableDelegate();
        internal delegate int GetTensorRtVersionDelegate();
        internal delegate EMotionBricksBridgeResult CreateSessionDelegate(out nint sessionHandle);
        internal delegate void DestroySessionDelegate(nint sessionHandle);
        internal delegate EMotionBricksBridgeResult ConfigureSessionDelegate(nint sessionHandle, ref NativeSessionConfig config);
        internal delegate EMotionBricksBridgeResult BuildOrLoadEngineDelegate(nint sessionHandle, out int loadedFromCache);
        internal delegate EMotionBricksBridgeResult RunSomaInferenceDelegate(nint sessionHandle, ref NativeSomaInputView input, ref NativeSomaOutputView output);
        internal delegate EMotionBricksBridgeResult GetLastErrorDelegate(
            nint sessionHandle,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[]? utf8Buffer,
            int bufferCapacity,
            out int requiredBytes);

        public required IsBackendAvailableDelegate IsBackendAvailable { get; init; }
        public required GetTensorRtVersionDelegate GetTensorRtVersion { get; init; }
        public required CreateSessionDelegate CreateSession { get; init; }
        public required DestroySessionDelegate DestroySession { get; init; }
        public required ConfigureSessionDelegate ConfigureSession { get; init; }
        public required BuildOrLoadEngineDelegate BuildOrLoadEngine { get; init; }
        public required RunSomaInferenceDelegate RunSomaInference { get; init; }
        public required GetLastErrorDelegate GetLastError { get; init; }

        public static bool TryCreate(nint libraryHandle, out NativeApi? api, out string? error)
        {
            api = null;

            if (!TryLoad(libraryHandle, "MBBridge_IsBackendAvailable", out IsBackendAvailableDelegate? isAvailable, out error)
                || !TryLoad(libraryHandle, "MBBridge_GetTensorRtVersion", out GetTensorRtVersionDelegate? getVersion, out error)
                || !TryLoad(libraryHandle, "MBBridge_CreateSession", out CreateSessionDelegate? createSession, out error)
                || !TryLoad(libraryHandle, "MBBridge_DestroySession", out DestroySessionDelegate? destroySession, out error)
                || !TryLoad(libraryHandle, "MBBridge_ConfigureSession", out ConfigureSessionDelegate? configureSession, out error)
                || !TryLoad(libraryHandle, "MBBridge_BuildOrLoadEngine", out BuildOrLoadEngineDelegate? buildOrLoadEngine, out error)
                || !TryLoad(libraryHandle, "MBBridge_RunSomaInference", out RunSomaInferenceDelegate? runSomaInference, out error)
                || !TryLoad(libraryHandle, "MBBridge_GetLastError", out GetLastErrorDelegate? getLastError, out error))
            {
                return false;
            }

            api = new NativeApi
            {
                IsBackendAvailable = isAvailable!,
                GetTensorRtVersion = getVersion!,
                CreateSession = createSession!,
                DestroySession = destroySession!,
                ConfigureSession = configureSession!,
                BuildOrLoadEngine = buildOrLoadEngine!,
                RunSomaInference = runSomaInference!,
                GetLastError = getLastError!,
            };

            error = null;
            return true;
        }

        private static bool TryLoad<T>(nint libraryHandle, string exportName, out T? value, out string? error)
            where T : Delegate
        {
            value = null;
            if (!NativeLibrary.TryGetExport(libraryHandle, exportName, out nint exportHandle) || exportHandle == nint.Zero)
            {
                error = $"{LibraryFileName} is missing the required export '{exportName}'.";
                return false;
            }

            value = Marshal.GetDelegateForFunctionPointer<T>(exportHandle);
            error = null;
            return true;
        }
    }
}
