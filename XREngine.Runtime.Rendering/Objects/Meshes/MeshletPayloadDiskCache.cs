using System;
using System.IO;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering;

/// <summary>
/// Disk-level cache for runtime-built <see cref="MeshletPayload"/>s, keyed by
/// the payload's <see cref="MeshletPayload.FreshnessHash"/> (mesh identity +
/// source-mesh hash + meshlet settings hash + lod settings hash +
/// meshoptimizer version key).
/// <para>
/// Cache path: <c>{CacheRoot}/Meshlets/v1/{hex[0:2]}/{hex16}.bin</c>.
/// </para>
/// <para>
/// On-disk byte layout reuses the cooked-mesh meshlet writer
/// (<see cref="XRMesh.SerializeMeshletPayloadToBytes"/>) so the runtime cache
/// and cooked assets stay in lockstep.
/// </para>
/// </summary>
internal static class MeshletPayloadDiskCache
{
    private const int FileVersion = 1;
    private const string CacheFolderName = "Meshlets";
    private const string CacheVersionFolderName = "v1";

    /// <summary>
    /// Optional override for the cache root. When null, falls back to
    /// <see cref="RuntimeRenderingHostServices.GameCachePath"/>.
    /// </summary>
    internal static string? CacheRootOverride { get; set; }

    /// <summary>
    /// Attempts to load a previously cached meshlet payload for the supplied
    /// mesh + settings. The cache key is the freshness hash of the prospective
    /// payload; on hit the loaded payload's recorded freshness hash is
    /// verified to match before it is returned.
    /// </summary>
    public static bool TryLoad(
        XRMesh mesh,
        MeshletGenerationSettings? meshletSettings,
        MeshLodGenerationSettings? lodSettings,
        out MeshletPayload? payload)
    {
        payload = null;

        if (!TryResolveCacheFilePath(mesh, meshletSettings, lodSettings, out string? cachePath, out ulong expectedFreshness))
            return false;

        if (!File.Exists(cachePath))
            return false;

        try
        {
            byte[] bytes = File.ReadAllBytes(cachePath);
            payload = XRMesh.DeserializeMeshletPayloadFromBytes(bytes);
            if (payload is null)
                return false;

            if (payload.FreshnessHash != expectedFreshness)
            {
                payload = null;
                TryDeleteStale(cachePath);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or InvalidOperationException)
        {
            XREngine.Debug.MeshesWarning($"[Meshlet Cache] Failed to load '{cachePath}'. {ex.Message}");
            payload = null;
            TryDeleteStale(cachePath);
            return false;
        }
    }

    /// <summary>
    /// Persists a freshly built meshlet payload to disk. Best-effort: IO
    /// failures are logged and swallowed.
    /// </summary>
    public static void TryStore(
        XRMesh mesh,
        MeshletGenerationSettings? meshletSettings,
        MeshLodGenerationSettings? lodSettings,
        MeshletPayload payload)
    {
        if (payload is null)
            return;

        if (!TryResolveCacheFilePath(mesh, meshletSettings, lodSettings, out string? cachePath, out ulong expectedFreshness))
            return;

        // Only persist payloads whose freshness hash matches the prospective key.
        // A mismatch indicates settings drift between caller and payload; storing
        // would create an unreachable cache entry.
        if (payload.FreshnessHash != expectedFreshness)
            return;

        try
        {
            string? directory = Path.GetDirectoryName(cachePath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);

            byte[] bytes = XRMesh.SerializeMeshletPayloadToBytes(payload);

            string tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tempPath, bytes);

            if (File.Exists(cachePath))
                File.Delete(cachePath);

            File.Move(tempPath, cachePath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            XREngine.Debug.MeshesWarning($"[Meshlet Cache] Failed to store '{cachePath}'. {ex.Message}");
        }
    }

    private static bool TryResolveCacheFilePath(
        XRMesh mesh,
        MeshletGenerationSettings? meshletSettings,
        MeshLodGenerationSettings? lodSettings,
        out string? cachePath,
        out ulong freshness)
    {
        cachePath = null;
        freshness = 0;

        string? cacheRoot = CacheRootOverride ?? RuntimeRenderingHostServices.GameCachePath;
        if (string.IsNullOrWhiteSpace(cacheRoot))
            return false;

        try
        {
            freshness = ComputeProspectiveFreshness(mesh, meshletSettings, lodSettings);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            XREngine.Debug.MeshesWarning($"[Meshlet Cache] Failed to compute key for mesh '{mesh.Name}'. {ex.Message}");
            return false;
        }

        string hex = freshness.ToString("X16");
        cachePath = Path.Combine(cacheRoot, CacheFolderName, CacheVersionFolderName, hex[..2], $"{hex}.bin");
        return true;
    }

    private static ulong ComputeProspectiveFreshness(
        XRMesh mesh,
        MeshletGenerationSettings? meshletSettings,
        MeshLodGenerationSettings? lodSettings)
    {
        MeshletGenerationSettingsSnapshot meshletSnapshot = MeshletGenerationSettingsSnapshot.From(meshletSettings);
        MeshLodGenerationSettingsSnapshot lodSnapshot = MeshLodGenerationSettingsSnapshot.From(lodSettings);

        string identity = MeshletPayloadUtility.ResolveSourceMeshIdentity(mesh);
        ulong sourceHash = MeshletPayloadUtility.ComputeSourceMeshHash(mesh);
        ulong meshletHash = MeshletPayloadUtility.ComputeHash(meshletSnapshot);
        ulong lodHash = MeshletPayloadUtility.ComputeHash(lodSnapshot);
        string versionKey = MeshOptimizerIntegration.MeshOptimizerVersionKey;

        return MeshletPayloadUtility.ComputeFreshnessHash(identity, sourceHash, meshletHash, lodHash, versionKey);
    }

    private static void TryDeleteStale(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; ignore.
            _ = ex;
        }
    }

    /// <summary>
    /// Stable file-format version stamp. Bump if the wire layout changes in an
    /// incompatible way (currently delegated entirely to
    /// <see cref="MeshletPayload.CurrentPayloadVersion"/> via the serializer).
    /// </summary>
    public static int CacheFormatVersion => FileVersion;
}
