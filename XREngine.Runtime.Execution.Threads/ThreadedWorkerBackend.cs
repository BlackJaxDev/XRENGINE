using XREngine.Scene.Transforms;

namespace XREngine.Execution;

/// <summary>Registers physical worker domains and transform workers for native hosts.</summary>
public static class ThreadedWorkerBackend
{
    private static readonly IEngineWorkerDomainFactory BuiltInFactory = new ThreadedWorkerDomainFactory();
    private static readonly ITransformPropagationWorkerPoolFactory BuiltInTransformFactory = new ThreadedTransformPropagationWorkerPoolFactory();

    /// <summary>Registers built-in factories only in empty slots.</summary>
    public static void EnsureRegistered()
    {
        EngineWorkerDomainServices.TryRegisterFactory(BuiltInFactory);
        TransformPropagationWorkerServices.TryRegisterFactory(BuiltInTransformFactory);
    }
}
