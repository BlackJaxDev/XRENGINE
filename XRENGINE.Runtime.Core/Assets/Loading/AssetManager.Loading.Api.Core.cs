using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Core.Files.Caching;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine;

public partial class AssetManager
{
    public Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
        string filePath,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        where T : XRAsset, new()
        => LoadAsync<T>(filePath, progressCallback: null, priority, bypassJobThread);

    public Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
        string filePath,
        Action<AssetLoadProgress>? progressCallback,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        where T : XRAsset, new()
        => RunOnJobThreadAsync(() =>
        {
            using IDisposable progressScope = AssetLoadProgressContext.Begin(filePath, progressCallback);
            return LoadCore<T>(filePath);
        }, priority, bypassJobThread);

    public Task<XRAsset?> LoadAsync(
        string filePath,
        Type type,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        => LoadAsync(filePath, type, progressCallback: null, priority, bypassJobThread);

    public Task<XRAsset?> LoadAsync(
        string filePath,
        Type type,
        Action<AssetLoadProgress>? progressCallback,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        return RunOnJobThreadAsync(() =>
        {
            using IDisposable progressScope = AssetLoadProgressContext.Begin(filePath, progressCallback);
            return LoadCore(filePath, type);
        }, priority, bypassJobThread);
    }

    public T? Load<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
        string filePath,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        where T : XRAsset, new()
        => RunOnJobThreadBlocking(() => LoadCore<T>(filePath), priority, bypassJobThread);

    public XRAsset? Load(
        string filePath,
        Type type,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        return RunOnJobThreadBlocking(() => LoadCore(filePath, type), priority, bypassJobThread);
    }

    public T? LoadImmediate<T>(string filePath) where T : XRAsset, new()
        => Load<T>(filePath, JobPriority.Normal, bypassJobThread: true);

    public XRAsset? LoadImmediate(string filePath, Type type)
        => Load(filePath, type, JobPriority.Normal, bypassJobThread: true);

    public T? LoadGameAsset<T>(params string[] relativePathFolders) where T : XRAsset, new()
        => LoadGameAsset<T>(JobPriority.Normal, bypassJobThread: false, relativePathFolders);

    public T? LoadGameAsset<T>(
        JobPriority priority,
        bool bypassJobThread,
        params string[] relativePathFolders)
        where T : XRAsset, new()
        => Load<T>(ResolveGameAssetPath(relativePathFolders), priority, bypassJobThread);

    public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        params string[] relativePathFolders)
        where T : XRAsset, new()
        => LoadEngineAsset<T>(JobPriority.Normal, bypassJobThread: false, relativePathFolders);

