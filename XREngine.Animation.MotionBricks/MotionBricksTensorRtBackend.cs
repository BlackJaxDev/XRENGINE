namespace XREngine.Animation.MotionBricks;

/// <summary>
/// <see cref="IMotionBricksInferenceBackend"/> implementation backed by NVIDIA TensorRT
/// through <see cref="MotionBricksTensorRtBridge"/>.
/// </summary>
public sealed class MotionBricksTensorRtBackend : IMotionBricksInferenceBackend
{
    public static MotionBricksTensorRtBackend Instance { get; } = new();

    public string Name => "NVIDIA TensorRT";

    public bool IsAvailable(out string? error)
        => MotionBricksTensorRtBridge.IsAvailable(out error);

    public bool TryCreateSession(
        MotionBricksSessionConfig config,
        out IMotionBricksInferenceSession? session,
        out string? error)
    {
        if (MotionBricksTensorRtBridge.TryCreateSession(config, out MotionBricksTensorRtSession? typedSession, out error))
        {
            session = typedSession;
            return true;
        }

        session = null;
        return false;
    }
}
