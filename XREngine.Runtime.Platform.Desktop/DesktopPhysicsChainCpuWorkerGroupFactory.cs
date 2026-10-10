using XREngine.Components;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Creates persistent desktop workers for physics-chain ranges.</summary>
public sealed class DesktopPhysicsChainCpuWorkerGroupFactory : IPhysicsChainCpuWorkerGroupFactory
{
    public IPhysicsChainCpuWorkerGroup Create(int requestedWorkerCount, Action<int> processRanges)
        => new DesktopPhysicsChainCpuWorkerGroup(requestedWorkerCount, processRanges);
}