    public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        JobPriority priority,
        params string[] relativePathFolders)
        where T : XRAsset, new()
        => LoadEngineAsset<T>(priority, bypassJobThread: false, relativePathFolders);

    public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        JobPriority priority,
        bool bypassJobThread,
        params string[] relativePathFolders)
        where T : XRAsset, new()
    {
        string path = ResolveEngineAssetPath(relativePathFolders);
        return Load<T>(path, priority, bypassJobThread)
            ?? throw new FileNotFoundException($"Unable to find engine asset at '{path}'.");
    }

    public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        params string[] relativePathFolders)
        where T : XRAsset, new()
    {
        string path = ResolveEngineAssetPath(relativePathFolders);
        return await LoadAsync<T>(path).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Unable to find engine asset at '{path}'.");
    }

    public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        JobPriority priority,
        bool bypassJobThread,
        params string[] relativePathFolders)
        where T : XRAsset, new()
    {
        string path = ResolveEngineAssetPath(relativePathFolders);
        return await LoadAsync<T>(path, priority, bypassJobThread).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Unable to find engine asset at '{path}'.");
    }

    private static T? DeserializeAssetFile<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath)
        where T : XRAsset, new()
        => DeserializeAssetFile(filePath, typeof(T)) as T;

    public static XRAsset? DeserializeAssetFile(string filePath, Type type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(type);

        IThirdPartyCacheCodec? directCodec = ThirdPartyCacheCodecRegistry.Find(type);
        if (directCodec?.TryReadDirectAssetFile(filePath, type, out XRAsset? directAsset) == true)
            return directAsset;

        EnsureYamlAssetRuntimeSupported(filePath);

        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        using IDisposable scope = AssetDeserializationContext.Push(filePath);
        ResetYamlReadContext();
        return Deserializer.Deserialize(reader, type) as XRAsset;
    }

    public static Task<XRAsset?> DeserializeAssetFileAsync(string filePath, Type type)
        => Task.Run(() => DeserializeAssetFile(filePath, type));

    private T? Load3rdPartyWithCache<T>(string filePath, string extension)
        where T : XRAsset, new()
        => RuntimeThirdPartyAssetLoadingServices.Current.Load(
            filePath,
            extension,
            typeof(T),
            importContext: CreateThirdPartyImportContext(filePath, typeof(T), cacheVariantKey: null)) as T;

    private XRAsset? Load3rdPartyWithCache(string filePath, string extension, Type type)
        => RuntimeThirdPartyAssetLoadingServices.Current.Load(
            filePath,
            extension,
            type,
            importContext: CreateThirdPartyImportContext(filePath, type, cacheVariantKey: null));

    public T? Load3rdPartyVariantWithCache<T>(
        string filePath,
        object? importOptions,
        string cacheVariantKey,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        where T : XRAsset, new()
        => RunOnJobThreadBlocking(
            () => RuntimeThirdPartyAssetLoadingServices.Current.Load(
                filePath,
                Path.GetExtension(filePath).TrimStart('.'),
                typeof(T),
                importOptions,
                CreateThirdPartyImportContext(filePath, typeof(T), cacheVariantKey)) as T,
            priority,
            bypassJobThread);

    private AssetImportContext CreateThirdPartyImportContext(
        string filePath,
        Type assetType,
        string? cacheVariantKey)
    {
        string? cacheDirectory = TryResolveThirdPartyCachePath(
            filePath,
            assetType,
            cacheVariantKey,
            out string cachePath)
                ? Path.GetDirectoryName(cachePath)
                : null;
        return new AssetImportContext(filePath, cacheDirectory);
    }

    public bool TryResolveThirdPartyCachePath(
        string filePath,
        Type assetType,
        string? cacheVariantKey,
        out string cachePath)
    {
        ThirdPartyCachePathRequest request = new(
            GameCachePath,
            GameAssetsPath,
            EngineAssetsPath,
            filePath,
            assetType,
            cacheVariantKey,
            ImportOptions: null,
            AssetExtension);
        IThirdPartyCachePathPolicy? policy = ThirdPartyCachePathPolicies.Find(assetType);
        return policy?.TryResolve(request, out cachePath) == true
            || request.TryResolveGenericThirdPartyCachePath(filePath, assetType, cacheVariantKey, out cachePath);
    }

    internal static CacheCodecOwnership GetThirdPartyCacheCodecOwnership(Type assetType)
        => ThirdPartyCacheCodecRegistry.Find(assetType)?.GetOwnership(assetType)
            ?? CacheCodecOwnership.NotHandled;

    internal string ResolveThirdPartyCacheAuthorityPath<T>(
        string filePath,
        object? importOptions = null,
        string? cacheVariantKey = null)
        where T : XRAsset, new()
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return filePath;

        string normalizedPath = Path.GetFullPath(filePath);
        if (string.Equals(Path.GetExtension(normalizedPath), $".{AssetExtension}", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(normalizedPath))
            return normalizedPath;

        IThirdPartyCacheCodec? codec = ThirdPartyCacheCodecRegistry.Find(typeof(T));
        if (codec is null || codec.GetOwnership(typeof(T)) == CacheCodecOwnership.Exclusive)
            return normalizedPath;

        string? variantKey = codec.ResolveDefaultVariantKey(cacheVariantKey);
        if (!TryResolveThirdPartyCachePath(normalizedPath, typeof(T), variantKey, out string cachePath)
            || !File.Exists(cachePath))
            return normalizedPath;

        DateTime sourceTimestampUtc = File.GetLastWriteTimeUtc(normalizedPath);
        DateTime cacheTimestampUtc = File.GetLastWriteTimeUtc(cachePath);
        return cacheTimestampUtc >= sourceTimestampUtc && codec.IsCacheUsable(cachePath)
            ? cachePath
            : normalizedPath;
    }

    public string ResolveTextureStreamingAuthorityPath(string filePath)
    {
        IThirdPartyCacheCodec? codec = ThirdPartyCacheCodecRegistry.FindByAuthorityRole(
            ThirdPartyCacheAuthorityRoles.TextureStreaming);
        Type? assetType = codec?.AuthorityAssetType;
        string? variantKey = codec?.ResolveDefaultVariantKey(null);
        return assetType is not null
            && TryResolveThirdPartyCachePath(filePath, assetType, variantKey, out string cachePath)
                ? cachePath
                : Path.GetFullPath(filePath);
    }
}
