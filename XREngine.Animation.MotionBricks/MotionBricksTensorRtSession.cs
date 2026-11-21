namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Owns one native TensorRT session handle and runs zero-allocation SOMA forward passes
/// over caller-provided host tensor buffers.
/// </summary>
public sealed class MotionBricksTensorRtSession : IMotionBricksInferenceSession
{
    private readonly MotionBricksTensorRtBridge.NativeApi _api;
    private nint _sessionHandle;
    private bool _engineReady;

    internal MotionBricksTensorRtSession(nint sessionHandle, MotionBricksTensorRtBridge.NativeApi api)
    {
        _sessionHandle = sessionHandle;
        _api = api;
    }

    public bool IsEngineReady => _engineReady && _sessionHandle != nint.Zero;

    public bool TryBuildOrLoadEngine(out bool loadedFromCache, out string? error)
    {
        loadedFromCache = false;
        if (_sessionHandle == nint.Zero)
        {
            error = "MotionBricks TensorRT session has been disposed.";
            return false;
        }

        EMotionBricksBridgeResult result = _api.BuildOrLoadEngine(_sessionHandle, out int cachedFlag);
        if (result != EMotionBricksBridgeResult.Success)
        {
            _engineReady = false;
            error = MotionBricksTensorRtBridge.GetLastError(
                _sessionHandle,
                $"MotionBricks TensorRT engine build/load failed ({result}).");
            return false;
        }

        _engineReady = true;
        loadedFromCache = cachedFlag != 0;
        error = null;
        return true;
    }

    public unsafe bool TryRunSomaInference(
        SomaInferenceInputBuffer input,
        SomaInferenceOutputBuffer output,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (_sessionHandle == nint.Zero)
        {
            error = "MotionBricks TensorRT session has been disposed.";
            return false;
        }

        if (!_engineReady)
        {
            error = "TensorRT engine is not ready. Call TryBuildOrLoadEngine first.";
            return false;
        }

        fixed (float* globalRootPtr = input.GlobalRootValues)
        fixed (int* hasGlobalRootPtr = input.HasGlobalRootValues)
        fixed (float* localRootPtr = input.LocalRootValues)
        fixed (int* hasLocalRootPtr = input.HasLocalRootValues)
        fixed (float* localPosesPtr = input.LocalPoses)
        fixed (int* hasLocalPosesPtr = input.HasLocalPoses)
        fixed (int* allowedTokensPtr = input.AllowedPredNumTokens)
        fixed (float* predMotionsPtr = output.PredGlobalMotions)
        fixed (int* predTokensPtr = output.PredNumTokens)
        {
            var inputView = new MotionBricksTensorRtBridge.NativeSomaInputView
            {
                GlobalRootValues = globalRootPtr,
                GlobalRootValuesCount = input.GlobalRootValues.Length,
                HasGlobalRootValues = hasGlobalRootPtr,
                HasGlobalRootValuesCount = input.HasGlobalRootValues.Length,
                LocalRootValues = localRootPtr,
                LocalRootValuesCount = input.LocalRootValues.Length,
                HasLocalRootValues = hasLocalRootPtr,
                HasLocalRootValuesCount = input.HasLocalRootValues.Length,
                LocalPoses = localPosesPtr,
                LocalPosesCount = input.LocalPoses.Length,
                HasLocalPoses = hasLocalPosesPtr,
                HasLocalPosesCount = input.HasLocalPoses.Length,
                AllowedPredNumTokens = allowedTokensPtr,
                AllowedPredNumTokensCount = input.AllowedPredNumTokens.Length,
            };

            var outputView = new MotionBricksTensorRtBridge.NativeSomaOutputView
            {
                PredGlobalMotions = predMotionsPtr,
                PredGlobalMotionsCount = output.PredGlobalMotions.Length,
                PredNumTokens = predTokensPtr,
                PredNumTokensCount = output.PredNumTokens.Length,
            };

            EMotionBricksBridgeResult result = _api.RunSomaInference(_sessionHandle, ref inputView, ref outputView);
            if (result != EMotionBricksBridgeResult.Success)
            {
                error = MotionBricksTensorRtBridge.GetLastError(
                    _sessionHandle,
                    $"MotionBricks TensorRT inference failed ({result}).");
                return false;
            }
        }

        error = null;
        return true;
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _sessionHandle, nint.Zero);
        _engineReady = false;
        if (handle != nint.Zero)
            _api.DestroySession(handle);
    }
}
