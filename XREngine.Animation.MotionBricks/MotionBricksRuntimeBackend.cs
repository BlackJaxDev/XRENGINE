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
            configResolver ?? (_ => ResolveDefaultSessionConfig()));
    }

    /// <summary>
    /// Resolves the default SOMA FP16 ONNX path and TensorRT plan cache directory from
    /// environment overrides or the local <c>Build/Dependencies/MotionBricks/</c> staging tree.
    /// </summary>
    public static MotionBricksSessionConfig ResolveDefaultSessionConfig()
    {
        string? envOnnx = Environment.GetEnvironmentVariable("MOTIONBRICKS_ONNX_PATH");
        string? envCache = Environment.GetEnvironmentVariable("MOTIONBRICKS_CACHE_DIR");

        string onnxPath = !string.IsNullOrWhiteSpace(envOnnx)
            ? Path.GetFullPath(envOnnx)
            : FindRelativeFile(Path.Combine("Build", "Dependencies", "MotionBricks", "soma", "mmm_v6_soma_fp16.onnx"));

        string cacheDir;
        if (!string.IsNullOrWhiteSpace(envCache))
            cacheDir = Path.GetFullPath(envCache);
        else if (!string.IsNullOrWhiteSpace(onnxPath))
            cacheDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(onnxPath)!)!, "cache");
        else
            cacheDir = Path.GetFullPath(Path.Combine("Build", "Dependencies", "MotionBricks", "cache"));

        return new MotionBricksSessionConfig
        {
            OnnxModelPath = onnxPath,
            PlanCacheDirectory = cacheDir,
            EnableFp16 = true,
            EnableTf32 = true,
            BuilderOptimizationLevel = 3,
        };
    }

    private static string FindRelativeFile(string relativePath)
    {
        string[] roots = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];
        for (int r = 0; r < roots.Length; r++)
        {
            DirectoryInfo? current = new DirectoryInfo(roots[r]);
            for (int depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
            {
                string candidate = Path.Combine(current.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return Path.GetFullPath(relativePath);
    }

    private sealed class TensorRtDriverFactory(
        IMotionBricksInferenceBackend backend,
        Func<MotionBricksLocomotionMotion, MotionBricksSessionConfig>? configResolver)
        : IMotionBricksLocomotionDriverFactory
    {
        public IMotionBricksLocomotionDriver CreateDriver(MotionBricksLocomotionMotion motion)
        {
            if (configResolver is null)
                return new MotionBricksAsyncLocomotionDriver(inferenceSession: null);

            MotionBricksSessionConfig config = configResolver(motion);
            if (!backend.TryCreateSession(config, out IMotionBricksInferenceSession? session, out string? error) || session is null)
                return new UnavailableMotionBricksDriver(error ?? $"{backend.Name} backend failed to create a session.");

            if (!session.TryBuildOrLoadEngine(out _, out string? buildError))
            {
                session.Dispose();
                return new UnavailableMotionBricksDriver(buildError ?? $"{backend.Name} backend failed to build or load the SOMA engine.");
            }

            return new MotionBricksAsyncLocomotionDriver(
                session,
                ownsSession: true,
                calibration: SomaRestSkeletonCalibration.SomaN1Default);
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
