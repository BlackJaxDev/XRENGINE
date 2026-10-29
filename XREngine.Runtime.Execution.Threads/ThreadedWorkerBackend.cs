using XREngine.Scene.Transforms;

namespace XREngine.Execution;

/// <summary>Registers native workers and blocking task completion.</summary>
public static class ThreadedWorkerBackend
{
    private static readonly IEngineWorkerDomainFactory BuiltInFactory = new ThreadedWorkerDomainFactory();
    private static readonly ITransformPropagationWorkerPoolFactory BuiltInTransformFactory = new ThreadedTransformPropagationWorkerPoolFactory();
    private static readonly IProfilerStatsWorkerFactory BuiltInProfilerFactory = new ThreadedProfilerStatsWorkerFactory();
    private static readonly IRuntimeBlockingTaskBackend BuiltInBlockingTaskBackend = new ThreadedBlockingTaskBackend();

    /// <summary>Registers built-in providers only in empty slots.</summary>
    public static void EnsureRegistered()
    {
        RuntimeBlockingTaskServices.TryRegisterBackend(BuiltInBlockingTaskBackend);
        EngineWorkerDomainServices.TryRegisterFactory(BuiltInFactory);
        TransformPropagationWorkerServices.TryRegisterFactory(BuiltInTransformFactory);
        ProfilerStatsWorkerServices.TryRegisterFactory(BuiltInProfilerFactory);
    }
}
