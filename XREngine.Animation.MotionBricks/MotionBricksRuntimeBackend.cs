namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Registers the TensorRT-backed MotionBricks driver factory with
/// <see cref="MotionBricksLocomotionRuntimeServices"/> without loading TensorRT
/// until a scene initializes a <see cref="MotionBricksLocomotionMotion"/>.
/// </summary>
public static class MotionBricksRuntimeBackend
{
    public static void Register(
        IMotionBricksInferenceBackend? backend = null,
        Func<MotionBricksLocomotionMotion, MotionBricksSessionConfig>? configResolver = null)
    {
        MotionBricksLocomotionRuntimeServices.CurrentFactory = new TensorRtDriverFactory(
            backend ?? MotionBricksTensorRtBackend.Instance,
            configResolver);
    }

    private sealed class TensorRtDriverFactory(
        IMotionBricksInferenceBackend backend,
        Func<MotionBricksLocomotionMotion, MotionBricksSessionConfig>? configResolver)
        : IMotionBricksLocomotionDriverFactory
    {
        public IMotionBricksLocomotionDriver CreateDriver(MotionBricksLocomotionMotion motion)
        {
            if (configResolver is null)
                return new SomaFixedTargetLocomotionDriver(inferenceSession: null);

            MotionBricksSessionConfig config = configResolver(motion);
            if (!backend.TryCreateSession(config, out IMotionBricksInferenceSession? session, out string? error) || session is null)
                return new UnavailableMotionBricksDriver(error ?? $"{backend.Name} backend failed to create a session.");

            if (!session.TryBuildOrLoadEngine(out _, out string? buildError))
            {
                session.Dispose();
                return new UnavailableMotionBricksDriver(buildError ?? $"{backend.Name} backend failed to build or load the SOMA engine.");
            }

            return new SomaFixedTargetLocomotionDriver(session, ownsSession: true);
        }
    }

    private sealed class UnavailableMotionBricksDriver(string diagnosticStatus) : IMotionBricksLocomotionDriver
    {
        public bool IsReady => false;
        public string DiagnosticStatus { get; } = diagnosticStatus;
        public void OnStateEntered() { }
        public void OnStateExited() { }
        public void ResetPlayback() { }
        public void AdvanceAndEvaluate(in MotionBricksSteeringInput input, MotionBricksEvaluatedPose destination)
            => destination.HasValidPose = false;
        public void Dispose() { }
    }
}
