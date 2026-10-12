using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Loads a model hierarchy for renderer-owned runtime consumers without exposing
/// importer implementations or producer-specific dependencies to Rendering or Bootstrap.
/// </summary>
public interface IRuntimeModelSceneLoadingServices
{
    Task<SceneNode?> LoadAsync(
        string sourcePath,
        SceneNode parent,
        CancellationToken cancellationToken = default);
}

/// <summary>Feature-composed runtime model hierarchy loader.</summary>
public static class RuntimeModelSceneLoadingServices
{
    private static readonly IRuntimeModelSceneLoadingServices Uninstalled =
        new UninstalledRuntimeModelSceneLoadingServices();
    private static readonly object Sync = new();
    private static IRuntimeModelSceneLoadingServices _current = Uninstalled;
    private static InstallationLease? _head;

    public static IRuntimeModelSceneLoadingServices Current => Volatile.Read(ref _current);

    public static bool IsInstalled => !ReferenceEquals(Current, Uninstalled);

    public static IDisposable Install(IRuntimeModelSceneLoadingServices services)
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
        IRuntimeModelSceneLoadingServices installed,
        InstallationLease? previous) : IDisposable
    {
        public IRuntimeModelSceneLoadingServices Installed { get; } = installed;
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
                Volatile.Write(ref _current, next?.Installed ?? Uninstalled);
            }
        }
    }

    private sealed class UninstalledRuntimeModelSceneLoadingServices : IRuntimeModelSceneLoadingServices
    {
        public Task<SceneNode?> LoadAsync(
            string sourcePath,
            SceneNode parent,
            CancellationToken cancellationToken = default)
            => Task.FromException<SceneNode?>(new InvalidOperationException(
                $"No runtime model-scene loader is installed for '{sourcePath}'. " +
                "Install ModelAssetPipelineRegistration in the application composition root."));
    }
}
