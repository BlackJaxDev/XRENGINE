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
        private readonly Lock _prewarmSync = new();
        private Task<(IMotionBricksInferenceSession? Session, string? Error)>? _prewarmTask;
        private Task<SomaMocapLocomotionAssetSet?>? _mocapAssetsTask;
        private string? _mocapAssetsRootDir;

        public void Prewarm()
        {
            if (configResolver is null)
                return;

            lock (_prewarmSync)
            {
                MotionBricksSessionConfig config = ResolveConfigSafely(motion: null);
                EnsureMocapAssetsTaskLocked(config);
                if (_prewarmTask is not null)
                    return;

                _prewarmTask = StartSessionWarmupTask(backend, config);
            }
        }

        public IMotionBricksLocomotionDriver CreateDriver(MotionBricksLocomotionMotion motion)
        {
            if (configResolver is null)
                return new MotionBricksAsyncLocomotionDriver(inferenceSession: null);

            Task<(IMotionBricksInferenceSession? Session, string? Error)> sessionTask;
            Task<SomaMocapLocomotionAssetSet?> mocapTask;
            lock (_prewarmSync)
            {
                MotionBricksSessionConfig config = ResolveConfigSafely(motion);
                mocapTask = EnsureMocapAssetsTaskLocked(config);
                if (_prewarmTask is not null)
                {
                    sessionTask = _prewarmTask;
                    _prewarmTask = null;
                }
                else
                {
                    sessionTask = StartSessionWarmupTask(backend, config);
                }
            }

            SomaMocapLocomotionAssetSet? mocapAssets = mocapTask.GetAwaiter().GetResult();
            var poseBank = new SomaLocomotionPoseBank(SomaRestSkeletonCalibration.SomaN1Default, mocapAssets);

            if (sessionTask.IsCompletedSuccessfully)
            {
                (IMotionBricksInferenceSession? session, string? error) = sessionTask.Result;
                if (session is not { IsEngineReady: true })
                {
                    session?.Dispose();
                    return new UnavailableMotionBricksDriver(error ?? $"{backend.Name} backend failed to build or load the SOMA engine.");
                }

                return new MotionBricksAsyncLocomotionDriver(
                    session,
                    ownsSession: true,
                    calibration: SomaRestSkeletonCalibration.SomaN1Default,
                    poseBank: poseBank);
            }

            // Never block the application thread waiting for TensorRT engine deserialization.
            // The driver's background worker receives the session as soon as warmup finishes.
            return new MotionBricksAsyncLocomotionDriver(
                sessionTask,
                calibration: SomaRestSkeletonCalibration.SomaN1Default,
                poseBank: poseBank);
        }

        private Task<SomaMocapLocomotionAssetSet?> EnsureMocapAssetsTaskLocked(MotionBricksSessionConfig config)
        {
            string? assetsRootDir = ResolveAssetsRootDirectory(config);
            if (_mocapAssetsTask is not null && string.Equals(_mocapAssetsRootDir, assetsRootDir, StringComparison.OrdinalIgnoreCase))
                return _mocapAssetsTask;

            _mocapAssetsRootDir = assetsRootDir;
            _mocapAssetsTask = Task.Run(() =>
            {
                SomaSmartPrimitiveAssetLoader.TryLoadPresetAssets(
                    assetsRootDir,
                    SomaRestSkeletonCalibration.SomaN1Default,
                    out SomaMocapLocomotionAssetSet? loaded,
                    out _);
                return loaded;
            });
            return _mocapAssetsTask;
        }

        private static string? ResolveAssetsRootDirectory(MotionBricksSessionConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.OnnxModelPath))
                return null;

            string? somaDir = Path.GetDirectoryName(config.OnnxModelPath);
            return somaDir is not null ? Path.GetDirectoryName(somaDir) : null;
        }

        private MotionBricksSessionConfig ResolveConfigSafely(MotionBricksLocomotionMotion? motion)
            => motion is not null ? configResolver!(motion) : ResolveDefaultSessionConfig();

        private static Task<(IMotionBricksInferenceSession? Session, string? Error)> StartSessionWarmupTask(
            IMotionBricksInferenceBackend backend,
            MotionBricksSessionConfig config)
            => Task.Run<(IMotionBricksInferenceSession? Session, string? Error)>(() =>
            {
                if (!backend.TryCreateSession(config, out IMotionBricksInferenceSession? session, out string? error) || session is null)
                    return (null, error ?? $"{backend.Name} backend failed to create a session.");

                if (!session.TryBuildOrLoadEngine(out _, out string? buildError))
                {
                    session.Dispose();
                    return (null, buildError ?? $"{backend.Name} backend failed to build or load the SOMA engine.");
                }

                return (session, null);
            });
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
