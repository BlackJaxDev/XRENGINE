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
    private static readonly object Sync = new();
    private static IRuntimeSceneImportServices? _current;
    private static InstallationLease? _head;

    /// <summary>Gets the installed scene importer, or <see langword="null"/> when none is installed.</summary>
    public static IRuntimeSceneImportServices? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Installs an application-owned scene importer and returns a lease that restores
    /// the previously installed importer when disposed.
    /// </summary>
    public static IDisposable Install(IRuntimeSceneImportServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (Sync)
        {
            InstallationLease lease = new(services, _head);
            _head = lease;
            Volatile.Write(ref _current, services);
            return lease;
        }
    }

    private sealed class InstallationLease(
        IRuntimeSceneImportServices installed,
        InstallationLease? previous) : IDisposable
    {
        public IRuntimeSceneImportServices Installed { get; } = installed;
        public InstallationLease? Previous { get; } = previous;
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            lock (Sync)
            {
                if (IsDisposed)
                    return;
                IsDisposed = true;
                if (!ReferenceEquals(_head, this))
                    return;
                InstallationLease? next = Previous;
                while (next?.IsDisposed == true)
                    next = next.Previous;
                _head = next;
                Volatile.Write(ref _current, next?.Installed);
            }
        }
    }
}
