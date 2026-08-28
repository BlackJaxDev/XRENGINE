namespace XREngine.Scene;

/// <summary>
/// Optional application-owned scene import boundary. Runtime.Core owns the
/// serialized scene identity without depending on editor import implementations.
/// </summary>
public interface IRuntimeSceneImportServices
{
    IReadOnlyList<SceneNode> ImportScene(string filePath);
}

/// <summary>
/// Installation point for optional application-owned scene import support.
/// The uninstalled state is <see langword="null"/> so headless consumers do not
/// accidentally acquire editor import behavior.
/// </summary>
public static class RuntimeSceneImportServices
{
    private static IRuntimeSceneImportServices? _current;

    /// <summary>Gets the installed scene importer, or <see langword="null"/> when none is installed.</summary>
    public static IRuntimeSceneImportServices? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Installs an application-owned scene importer and returns a lease that restores
    /// the previously installed importer when disposed.
    /// </summary>
    public static IDisposable Install(IRuntimeSceneImportServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IRuntimeSceneImportServices? previous = Interlocked.Exchange(ref _current, services);
        return new InstallationLease(services, previous);
    }

    private sealed class InstallationLease(
        IRuntimeSceneImportServices installed,
        IRuntimeSceneImportServices? previous) : IDisposable
    {
        private IRuntimeSceneImportServices? _installed = installed;

        public void Dispose()
        {
            IRuntimeSceneImportServices? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
        }
    }
}
