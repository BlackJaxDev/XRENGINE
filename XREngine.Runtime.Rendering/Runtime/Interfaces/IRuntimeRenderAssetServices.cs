using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Required asset, texture IO, import, and streaming services used by runtime rendering.
/// </summary>
public interface IRuntimeRenderAssetServices
{

    /// <summary>
    /// Gets the root that contains engine-owned runtime assets.
    /// </summary>
    string EngineAssetsPath { get; }

    /// <summary>
    /// Gets the root that contains project-owned game assets.
    /// </summary>
    string GameAssetsPath { get; }

    /// <summary>
    /// Gets the root used for generated project asset caches.
    /// </summary>
    string? GameCachePath { get; }

    /// <summary>
    /// Resolves an engine asset path from relative path components.
    /// </summary>
    string ResolveEngineAssetPath(params string[] relativePathFolders);

    /// <summary>
    /// Resolves or creates the import options associated with a third-party source asset.
    /// </summary>
    object? GetOrCreateThirdPartyImportOptions(string sourcePath, Type assetType);

    /// <summary>
    /// Loads an asset through the host asset manager with explicit scheduling behavior.
    /// </summary>
    TAsset? LoadAsset<TAsset>(string filePath, JobPriority priority, bool bypassJobThread)
        where TAsset : XRAsset, new();

    /// <summary>
    /// Resolves the cache path for one third-party import variant.
    /// </summary>
    bool TryResolveThirdPartyCachePath(
        string filePath,
        Type assetType,
        string? cacheVariantKey,
        out string cachePath);

    /// <summary>
    /// Loads or imports one cached third-party asset variant.
    /// </summary>
    TAsset? LoadThirdPartyVariantWithCache<TAsset>(
        string filePath,
        object? importOptions,
        string cacheVariantKey,
        JobPriority priority,
        bool bypassJobThread)
        where TAsset : XRAsset, new();

    /// <summary>
    /// Evicts a loaded asset identity and its source-path aliases from the host cache.
    /// </summary>
    void EvictAsset(XRAsset asset, string resolvedPath);

    /// <summary>
    /// Loads an engine asset asynchronously with explicit scheduling behavior.
    /// </summary>
    Task<TAsset> LoadEngineAssetAsync<TAsset>(
        JobPriority priority,
        bool bypassJobThread,
        params string[] relativePathFolders)
        where TAsset : XRAsset, new();

    /// <summary>
    /// Gets the host asset-file extension without a leading period.
    /// </summary>
    string AssetFileExtension { get; }

    /// <summary>
    /// Gets the texture path used when a requested texture cannot be loaded.
    /// </summary>
    string? TextureFallbackPath { get; }

    /// <summary>
    /// Gets the material used when a requested material is invalid or unavailable.
    /// </summary>
    XRMaterial? InvalidMaterial { get; }

    /// <summary>
    /// Reads all bytes for a file path using the host file IO path, including DirectStorage where available.
    /// </summary>
    byte[] ReadAllBytes(string filePath);

    /// <summary>
    /// Resolves the authoritative path used to key texture streaming state and cooked caches.
    /// </summary>
    string ResolveTextureStreamingAuthorityPath(string filePath);

    /// <summary>
    /// Reports sparse texture streaming support for the supplied internal texture format.
    /// </summary>
    SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format);

    /// <summary>
    /// Attempts to schedule an asynchronous sparse texture streaming transition on the render backend.
    /// </summary>
    bool TryScheduleSparseTextureStreamingTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null);

    /// <summary>
    /// Finalizes a sparse texture streaming transition after backend work completes.
    /// </summary>
    SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult);

    /// <summary>
    /// Schedules an enumerator-based background job through the host job system.
    /// </summary>
    EnumeratorJob ScheduleEnumeratorJob(
        Func<IEnumerable> routineFactory,
        JobPriority priority = JobPriority.Normal,
        Action? completed = null,
        Action<Exception>? error = null,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Loads an engine asset through the host asset manager.
    /// </summary>
    TAsset? LoadAsset<TAsset>(string filePath) where TAsset : XRAsset, new();
}
