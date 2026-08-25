using XREngine.Core.Files;
using XREngine.Core.Files.Caching;

namespace XREngine.Rendering;

/// <summary>Rendering-owned codec for binary texture streaming cache payloads.</summary>
internal sealed class TextureStreamingCacheCodec : IThirdPartyCacheCodec
{
    public string? AuthorityRole => ThirdPartyCacheAuthorityRoles.TextureStreaming;

    public Type? AuthorityAssetType => typeof(XRTexture2D);

    public CacheCodecOwnership GetOwnership(Type assetType)
        => assetType == typeof(XRTexture2D)
            ? CacheCodecOwnership.Cooperative
            : CacheCodecOwnership.NotHandled;

    public CacheWriteMode WriteMode => CacheWriteMode.Blocking;

    public string? ResolveDefaultVariantKey(string? explicitVariantKey)
    {
        string payloadKey =
            $"TextureStreaming_v3_preview{XRTexture2D.ImportedPreviewMaxDimensionInternal}_rgba8_uncompressed_binary";
        return string.IsNullOrWhiteSpace(explicitVariantKey)
            ? payloadKey
            : Path.Combine(payloadKey, explicitVariantKey);
    }

    public XRAsset PrepareForWrite(string cachePath, XRAsset asset)
        => TryPrepareStreamingAsset(cachePath, asset, out XRAsset cacheAsset)
            ? cacheAsset
            : asset;

    public CacheReadResult Read(string cachePath, string originalPath, DateTime sourceTimestampUtc)
    {
        if (!LooksLikeBinaryPayload(cachePath))
            return CacheReadResult.Miss();

        DateTime cacheTimestampUtc = File.GetLastWriteTimeUtc(cachePath);
        if (cacheTimestampUtc < sourceTimestampUtc)
            return CacheReadResult.Miss();

        try
        {
            XRTexture2D texture = DeserializeBinaryAsset(cachePath);
            texture.OriginalPath = originalPath;
            texture.OriginalLastWriteTimeUtc = cacheTimestampUtc;
            return CacheReadResult.Hit(texture);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CacheReadResult.Rejected(CacheRejectReason.Unreadable, exception.Message);
        }
    }

    public CacheWriteResult Write(string cachePath, XRAsset cacheAsset, XRAsset originalAsset)
    {
        if (cacheAsset is not XRTexture2D texture || !HasStreamableShape(texture))
        {
            return CacheWriteResult.Failed(
                CacheRejectReason.SerializationFailed,
                "The texture is not in streaming-cache shape.");
        }

        DateTime sourceTimestampUtc = texture.OriginalLastWriteTimeUtc ?? DateTime.MinValue;
        if (sourceTimestampUtc == DateTime.MinValue && TryResolveSourcePath(texture, out string sourcePath))
            sourceTimestampUtc = File.GetLastWriteTimeUtc(sourcePath);

        return sourceTimestampUtc != DateTime.MinValue
            && XRTexture2D.WriteBinaryStreamingCacheFile(texture, cachePath, sourceTimestampUtc)
                ? CacheWriteResult.Written()
                : CacheWriteResult.Failed(
                    CacheRejectReason.SerializationFailed,
                    "The texture could not be serialized as a binary streaming payload.");
    }

    public bool IsCacheUsable(string cachePath)
        => XRTexture2D.IsTextureStreamingAssetUsable(cachePath);

    public bool TryReadDirectAssetFile(string filePath, Type assetType, out XRAsset? asset)
    {
        asset = null;
        if (assetType != typeof(XRTexture2D) || !LooksLikeBinaryPayload(filePath))
            return false;

        asset = DeserializeBinaryAsset(filePath);
        return true;
    }

    private static bool TryPrepareStreamingAsset(string cachePath, XRAsset asset, out XRAsset cacheAsset)
    {
        cacheAsset = asset;
        if (asset is not XRTexture2D texture || HasStreamableShape(texture))
            return false;
        if (!TryResolveSourcePath(texture, out string sourcePath))
            return false;

        DateTime sourceTimestampUtc = texture.OriginalLastWriteTimeUtc ?? File.GetLastWriteTimeUtc(sourcePath);
        if (sourceTimestampUtc == DateTime.MinValue
            || !XRTexture2D.TryCreateTextureStreamingCacheAsset(
                texture,
                sourcePath,
                cachePath,
                sourceTimestampUtc,
                out XRTexture2D streamingTexture))
        {
            return false;
        }

        cacheAsset = streamingTexture;
        return true;
    }

    private static bool LooksLikeBinaryPayload(string cachePath)
    {
        try
        {
            using FileStream stream = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int first = stream.ReadByte();
            return first >= 0
                && first <= (byte)RuntimeCookedBinaryTypeMarker.CustomObject
                && first is not (byte)'\t' and not (byte)'\n' and not (byte)'\r';
        }
        catch
        {
            return false;
        }
    }

    private static XRTexture2D DeserializeBinaryAsset(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        if (!XRTexture2D.TryDeserializeTextureStreamingPayload(bytes, out XRTexture2D? texture))
        {
            throw new InvalidDataException(
                $"Texture asset '{filePath}' begins with a binary marker but is not a valid XRTexture2D streaming payload.");
        }

        texture.FilePath = Path.GetFullPath(filePath);
        return texture;
    }

    private static bool HasStreamableShape(XRTexture2D texture)
    {
        Mipmap2D[] mipmaps = texture.Mipmaps;
        if (mipmaps is null || mipmaps.Length == 0)
            return false;

        uint sourceMaxDimension = Math.Max(mipmaps[0].Width, mipmaps[0].Height);
        uint previewMaxDimension = XRTexture2D.GetPreviewResidentSize(sourceMaxDimension);
        return sourceMaxDimension <= previewMaxDimension || mipmaps.Length > 1;
    }

    private static bool TryResolveSourcePath(XRTexture2D texture, out string sourcePath)
    {
        sourcePath = texture.OriginalPath ?? texture.FilePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourcePath))
            return false;

        sourcePath = Path.GetFullPath(sourcePath);
        if (Path.GetExtension(sourcePath).Equals(
            $".{AssetSerializationServices.Current.AssetExtension}",
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return File.Exists(sourcePath);
    }
}
