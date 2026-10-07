using XREngine.Execution;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Registers the built-in desktop worker backend without starting worker threads.</summary>
public static class DesktopWorkerBackend
{
    /// <summary>Registers the built-in worker backend if no host factory is installed.</summary>
    public static void EnsureRegistered() => ThreadedWorkerBackend.EnsureRegistered();
}
