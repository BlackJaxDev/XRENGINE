using ImageMagick;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace XREngine.Rendering;

internal static class TextureStreamingSourceFactory
{
    internal static ITextureStreamingSource Create(string filePath)
    {
        string authorityPath = XRTexture2D.ResolveTextureStreamingAuthorityPathInternal(filePath, out string? originalSourcePath);

        if (XRTexture2D.HasAssetExtensionInternal(authorityPath))
            return new AssetTextureStreamingSource(authorityPath, originalSourcePath);

        return new ThirdPartyTextureStreamingSource(authorityPath);
    }
}

internal sealed class AssetTextureStreamingSource(string assetPath, string? fallbackSourcePath = null) : ITextureStreamingSource
{
    private readonly ThirdPartyTextureStreamingSource? _fallbackSource = string.IsNullOrWhiteSpace(fallbackSourcePath)
        ? null
        : new ThirdPartyTextureStreamingSource(Path.GetFullPath(fallbackSourcePath));
    private int _preferFallback;

    public string SourcePath => assetPath;

    public TextureStreamingResidentData LoadResidentData(uint maxResidentDimension, bool includeMipChain, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _preferFallback) != 0 && _fallbackSource is not null)
            return _fallbackSource.LoadResidentData(maxResidentDimension, includeMipChain, cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long totalStartTimestamp = XRTexture2D.StartImportedTextureTiming();
            long readStartTimestamp = XRTexture2D.StartImportedTextureTiming();
            byte[] assetBytes = RuntimeRenderingHostServices.Assets.ReadAllBytes(assetPath);
            double cacheReadMilliseconds = XRTexture2D.CompleteImportedTextureTiming(readStartTimestamp);
            cancellationToken.ThrowIfCancellationRequested();

            long parseStartTimestamp = XRTexture2D.StartImportedTextureTiming();
            if (XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes(assetBytes, maxResidentDimension, includeMipChain, out TextureStreamingResidentData residentData))
            {
                double cacheParseMilliseconds = XRTexture2D.CompleteImportedTextureTiming(parseStartTimestamp);
                TextureRuntimeDiagnostics.LogCacheRead(
                    RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
                    _fallbackSource?.SourcePath ?? assetPath,
                    assetPath,
                    residentData.SourceWidth,
                    residentData.SourceHeight,
                    maxResidentDimension,
                    residentData.ResidentMaxDimension,
                    includeMipChain,
                    residentData.Mipmaps.Length,
                    cacheReadMilliseconds,
                    cacheParseMilliseconds,
                    XRTexture2D.CompleteImportedTextureTiming(totalStartTimestamp),
                    usedCookedPayload: true);
                return residentData;
            }

            double failedPayloadParseMilliseconds = XRTexture2D.CompleteImportedTextureTiming(parseStartTimestamp);
            cancellationToken.ThrowIfCancellationRequested();

            XRTexture2D? scratch = RuntimeRenderingHostServices.Assets.LoadAsset<XRTexture2D>(assetPath);
            if (scratch is not null)
            {
                TextureStreamingResidentData loadedResidentData = XRTexture2D.BuildResidentDataFromLoadedTexture(
                    scratch,
                    maxResidentDimension,
                    includeMipChain,
                    cancellationToken);
                TextureRuntimeDiagnostics.LogCacheRead(
                    RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
                    _fallbackSource?.SourcePath ?? assetPath,
                    assetPath,
                    loadedResidentData.SourceWidth,
                    loadedResidentData.SourceHeight,
                    maxResidentDimension,
                    loadedResidentData.ResidentMaxDimension,
                    includeMipChain,
                    loadedResidentData.Mipmaps.Length,
                    cacheReadMilliseconds,
                    failedPayloadParseMilliseconds,
                    XRTexture2D.CompleteImportedTextureTiming(totalStartTimestamp),
                    usedCookedPayload: false);
                return loadedResidentData;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (_fallbackSource is not null)
        {
            return LoadFallbackResidentData(maxResidentDimension, includeMipChain, cancellationToken, ex);
        }

        if (_fallbackSource is not null)
            return LoadFallbackResidentData(maxResidentDimension, includeMipChain, cancellationToken, failure: null);

        throw new FileNotFoundException($"Failed to load texture asset '{assetPath}'.", assetPath);
    }

    private TextureStreamingResidentData LoadFallbackResidentData(uint maxResidentDimension, bool includeMipChain, CancellationToken cancellationToken, Exception? failure)
    {
        if (_fallbackSource is null)
            throw failure ?? new FileNotFoundException($"Failed to load texture asset '{assetPath}'.", assetPath);

        if (Interlocked.Exchange(ref _preferFallback, 1) == 0)
        {
            string reason = failure is null
                ? "the cached texture asset was unreadable"
                : $"{failure.GetType().Name}: {failure.Message}";
            Debug.TexturesWarning(
                $"Falling back to source texture '{_fallbackSource.SourcePath}' because cache asset '{assetPath}' could not be used ({reason}).");
        }

        return _fallbackSource.LoadResidentData(maxResidentDimension, includeMipChain, cancellationToken);
    }
}

internal sealed class ThirdPartyTextureStreamingSource(string sourcePath) : ITextureStreamingSource
{
    public string SourcePath => sourcePath;

    public TextureStreamingResidentData LoadResidentData(uint maxResidentDimension, bool includeMipChain, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            using MagickImage filler = (MagickImage)XRTexture2D.FillerImage.Clone();
            return XRTexture2D.BuildResidentDataFromImage(filler, maxResidentDimension, includeMipChain, cancellationToken: cancellationToken);
        }

