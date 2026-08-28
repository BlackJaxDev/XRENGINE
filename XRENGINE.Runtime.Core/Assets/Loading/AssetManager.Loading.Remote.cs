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
        private async Task<T?> LoadAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, RemoteAssetLoadMode mode, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null) where T : XRAsset, new()
        {
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

            RemoteJobResponse response;
            try
            {
                response = await _jobManagerProvider().ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset load failed for '{filePath}': {ex.Message}");
                return await LoadLocalOnlyAsync<T>(filePath, priority, cancellationToken).ConfigureAwait(false);
            }

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
            var asset = Deserializer.Deserialize<T>(contents);
            PostLoaded(filePath, asset);
            return asset;
        }

        private Task<T?> LoadLocalOnlyAsync<
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
            string filePath,
            JobPriority priority,
            CancellationToken cancellationToken)
            where T : XRAsset, new()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return RunOnJobThreadAsync(() => LoadCore<T>(filePath), priority);
        }

        private bool ShouldAttemptRemoteAssetDownload()
            => _remoteAssetDownloadAllowedProvider()
                && _jobManagerProvider().RemoteTransport?.IsConnected == true;

        private async Task<bool> TryDownloadAssetFromRemoteAsync(string filePath, Type assetType, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null)
        {
            if (!ShouldAttemptRemoteAssetDownload())
                return false;

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
