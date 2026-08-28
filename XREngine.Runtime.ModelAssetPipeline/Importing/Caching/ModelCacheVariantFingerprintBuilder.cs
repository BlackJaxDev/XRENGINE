using System.Security.Cryptography;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Computes the deterministic semantic variant selected before model-cache lookup.
/// </summary>
public static class ModelCacheVariantFingerprintBuilder
{
    public static ModelCacheVariantFingerprint Compute(
        string sourceFilePath,
        ModelImportOptions importOptions,
        ModelImportBackendResolution backendResolution,
        ModelCookOverrideSnapshot? cookOverrides = null,
        string? callerVariantKey = null,
        string? engineBuildIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentNullException.ThrowIfNull(importOptions);
        ArgumentNullException.ThrowIfNull(backendResolution);

        cookOverrides ??= ModelCookOverrideSnapshot.Empty;
        byte[] importSettings = ModelImportCanonicalSettings.Serialize(importOptions, sourceFilePath);
        byte[] cookSettings = ModelCookCanonicalSettings.Serialize(importOptions.CookSettings);

        using ModelCacheCanonicalWriter writer = new();
        writer.WriteString(1, "xrengine.model-cache-variant");
        writer.WriteUInt32(2, ModelBinaryCacheVersions.VariantFingerprint);
        writer.WriteUInt32(10, ModelBinaryCacheVersions.Schema);
        writer.WriteUInt32(11, ModelBinaryCacheVersions.Payload);
        writer.WriteUInt32(12, ModelBinaryCacheVersions.ContainerCodec);
        writer.WriteBytes(13, SerializeChunkVersions());
        writer.WriteUInt32(14, ModelBinaryCacheVersions.HashingPolicy);
        writer.WriteUInt32(15, ModelBinaryCacheVersions.SourceIdentityPolicy);
        writer.WriteUInt32(16, ModelBinaryCacheVersions.CachePathPolicy);
        writer.WriteUInt32(20, backendResolution.ResolverPolicyVersion);
        writer.WriteString(21, backendResolution.SourceExtension);
        writer.WriteInt32(22, (int)backendResolution.RequestedPolicy);
        writer.WriteInt32(23, (int)backendResolution.HostPreference);
        writer.WriteString(24, backendResolution.CandidateListHash);
        writer.WriteBytes(30, importSettings);
        writer.WriteBytes(31, cookSettings);
        writer.WriteString(32, cookOverrides.Hash);
        writer.WriteString(33, string.IsNullOrWhiteSpace(callerVariantKey) ? null : callerVariantKey);

        byte[] canonicalBytes = writer.ToArray();
        byte[] digest = SHA256.HashData(canonicalBytes);
        string fullHash = Convert.ToHexString(digest).ToLowerInvariant();
        string value = Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
        return new ModelCacheVariantFingerprint(
            value,
            fullHash,
            canonicalBytes,
            string.IsNullOrWhiteSpace(engineBuildIdentity) ? null : engineBuildIdentity);
    }

    private static byte[] SerializeChunkVersions()
    {
        using ModelCacheCanonicalWriter writer = new();
        writer.WriteUInt32(1, ModelBinaryChunkVersions.Dependencies);
        writer.WriteUInt32(2, ModelBinaryChunkVersions.Manifest);
        writer.WriteUInt32(3, ModelBinaryChunkVersions.PrefabGraph);
        writer.WriteUInt32(4, ModelBinaryChunkVersions.ComponentDirectory);
        writer.WriteUInt32(5, ModelBinaryChunkVersions.ComponentPayloads);
        writer.WriteUInt32(6, ModelBinaryChunkVersions.Models);
        writer.WriteUInt32(7, ModelBinaryChunkVersions.SubMeshes);
        writer.WriteUInt32(8, ModelBinaryChunkVersions.MeshDirectory);
        writer.WriteUInt32(9, ModelBinaryChunkVersions.MeshCoreStreams);
        writer.WriteUInt32(10, ModelBinaryChunkVersions.Skinning);
        writer.WriteUInt32(11, ModelBinaryChunkVersions.Skeletons);
        writer.WriteUInt32(12, ModelBinaryChunkVersions.MorphTargets);
        writer.WriteUInt32(13, ModelBinaryChunkVersions.LodTables);
        writer.WriteUInt32(14, ModelBinaryChunkVersions.Meshlets);
        writer.WriteUInt32(15, ModelBinaryChunkVersions.Materials);
        writer.WriteUInt32(16, ModelBinaryChunkVersions.TextureReferences);
        writer.WriteUInt32(17, ModelBinaryChunkVersions.AnimationReferences);
        writer.WriteUInt32(18, ModelBinaryChunkVersions.ImportedEntityTable);
        writer.WriteUInt32(19, ModelBinaryChunkVersions.ColliderHints);
        writer.WriteUInt32(20, ModelBinaryChunkVersions.Diagnostics);
        return writer.ToArray();
    }
}
