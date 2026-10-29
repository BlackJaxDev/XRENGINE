using XREngine.Core.Files;
using XREngine.Core.Files.Caching;
using XREngine.Data;

namespace XREngine.Animation;

/// <summary>Animation-owned cooked payload cache codec.</summary>
internal sealed class AnimationClipBinaryCacheCodec : IThirdPartyCacheCodec, IHostFileCacheCodec
{
    public CacheCodecOwnership GetOwnership(Type assetType)
        => assetType == typeof(AnimationClip)
            ? CacheCodecOwnership.Cooperative
            : CacheCodecOwnership.NotHandled;

    public CacheWriteMode WriteMode => CacheWriteMode.Background;

    public string? ResolveDefaultVariantKey(string? explicitVariantKey)
        => explicitVariantKey;

    public XRAsset PrepareForWrite(string cachePath, XRAsset asset)
        => asset;

    public CacheReadResult Read(string cachePath, string originalPath, DateTime sourceTimestampUtc)
    {
        try
        {
            return Read(cachePath, originalPath, sourceTimestampUtc, CaptureFiles());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CacheReadResult.Rejected(CacheRejectReason.Unreadable, exception.Message);
        }
    }

    public CacheReadResult Read(string cachePath, string originalPath, DateTime sourceTimestampUtc, IHostBinaryCacheFileBackend files)
    {
        try
        {
            RuntimeAssetReadServices.EnsureHostFileAccess("Animation cache read");
            byte[] bytes = files.ReadAllBytes(cachePath);
            if (!PublishedCookedAssetRegistry.TryDeserialize(typeof(AnimationClip), bytes, out object? value)
                || value is not AnimationClip clip)
            {
                return CacheReadResult.Miss();
            }

            if (clip.OriginalLastWriteTimeUtc is null
                || clip.OriginalLastWriteTimeUtc.Value < sourceTimestampUtc)
            {
                return CacheReadResult.Miss();
            }

            clip.OriginalPath = originalPath;
            return CacheReadResult.Hit(clip);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CacheReadResult.Rejected(CacheRejectReason.Unreadable, exception.Message);
        }
    }

    public CacheWriteResult Write(string cachePath, XRAsset cacheAsset, XRAsset originalAsset)
        => Write(cachePath, cacheAsset, originalAsset, CaptureFiles());

    public CacheWriteResult Write(string cachePath, XRAsset cacheAsset, XRAsset originalAsset, IHostBinaryCacheFileBackend files)
    {
        RuntimeAssetReadServices.EnsureHostFileAccess("Animation cache write");
        if (cacheAsset is not AnimationClip clip
            || !PublishedCookedAssetRegistry.TrySerialize(clip, out byte[] payload))
        {
            return CacheWriteResult.Failed(
                CacheRejectReason.SerializationFailed,
                "The animation clip could not be serialized as a cooked binary payload.");
        }

        files.WriteAllBytesAtomic(cachePath, payload);
        return CacheWriteResult.Written();
    }

    public bool IsIncomplete(XRAsset asset, string sourceExtension)
        => asset is AnimationClip
        {
            HasRootMotion: true,
            ImportedHumanoidRootMotionSettings: null
        }
        && string.Equals(sourceExtension, "anim", StringComparison.OrdinalIgnoreCase);

    private static IHostBinaryCacheFileBackend CaptureFiles()
    {
        RuntimeAssetReadServices.EnsureHostFileAccess("Animation cache file access");
        return RuntimeFileDiscoveryServices.Required as IHostBinaryCacheFileBackend
            ?? throw new NotSupportedException("AssetSource.BinaryCacheUnavailable: the installed file system does not provide binary cache files.");
    }
}
