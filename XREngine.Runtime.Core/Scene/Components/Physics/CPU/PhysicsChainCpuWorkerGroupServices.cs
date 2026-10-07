namespace XREngine.Components;

/// <summary>Holds the worker capability installed by the host.</summary>
public static class PhysicsChainCpuWorkerGroupServices
{
    private static IPhysicsChainCpuWorkerGroupFactory? _current;

    public static IPhysicsChainCpuWorkerGroupFactory? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IPhysicsChainCpuWorkerGroupFactory Required => Current ?? throw new NotSupportedException(
        "PhysicsChainCpuWorkerGroup.MissingFactory: positive worker counts require an installed host worker-group factory.");
}
