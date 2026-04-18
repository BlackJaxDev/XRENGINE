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
            if (mode == RemoteAssetLoadMode.None || Engine.Jobs.RemoteTransport?.IsConnected != true)
                return await LoadAsync<T>(filePath, priority).ConfigureAwait(false);

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
                response = await Engine.Jobs.ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset load failed for '{filePath}': {ex.Message}");
                return await LoadAsync<T>(filePath, priority).ConfigureAwait(false);
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

        private static bool ShouldAttemptRemoteAssetDownload()
        {
            if (Engine.Jobs.RemoteTransport?.IsConnected != true)
                return false;

            return Engine.Networking is ClientNetworkingManager or PeerToPeerNetworkingManager;
        }

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
                response = await Engine.Jobs.ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
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

            if (response.Metadata is not null && response.Metadata.TryGetValue("path", out var serverPath) && !string.IsNullOrWhiteSpace(serverPath))
                filePath = serverPath;

            try
            {
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

        private async Task<bool> TryDownloadAssetFromRemoteByIdAsync(Guid assetId, Type assetType, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null)
        {
            if (assetId == Guid.Empty || !ShouldAttemptRemoteAssetDownload())
                return false;

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
                response = await Engine.Jobs.ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset download failed for id '{assetId}': {ex.Message}");
                return false;
            }

            if (!response.Success)
            {
                Debug.LogWarning($"Remote asset download failed for id '{assetId}': {response.Error ?? "Unknown error"}");
                return false;
            }

            if (response.Payload is null || response.Payload.Length == 0)
            {
                Debug.LogWarning($"Remote asset download returned no data for id '{assetId}'.");
                return false;
            }

            string? targetPath = null;
            if (response.Metadata is not null && response.Metadata.TryGetValue("path", out var serverPath) && !string.IsNullOrWhiteSpace(serverPath))
            {
                targetPath = serverPath;
            }
            else if (TryResolveAssetPathById(assetId, out var resolvedPath))
            {
                targetPath = resolvedPath;
            }
            else
            {
                targetPath = Path.Combine(GameAssetsPath, $"{assetId:D}.{AssetExtension}");
            }

            try
            {
                string? directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllBytesAsync(targetPath, response.Payload, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to persist remote asset '{assetId}' to '{targetPath}': {ex.Message}");
                return false;
            }
        }
    }
}