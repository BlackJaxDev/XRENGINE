namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Swappable backend interface for MotionBricks neural inference (for example NVIDIA TensorRT
/// or a future ONNX Runtime DirectML provider).
/// </summary>
public interface IMotionBricksInferenceBackend
{
    /// <summary>
    /// Stable diagnostic name of this inference backend.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Checks whether the required native runtime and hardware backend are available.
    /// Never silently falls back to CPU execution when a GPU backend is requested.
    /// </summary>
    bool IsAvailable(out string? error);

    /// <summary>
    /// Creates and configures a new inference session without running model build work yet.
    /// </summary>
    bool TryCreateSession(MotionBricksSessionConfig config, out IMotionBricksInferenceSession? session, out string? error);
}
