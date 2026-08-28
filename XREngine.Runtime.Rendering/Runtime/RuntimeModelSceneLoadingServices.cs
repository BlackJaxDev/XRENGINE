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
    private static IRuntimeModelSceneLoadingServices _current = Uninstalled;

    public static IRuntimeModelSceneLoadingServices Current => Volatile.Read(ref _current);

    public static bool IsInstalled => !ReferenceEquals(Current, Uninstalled);

    public static IDisposable Install(IRuntimeModelSceneLoadingServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IRuntimeModelSceneLoadingServices previous = Interlocked.Exchange(ref _current, services);
        return new InstallationLease(services, previous);
    }

    private sealed class InstallationLease(
        IRuntimeModelSceneLoadingServices installed,
        IRuntimeModelSceneLoadingServices previous) : IDisposable
    {
        private IRuntimeModelSceneLoadingServices? _installed = installed;

        public void Dispose()
        {
            IRuntimeModelSceneLoadingServices? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
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
