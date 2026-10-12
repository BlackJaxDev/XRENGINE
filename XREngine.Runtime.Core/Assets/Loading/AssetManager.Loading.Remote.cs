using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XRAsset = XREngine.Core.Files.XRAsset;

namespace XREngine
{
    public partial class AssetManager
    {
        private int _remoteAssetPublications;
        private bool _remoteAssetDisposalInProgress;
        private readonly List<ObjectCacheOwnership> _failedRemoteAssetOwnership = [];

        private async Task<T?> LoadAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, RemoteAssetLoadMode mode, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null) where T : XRAsset, new()
        {
            if (UsesRuntimeAssetCatalog)
            {
                if (mode != RemoteAssetLoadMode.None)
                    throw new NotSupportedException("AssetSource.RemoteJobUnavailable: published catalog assets do not use authoring remote jobs; load the catalog path asynchronously with RemoteAssetLoadMode.None.");
                return (T?)await LoadFromRuntimeSourceAsync(filePath, typeof(T), cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (mode == RemoteAssetLoadMode.SendLocalCopy)
                EnsureHostFileAssetAccess();

            IRuntimeAssetSource? ownerSource = _runtimeAssetSource;
            int ownerEpoch = RuntimeSourceEpoch;
            EnsureRemoteAssetResponseOwnerCurrent(ownerSource, ownerEpoch);

            if (mode == RemoteAssetLoadMode.None || _jobManagerProvider().RemoteTransport?.IsConnected != true)
                return await LoadLocalOnlyAsync<T>(filePath, priority, cancellationToken).ConfigureAwait(false);

            var metadata = additionalMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);

            metadata["path"] = filePath;
            metadata["type"] = typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name;

            byte[]? payload = null;
            var transferMode = RemoteJobTransferMode.RequestFromRemote;

            if (mode == RemoteAssetLoadMode.SendLocalCopy)
            {
                EnsureHostFileAssetAccess();
                transferMode = RemoteJobTransferMode.PushDataToRemote;
                if (File.Exists(filePath))
                    payload = await DirectStorageIO.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            }

            var request = new RemoteJobRequest
            {
                Operation = RemoteJobRequest.Operations.AssetLoad,
                TransferMode = transferMode,
                Payload = payload,
                Metadata = metadata,
            };

            EnsureRemoteAssetResponseOwnerCurrent(ownerSource, ownerEpoch);
            RemoteJobResponse response;
            try
            {
                response = await _jobManagerProvider().ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EnsureRemoteAssetResponseOwnerCurrent(ownerSource, ownerEpoch);
                Debug.LogWarning($"Remote asset load failed for '{filePath}': {ex.Message}");
                return await LoadLocalOnlyAsync<T>(filePath, priority, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureRemoteAssetResponseOwnerCurrent(ownerSource, ownerEpoch);
            if (!response.Success)
            {
                Debug.LogWarning($"Remote asset load failed for '{filePath}': {response.Error ?? "Unknown error"}");
                return null;
            }

            if (response.Payload is null || response.Payload.Length == 0)
            {
                Debug.LogWarning($"Remote asset load returned no data for '{filePath}'.");
                return null;
            }

            string contents = Encoding.UTF8.GetString(response.Payload);
            using var scope = AssetDeserializationContext.Push(filePath);
            using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            T? asset = Deserializer.Deserialize<T>(contents);
            if (asset is null)
                return null;

            // Reserve the owner, not its monitor. Constructors, property handlers, object
            // publication and AssetLoaded callbacks must be able to run on other threads.
            ObjectCacheOwnership? ownership = null;
            bool ownsRoot = false;
            List<XRAsset> pathCacheOwners = [];
            lock (_runtimePublicationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureRemoteAssetResponseOwnerCurrent(ownerSource, ownerEpoch);
                _remoteAssetPublications++;
            }
            try
            {
                ownership = publication.CompleteWithOwnership();
                foreach (XRObjectBase value in ownership.Objects)
                    if (ReferenceEquals(value, asset))
                    {
                        ownsRoot = true;
                        break;
                    }
                if (!ownsRoot || asset.IsDestroyed)
                    throw new InvalidDataException("AssetSource.InvalidRemoteRoot: the remote response must create a live, independently owned asset root.");
                using ObjectCachePublicationScope postLoadPublication = XRObjectBase.BeginIndependentObjectCachePublication();
                PostLoaded(filePath, asset, ownership, pathCacheOwners);
                postLoadPublication.CompleteWithOwnership();
                return asset;
            }
            catch (Exception error)
            {
                if (ownsRoot)
                {
                    asset.PropertyChanged -= AssetPropertyChanged;
                    foreach (var pair in LoadedAssetsByPathInternal)
                        if (ReferenceEquals(pair.Value, asset))
                            LoadedAssetsByPathInternal.TryRemove(pair);
                    foreach (var pair in LoadedAssetsByOriginalPathInternal)
                        if (ReferenceEquals(pair.Value, asset))
                            LoadedAssetsByOriginalPathInternal.TryRemove(pair);
                    foreach (var pair in LoadedAssetsByIDInternal)
                        if (ReferenceEquals(pair.Value, asset))
                            LoadedAssetsByIDInternal.TryRemove(pair);
                    foreach (var pair in DirtyAssets)
                        if (ReferenceEquals(pair.Value, asset))
                            DirtyAssets.TryRemove(pair);
                }

                List<Exception>? cleanupFailures = null;
                foreach (XRAsset pathOwner in pathCacheOwners)
                {
                    try { pathOwner.EmbeddedAssets.Remove(asset, reportRemoved: false, reportModified: false); }
                    catch (Exception cleanupError) { (cleanupFailures ??= []).Add(cleanupError); }
                }
                if (ownership is not null)
                {
                    try { ownership.Dispose(); }
                    catch (Exception cleanupError)
                    {
                        lock (_runtimePublicationGate)
                            _failedRemoteAssetOwnership.Add(ownership);
                        (cleanupFailures ??= []).Add(cleanupError);
                    }
                }
                if (cleanupFailures is not null)
                    throw new AggregateException([error, .. cleanupFailures]);
                throw;
            }
            finally
            {
                lock (_runtimePublicationGate)
                    _remoteAssetPublications--;
            }
        }

        // Called only under the publication gate. Lifecycle callers may retry after the
        // synchronous publication or disposal finishes; waiting here could deadlock callbacks.
        private void RejectRemoteAssetLifecycleOverlap()
        {
            if (_remoteAssetPublications != 0)
                throw new InvalidOperationException("AssetSource.PublicationPending: a remote asset is being published; retry after its callbacks complete.");
            if (_remoteAssetDisposalInProgress)
                throw new InvalidOperationException("AssetSource.TeardownPending: remote asset cleanup is in progress; retry after disposal completes.");
        }

        private void DisposeFailedRemoteAssets()
        {
            List<Exception>? failures = null;
            for (int index = _failedRemoteAssetOwnership.Count - 1; index >= 0; index--)
            {
                try
                {
                    _failedRemoteAssetOwnership[index].Dispose();
                    _failedRemoteAssetOwnership.RemoveAt(index);
                }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null)
                throw new AggregateException("AssetSource.TeardownFailed: remote asset allocations could not all be released.", failures);
        }

        private void EnsureRemoteAssetResponseOwnerCurrent(IRuntimeAssetSource? source, int epoch)
        {
            if (_runtimeSourceTeardown || _runtimeSourceUnbinding || _runtimeSourceDisposing
                || _remoteAssetDisposalInProgress || UsesRuntimeAssetCatalog
                || !ReferenceEquals(source, _runtimeAssetSource)
                || RuntimeSourceEpoch != epoch)
                throw new OperationCanceledException("AssetSource.StaleSession: the remote asset response belongs to a retired content owner.");
        }

        private Task<T?> LoadLocalOnlyAsync<
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
            string filePath,
            JobPriority priority,
            CancellationToken cancellationToken)
            where T : XRAsset, new()
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureHostFileAssetAccess();
            return RunOnJobThreadAsync(() => LoadCore<T>(filePath), priority);
        }

        private bool ShouldAttemptRemoteAssetDownload()
            => _remoteAssetDownloadAllowedProvider()
                && _jobManagerProvider().RemoteTransport?.IsConnected == true;

        private async Task<bool> TryDownloadAssetFromRemoteAsync(string filePath, Type assetType, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null)
        {
            if (!ShouldAttemptRemoteAssetDownload())
                return false;

            EnsureHostFileAssetAccess();

            var metadata = additionalMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);

            metadata["path"] = filePath;
            metadata["type"] = assetType.AssemblyQualifiedName ?? assetType.FullName ?? assetType.Name;

            var request = new RemoteJobRequest
            {
                Operation = RemoteJobRequest.Operations.AssetLoad,
                TransferMode = RemoteJobTransferMode.RequestFromRemote,
                Metadata = metadata,
            };

            RemoteJobResponse response;
            try
            {
                response = await _jobManagerProvider().ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset download failed for '{filePath}': {ex.Message}");
                return false;
            }

            if (!response.Success)
            {
                Debug.LogWarning($"Remote asset download failed for '{filePath}': {response.Error ?? "Unknown error"}");
                return false;
            }

            if (response.Payload is null || response.Payload.Length == 0)
            {
                Debug.LogWarning($"Remote asset download returned no data for '{filePath}'.");
                return false;
            }

            // Owner retirement is a route failure, not a recoverable disk-write failure.
            // Propagate it before callers can continue into a host-file load job.
            EnsureHostFileAssetAccess();
            try
            {
                filePath = Path.GetFullPath(filePath);
                string? directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllBytesAsync(filePath, response.Payload, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to persist remote asset '{filePath}': {ex.Message}");
                return false;
            }
        }

        private async Task<string?> TryDownloadAssetFromRemoteByIdAsync(Guid assetId, Type assetType, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null)
        {
            if (assetId == Guid.Empty || !ShouldAttemptRemoteAssetDownload())
                return null;

            EnsureHostFileAssetAccess();

            var metadata = additionalMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);

            metadata["id"] = assetId.ToString("D");
            metadata["type"] = assetType.AssemblyQualifiedName ?? assetType.FullName ?? assetType.Name;

            var request = new RemoteJobRequest
            {
                Operation = RemoteJobRequest.Operations.AssetLoad,
                TransferMode = RemoteJobTransferMode.RequestFromRemote,
                Metadata = metadata,
            };

            RemoteJobResponse response;
            try
            {
                response = await _jobManagerProvider().ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset download failed for id '{assetId}': {ex.Message}");
                return null;
            }

            if (!response.Success)
            {
                Debug.LogWarning($"Remote asset download failed for id '{assetId}': {response.Error ?? "Unknown error"}");
                return null;
            }

            if (response.Payload is null || response.Payload.Length == 0)
            {
                Debug.LogWarning($"Remote asset download returned no data for id '{assetId}'.");
                return null;
            }

            EnsureHostFileAssetAccess();
            string targetPath = TryResolveAssetPathById(assetId, out string? resolvedPath)
                ? resolvedPath
                : Path.Combine(GameAssetsPath, $"{assetId:D}.{AssetExtension}");
            targetPath = Path.GetFullPath(targetPath);

            try
            {
                string? directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllBytesAsync(targetPath, response.Payload, cancellationToken).ConfigureAwait(false);
                return targetPath;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to persist remote asset '{assetId}' to '{targetPath}': {ex.Message}");
                return null;
            }
        }
    }
}
