using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Networking;
using XREngine.Scene;

namespace XREngine.Server.Instances
{
    /// <summary>
    /// Resolves and downloads world payloads from remote storage (Azure blob, S3, or direct URLs) and deserializes them into XRWorld assets.
    /// </summary>
    internal sealed class WorldDownloadService
    {
        private readonly HttpClient _httpClient;
        private readonly string _cacheRoot;

        public WorldDownloadService(HttpClient? httpClient = null, string? cacheRoot = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _cacheRoot = cacheRoot ?? Path.Combine(Path.GetTempPath(), "xre", "world-cache");
            Directory.CreateDirectory(_cacheRoot);
        }

        public async Task<XRWorld> FetchWorldAsync(WorldLocator locator, Guid? instanceId = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(locator);

            string cachePath = GetCachePath(locator, instanceId);
            if (!File.Exists(cachePath))
            {
                Uri downloadUri = ResolveUri(locator);
                using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var fs = File.Open(cachePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            byte[] bytes = await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return BuildPlaceholder(locator);

            try
            {
#pragma warning disable IL2026, IL3050
                var world = CookedBinarySerializer.Deserialize(typeof(XRWorld), bytes) as XRWorld;
#pragma warning restore IL2026, IL3050
                if (world is not null)
                {
                    if (!string.IsNullOrWhiteSpace(locator.Name))
                        world.Name = locator.Name!;
                    return world;
                }
            }
            catch
            {
                // Fall back to placeholder; errors are expected when the payload is not a cooked world asset.
            }

            return BuildPlaceholder(locator);
        }

        private static XRWorld BuildPlaceholder(WorldLocator locator)
        {
            XRWorld world = new()
            {
                Name = string.IsNullOrWhiteSpace(locator.Name) ? $"World-{locator.WorldId}" : locator.Name!
            };
            XRScene scene = new(world.Name);
            world.Scenes.Add(scene);
            return world;
        }

        private string GetCachePath(WorldLocator locator, Guid? instanceId)
        {
            string cacheKey = locator.WorldId != Guid.Empty
                ? locator.WorldId.ToString("N")
                : instanceId.HasValue && instanceId.Value != Guid.Empty
                    ? instanceId.Value.ToString("N")
                    : ResolveCacheKeyFromUri(locator);

            string fileName = $"{cacheKey}.bin";
            return Path.Combine(_cacheRoot, fileName);
        }

        private static string ResolveCacheKeyFromUri(WorldLocator locator)
        {
            if (!string.IsNullOrWhiteSpace(locator.DownloadUri))
                return HashString(locator.DownloadUri);

            var uri = ResolveUri(locator);
            return HashString(uri.AbsoluteUri);
        }

        private static string HashString(string value)
        {
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static Uri ResolveUri(WorldLocator locator)
        {
            if (!string.IsNullOrWhiteSpace(locator.DownloadUri) && Uri.TryCreate(locator.DownloadUri, UriKind.Absolute, out var absolute))
                return absolute;

            var provider = locator.Provider?.ToLowerInvariant() ?? "direct";
            var objectPath = locator.ObjectPath?.TrimStart('/') ?? string.Empty;
            var container = locator.ContainerOrBucket?.TrimEnd('/') ?? string.Empty;
            var token = string.IsNullOrWhiteSpace(locator.AccessToken) ? null : locator.AccessToken;

            static Uri BuildUri(string baseAddress, string path, string? token)
            {
                var builder = new StringBuilder();
                builder.Append(baseAddress.TrimEnd('/'));
                if (!string.IsNullOrEmpty(path))
                {
                    builder.Append('/');
                    builder.Append(path);
                }
                if (!string.IsNullOrWhiteSpace(token))
                {
                    builder.Append(path.Contains('?') ? '&' : '?');
                    builder.Append(token.TrimStart('?'));
                }
                return new Uri(builder.ToString());
            }

            return provider switch
            {
                "azure" when !string.IsNullOrWhiteSpace(container) && !string.IsNullOrWhiteSpace(objectPath)
                    => BuildUri($"https://{container}.blob.core.windows.net", objectPath, token),
                "s3" when !string.IsNullOrWhiteSpace(container) && !string.IsNullOrWhiteSpace(objectPath)
                    => BuildUri($"https://{container}.s3.amazonaws.com", objectPath, token),
                _ when !string.IsNullOrWhiteSpace(container) && !string.IsNullOrWhiteSpace(objectPath)
                    => BuildUri(container, objectPath, token),
                _ => throw new InvalidOperationException("World locator did not provide a resolvable download URI.")
            };
        }
    }
}
