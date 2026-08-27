namespace XREngine;

/// <summary>Optional authoring reactions invoked by Runtime.Core file watchers.</summary>
public interface IRuntimeAssetAuthoringServices
{
    void QueueAutoImport(string sourcePath, string reason);
    void HandleSourceDeleted(string sourcePath);
    void HandleSourceRenamed(string oldSourcePath, string newSourcePath);
}

public static class RuntimeAssetAuthoringServices
{
    private static readonly IRuntimeAssetAuthoringServices Default = new UninstalledServices();
    private static IRuntimeAssetAuthoringServices _current = Default;

    public static IRuntimeAssetAuthoringServices Current => Volatile.Read(ref _current);

    public static IDisposable Install(IRuntimeAssetAuthoringServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IRuntimeAssetAuthoringServices previous = Interlocked.Exchange(ref _current, services);
        return new InstallationLease(services, previous);
    }

    private sealed class InstallationLease(
        IRuntimeAssetAuthoringServices installed,
        IRuntimeAssetAuthoringServices previous) : IDisposable
    {
        private IRuntimeAssetAuthoringServices? _installed = installed;

        public void Dispose()
        {
            IRuntimeAssetAuthoringServices? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
        }
    }

    private sealed class UninstalledServices : IRuntimeAssetAuthoringServices
    {
        public void QueueAutoImport(string sourcePath, string reason) { }
        public void HandleSourceDeleted(string sourcePath) { }
        public void HandleSourceRenamed(string oldSourcePath, string newSourcePath) { }
    }
}
