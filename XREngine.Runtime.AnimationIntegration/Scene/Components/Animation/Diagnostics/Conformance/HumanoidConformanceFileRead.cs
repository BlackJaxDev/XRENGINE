using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.Components.Animation;

/// <summary>Retains one admitted host file owner for a conformance operation.</summary>
internal sealed class HumanoidConformanceFileRead : IDisposable
{
    private readonly RuntimeAssetReadLease _read;
    private readonly IRuntimeFileDiscovery? _discovery;
    private readonly long _generation;
    private readonly IRuntimeHostFileReadBackend _files;
    private readonly IAssetMetadataFileBackend? _metadata;

    public HumanoidConformanceFileRead(string operation)
    {
        _read = RuntimeAssetReadServices.Capture();
        try
        {
            _read.EnsureHostFileAccess(operation);
            _discovery = RuntimeFileDiscoveryServices.Capture(out _generation);
            _files = _discovery as IRuntimeHostFileReadBackend
                ?? throw new NotSupportedException("AssetSource.HostFileReadUnavailable: the installed file system does not provide direct host-file reads.");
            _metadata = _discovery as IAssetMetadataFileBackend;
            EnsureCurrent();
        }
        catch
        {
            _read.Dispose();
            throw;
        }
    }

    public bool FileExists(string path)
        => RuntimeFileDiscoveryServices.HostFileExists(_read, path, "Humanoid conformance read", _discovery, _generation);

    public bool DirectoryExists(string path)
    {
        EnsureCurrent();
        bool exists = (_metadata ?? throw new NotSupportedException(
            "AssetSource.MetadataFileUnavailable: the installed asset file system does not provide directory probes.")).DirectoryExists(path);
        EnsureCurrent();
        return exists;
    }

    public Stream OpenRead(string path)
    {
        EnsureCurrent();
        Stream stream = _files.OpenRead(path, FileShare.Read);
        try
        {
            EnsureCurrent();
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public IDisposable BeginPublication()
    {
        IDisposable hostPublication = RuntimeSourcePublicationServices.BeginPublication(RuntimeSourcePublicationServices.Generation);
        IDisposable? readPublication = null;
        try
        {
            readPublication = _read.BeginPublication();
            EnsureCurrent();
            return new HumanoidConformancePublication(readPublication, hostPublication);
        }
        catch
        {
            readPublication?.Dispose();
            hostPublication.Dispose();
            throw;
        }
    }

    public void EnsureCurrent()
    {
        _read.EnsureCurrent();
        if (!RuntimeFileDiscoveryServices.IsCurrent(_discovery, _generation))
            throw new OperationCanceledException("AssetSource.StaleFileDiscovery: the conformance file provider changed during the read.");
    }

    public void Dispose() => _read.Dispose();
}
