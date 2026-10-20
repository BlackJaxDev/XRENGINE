namespace XREngine.Scene.Transforms;

/// <summary>Holds the process factory for transform propagation workers.</summary>
internal static class TransformPropagationWorkerServices
{
    private static ITransformPropagationWorkerPoolFactory? _factory;

    /// <summary>Gets the registered factory without creating workers.</summary>
    internal static ITransformPropagationWorkerPoolFactory? Factory => Volatile.Read(ref _factory);

    /// <summary>Installs a factory only when the slot is empty.</summary>
    internal static bool TryRegisterFactory(ITransformPropagationWorkerPoolFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Interlocked.CompareExchange(ref _factory, factory, null) is null;
    }

    /// <summary>Gets the factory required for parallel transform propagation.</summary>
    internal static ITransformPropagationWorkerPoolFactory GetRequiredFactory()
        => Factory ?? throw new InvalidOperationException(
            "No transform propagation worker factory is registered. Call ThreadedWorkerBackend.EnsureRegistered() before processing parallel transforms.");
}
