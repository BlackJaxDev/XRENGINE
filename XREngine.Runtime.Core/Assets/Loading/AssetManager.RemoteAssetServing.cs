using XREngine.Core.Files;

namespace XREngine;

public partial class AssetManager
{
    /// <summary>Reads a remote request through this owner's captured source and reserves response publication.</summary>
    internal async Task<RemoteJobResponse?> ServeRemoteAssetAsync(
        Guid assetId,
        string? requestedPath,
        Func<byte[], string, RemoteJobResponse> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publish);

        IRuntimeAssetSource? source;
        int epoch;
        CancellationTokenSource lifetime;
        lock (_runtimePublicationGate)
        {
            source = _runtimeAssetSource;
            epoch = _runtimeSourceEpoch;
            EnsureRemoteAssetServingOwnerCurrent(source, epoch);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runtimeSourceLifetime.Token);
        }

        using (lifetime)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            string? path = null;

            // Existence and metadata-root lookup are synchronous. Keep their owner
            // stable without holding the gate through source callbacks or file probes.
            lock (_runtimePublicationGate)
            {
                EnsureRemoteAssetServingOwnerCurrent(source, epoch);
                _remoteAssetPublications++;
            }
            try
            {
                if (source is IRuntimeAssetCatalog catalog)
                {
                    if (assetId != Guid.Empty)
                    {
                        if (TryGetAssetByID(assetId, out XRAsset? asset) && !asset.IsDestroyed)
                            path = asset.FilePath;
                    }
                    else
                        path = requestedPath;

                    if (string.IsNullOrWhiteSpace(path))
                        return null;
                    path = Path.GetFullPath(path);
                    if (!catalog.TryGetAsset(path, out _))
                        return null;
                }
                else
                {
                    // Only a synchronous host-file owner may use file existence and metadata-root probes.
                    // An asynchronous source discovers a missing path from its read result.
                    if (source is null || source.SupportsSynchronousReads)
                        EnsureHostFileAssetAccess();

                    if (assetId != Guid.Empty)
                    {
                        if (TryGetAssetByID(assetId, out XRAsset? asset) && !asset.IsDestroyed
                            && !string.IsNullOrWhiteSpace(asset.FilePath)
                            && (!SupportsSynchronousAssetWork || (source?.Exists(asset.FilePath) ?? File.Exists(asset.FilePath))))
                            path = asset.FilePath;
                        else if (SupportsSynchronousAssetWork && TryResolveAssetPathById(assetId, out string? resolved)
                            && !string.IsNullOrWhiteSpace(resolved)
                            && (source?.Exists(resolved) ?? File.Exists(resolved)))
                            path = resolved;
                    }
                    else if (!string.IsNullOrWhiteSpace(requestedPath)
                        && (!SupportsSynchronousAssetWork || (source?.Exists(requestedPath) ?? File.Exists(requestedPath))))
                        path = requestedPath;

                    if (string.IsNullOrWhiteSpace(path))
                        return null;
                }
            }
            finally
            {
                lock (_runtimePublicationGate)
                    _remoteAssetPublications--;
            }

            lock (_runtimePublicationGate)
                EnsureRemoteAssetServingOwnerCurrent(source, epoch);
            byte[] payload;
            try
            {
                payload = source is null
                    ? await File.ReadAllBytesAsync(path, lifetime.Token).ConfigureAwait(false)
                    : await source.ReadAllBytesAsync(path, lifetime.Token).ConfigureAwait(false);
            }
            catch (FileNotFoundException) when (source is not IRuntimeAssetCatalog && source is { SupportsSynchronousReads: false })
            {
                lock (_runtimePublicationGate)
                    EnsureRemoteAssetServingOwnerCurrent(source, epoch);
                return null;
            }
            catch (DirectoryNotFoundException) when (source is not IRuntimeAssetCatalog && source is { SupportsSynchronousReads: false })
            {
                lock (_runtimePublicationGate)
                    EnsureRemoteAssetServingOwnerCurrent(source, epoch);
                return null;
            }

            lock (_runtimePublicationGate)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                EnsureRemoteAssetServingOwnerCurrent(source, epoch);
                _remoteAssetPublications++;
            }
            try
            {
                return publish(payload, path);
            }
            finally
            {
                lock (_runtimePublicationGate)
                    _remoteAssetPublications--;
            }
        }
    }

    private void EnsureRemoteAssetServingOwnerCurrent(IRuntimeAssetSource? source, int epoch)
    {
        if (_runtimeSourceTeardown || _runtimeSourceUnbinding || _runtimeSourceDisposing
            || _remoteAssetDisposalInProgress || !ReferenceEquals(source, _runtimeAssetSource)
            || epoch != _runtimeSourceEpoch)
            throw new OperationCanceledException("AssetSource.StaleSession: the remote asset request belongs to a retired content owner.");
    }
}
