using XREngine.Core.Files;
using XREngine.Core.Files.Caching;

namespace XREngine.Animation;

/// <summary>Animation-owned cooked payload cache codec.</summary>
internal sealed class AnimationClipBinaryCacheCodec : IThirdPartyCacheCodec
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
            byte[] bytes = File.ReadAllBytes(cachePath);
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
    {
        if (cacheAsset is not AnimationClip clip
            || !PublishedCookedAssetRegistry.TrySerialize(clip, out byte[] payload))
        {
            return CacheWriteResult.Failed(
                CacheRejectReason.SerializationFailed,
                "The animation clip could not be serialized as a cooked binary payload.");
        }

        WriteAllBytesAtomic(cachePath, payload);
        return CacheWriteResult.Written();
    }

    public bool IsIncomplete(XRAsset asset, string sourceExtension)
        => asset is AnimationClip
        {
            HasRootMotion: true,
            ImportedHumanoidRootMotionSettings: null
        }
        && string.Equals(sourceExtension, "anim", StringComparison.OrdinalIgnoreCase);

    private static void WriteAllBytesAtomic(string filePath, byte[] bytes)
    {
        string tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }
}
