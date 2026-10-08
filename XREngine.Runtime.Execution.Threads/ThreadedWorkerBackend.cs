using XREngine.Scene.Transforms;

namespace XREngine.Execution;

/// <summary>Registers native job, transform, and profiler workers.</summary>
public static class ThreadedWorkerBackend
{
    private static readonly IEngineWorkerDomainFactory BuiltInFactory = new ThreadedWorkerDomainFactory();
    private static readonly ITransformPropagationWorkerPoolFactory BuiltInTransformFactory = new ThreadedTransformPropagationWorkerPoolFactory();
    private static readonly IProfilerStatsWorkerFactory BuiltInProfilerFactory = new ThreadedProfilerStatsWorkerFactory();

    /// <summary>Registers built-in factories only in empty slots.</summary>
    public static void EnsureRegistered()
    {
        EngineWorkerDomainServices.TryRegisterFactory(BuiltInFactory);
        TransformPropagationWorkerServices.TryRegisterFactory(BuiltInTransformFactory);
        ProfilerStatsWorkerServices.TryRegisterFactory(BuiltInProfilerFactory);
    }
}
