using XREngine.Components;
using XREngine.Execution;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Registers the built-in desktop worker backend without starting worker threads.</summary>
public static class DesktopWorkerBackend
{
    /// <summary>
    /// Registers the built-in worker backend and the physics-chain worker-group
    /// factory if the host has not installed its own.
    /// </summary>
    public static void EnsureRegistered()
    {
        ThreadedWorkerBackend.EnsureRegistered();
        PhysicsChainCpuWorkerGroupServices.Current ??= new DesktopPhysicsChainCpuWorkerGroupFactory();
    }
}
