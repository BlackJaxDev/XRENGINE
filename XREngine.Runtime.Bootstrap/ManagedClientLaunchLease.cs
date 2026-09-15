using XREngine.ControlPlane;

namespace XREngine.Runtime.Bootstrap;

/// <summary>In-memory managed launch paired with the cache lease that keeps its package available.</summary>
public sealed class ManagedClientLaunchLease(ManagedClientLaunch launch, RemoteWorldPackageLease packageLease) : IDisposable
{
    public ManagedClientLaunch Launch { get; } = launch;
    public void Dispose() => packageLease.Dispose();
}