        long decodeStartTimestamp = XRTexture2D.StartImportedTextureTiming();
        using MagickImage sourceImage = new(sourcePath);
        double decodeMilliseconds = XRTexture2D.CompleteImportedTextureTiming(decodeStartTimestamp);
        cancellationToken.ThrowIfCancellationRequested();
        return XRTexture2D.BuildResidentDataFromImage(
            sourceImage,
            maxResidentDimension,
            includeMipChain,
            sourcePath,
            decodeMilliseconds,
            cancellationToken);
    }
}

internal static class TextureStreamingResidentDataReuseCache
{
    private const int MaxEntries = 24;
    private const long MaxEntryBytes = 64L * 1024L * 1024L;
    private const long MaxTotalBytes = 256L * 1024L * 1024L;
    private const double EntryLifetimeMilliseconds = 15_000.0;

    private readonly record struct CacheKey(
        string AuthorityPath,
        long LastWriteUtcTicks,
        long Length,
        uint MaxResidentDimension,
        bool IncludeMipChain);

    private sealed record CacheEntry(TextureStreamingResidentData Data, long CreatedTimestamp, long Bytes);

    private static readonly ConcurrentDictionary<CacheKey, CacheEntry> Entries = new();
    private static long s_totalBytes;

    public static bool TryGet(
        ITextureStreamingSource source,
        uint maxResidentDimension,
        bool includeMipChain,
        out TextureStreamingResidentData residentData)
    {
        residentData = default;
        if (!TryCreateKey(source, maxResidentDimension, includeMipChain, out CacheKey key))
            return false;

        if (!Entries.TryGetValue(key, out CacheEntry? entry))
            return false;

        if (TextureRuntimeDiagnostics.ElapsedMilliseconds(entry.CreatedTimestamp) > EntryLifetimeMilliseconds)
        {
            Remove(key);
            return false;
        }

        residentData = CloneResidentData(entry.Data);
        return true;
    }

    public static void Store(
        ITextureStreamingSource source,
        uint maxResidentDimension,
        bool includeMipChain,
        TextureStreamingResidentData residentData)
    {
        if (!TryCreateKey(source, maxResidentDimension, includeMipChain, out CacheKey key))
            return;

        long bytes = XRTexture2D.CalculateResidentUploadBytes(residentData);
        if (bytes <= 0 || bytes > MaxEntryBytes)
            return;

        CacheEntry entry = new(CloneResidentData(residentData), TextureRuntimeDiagnostics.StartTiming(), bytes);
        Entries.AddOrUpdate(
            key,
            addValueFactory: _ =>
            {
                Interlocked.Add(ref s_totalBytes, bytes);
                return entry;
            },
            updateValueFactory: (_, previous) =>
            {
                Interlocked.Add(ref s_totalBytes, bytes - previous.Bytes);
                return entry;
            });

        PruneIfNeeded();
    }

    private static bool TryCreateKey(
        ITextureStreamingSource source,
        uint maxResidentDimension,
        bool includeMipChain,
        out CacheKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(source.SourcePath))
            return false;

        string authorityPath = Path.GetFullPath(source.SourcePath);
        long lastWriteUtcTicks = 0L;
        long length = 0L;
        try
        {
            FileInfo info = new(authorityPath);
            if (info.Exists)
            {
                lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
                length = info.Length;
            }
        }
        catch
        {
            return false;
        }

        key = new CacheKey(
            authorityPath,
            lastWriteUtcTicks,
            length,
            maxResidentDimension,
            includeMipChain);
        return true;
    }

    private static TextureStreamingResidentData CloneResidentData(TextureStreamingResidentData residentData)
    {
        Mipmap2D[] sourceMipmaps = residentData.Mipmaps;
        Mipmap2D[] mipmaps = new Mipmap2D[sourceMipmaps.Length];
        for (int i = 0; i < sourceMipmaps.Length; i++)
            mipmaps[i] = sourceMipmaps[i].Clone(cloneImage: true);

        return new TextureStreamingResidentData(
            mipmaps,
            residentData.SourceWidth,
            residentData.SourceHeight,
            residentData.ResidentMaxDimension);
    }

    private static void PruneIfNeeded()
    {
        long totalBytes = Interlocked.Read(ref s_totalBytes);
        if (Entries.Count <= MaxEntries && totalBytes <= MaxTotalBytes)
            return;

        foreach (KeyValuePair<CacheKey, CacheEntry> pair in Entries)
        {
            if (TextureRuntimeDiagnostics.ElapsedMilliseconds(pair.Value.CreatedTimestamp) > EntryLifetimeMilliseconds)
                Remove(pair.Key);
        }

        while (Entries.Count > MaxEntries || Interlocked.Read(ref s_totalBytes) > MaxTotalBytes)
        {
            CacheKey oldestKey = default;
            CacheEntry? oldestEntry = null;
            foreach (KeyValuePair<CacheKey, CacheEntry> pair in Entries)
            {
                if (oldestEntry is null || pair.Value.CreatedTimestamp < oldestEntry.CreatedTimestamp)
                {
                    oldestKey = pair.Key;
                    oldestEntry = pair.Value;
                }
            }

            if (oldestEntry is null)
                return;

            Remove(oldestKey);
        }
    }

    private static void Remove(CacheKey key)
    {
        if (Entries.TryRemove(key, out CacheEntry? removed))
            Interlocked.Add(ref s_totalBytes, -removed.Bytes);
    }
}
