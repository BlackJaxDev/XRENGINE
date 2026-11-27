namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Active inference session for running SOMA MotionBricks forward passes on preallocated buffers.
/// </summary>
public interface IMotionBricksInferenceSession : IDisposable
{
    /// <summary>
    /// True after the backend engine has been built or loaded and is ready for inference.
    /// </summary>
    bool IsEngineReady { get; }

    /// <summary>
    /// Builds a fresh engine plan or loads a compatible cached plan for the configured model.
    /// </summary>
    bool TryBuildOrLoadEngine(out bool loadedFromCache, out string? error);

    /// <summary>
    /// Executes one SOMA forward pass reading from <paramref name="input"/> and writing raw
    /// output tensors into <paramref name="output"/>.
    /// </summary>
    bool TryRunSomaInference(SomaInferenceInputBuffer input, SomaInferenceOutputBuffer output, out string? error);
}
