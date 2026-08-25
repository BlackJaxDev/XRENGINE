using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Scene.Prefabs;

namespace XREngine;

/// <summary>Prefab-specific staged loading layered over Runtime.Core's generic asset manager.</summary>
public static class AssetManagerPrefabLoadingExtensions
{
    public static async Task<PrefabPartialLoadPlan?> PreparePrefabPartialLoadAsync(
        this AssetManager assets,
        string filePath,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
    {
        ArgumentNullException.ThrowIfNull(assets);

        if (!File.Exists(filePath))
            _ = await assets.LoadAsync<XRPrefabSource>(filePath, priority, bypassJobThread).ConfigureAwait(false);
        if (!File.Exists(filePath))
            return null;

        return bypassJobThread
            ? PreparePrefabPartialLoad(assets, filePath)
            : await Task.Run(() => PreparePrefabPartialLoad(assets, filePath)).ConfigureAwait(false);
    }

    public static async Task<XRPrefabSource?> LoadPrefabWithReferencesAsync(
        this AssetManager assets,
        string filePath,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false,
        CancellationToken cancellationToken = default,
        int maxConcurrentReferenceLoads = 4)
    {
        if (maxConcurrentReferenceLoads <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentReferenceLoads));

        cancellationToken.ThrowIfCancellationRequested();
        PrefabPartialLoadPlan? plan = await assets.PreparePrefabPartialLoadAsync(
            filePath,
            priority,
            bypassJobThread).ConfigureAwait(false);
        if (plan is null)
            return null;

        IReadOnlyList<DeferredAssetLoadReference> references = plan.ExternalReferences;
        if (references.Count > 0)
        {
            int nextReferenceIndex = -1;
            int workerCount = Math.Min(maxConcurrentReferenceLoads, references.Count);
            Task[] workers = new Task[workerCount];
            for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                workers[workerIndex] = Task.Run(async () =>
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int referenceIndex = Interlocked.Increment(ref nextReferenceIndex);
                        if (referenceIndex >= references.Count)
                            return;

                        DeferredAssetLoadReference reference = references[referenceIndex];
                        if (assets.TryGetAssetByPath(reference.AssetPath, out _))
                            continue;

                        XRAsset? loaded = await assets.LoadAsync(
                            reference.AssetPath,
                            reference.AssetType,
                            priority,
                            bypassJobThread: true).ConfigureAwait(false);
                        if (loaded is null)
                        {
                            throw new InvalidDataException(
                                $"Referenced asset '{reference.AssetPath}' could not be loaded as '{reference.AssetType.FullName}'.");
                        }
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await assets.LoadAsync<XRPrefabSource>(filePath, priority, bypassJobThread).ConfigureAwait(false);
    }

    private static PrefabPartialLoadPlan? PreparePrefabPartialLoad(AssetManager assets, string filePath)
    {
        AssetManager.EnsureYamlAssetRuntimeSupported(filePath);
        filePath = Path.GetFullPath(filePath);
        if (!File.Exists(filePath))
            return null;

        PrefabPartialLoadPlan? plan = DeserializePartialPrefab(filePath);
        if (plan is null || !EnsureMissingOwnedOutputMetadata(assets, plan.PartialPrefab))
            return plan;

        return DeserializePartialPrefab(filePath);
    }

    private static PrefabPartialLoadPlan? DeserializePartialPrefab(string filePath)
    {
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        using IDisposable pathScope = AssetDeserializationContext.Push(filePath);
        using IDisposable cacheSuppression = XRObjectBase.SuppressObjectCacheRegistration();
        using DeferredAssetReferenceContext.Collector collector = new();
        YamlDefaultTypeContext.ResetReadState();
        YamlTransformReferenceContext.ResetReadState();

        XRPrefabSource? partialPrefab = AssetManager.Deserializer.Deserialize<XRPrefabSource>(reader);
        if (partialPrefab is null)
            return null;

        partialPrefab.FilePath = filePath;
        partialPrefab.SourceAsset = partialPrefab;
        return new PrefabPartialLoadPlan(partialPrefab, collector.References);
    }

    private static bool EnsureMissingOwnedOutputMetadata(AssetManager assets, XRPrefabSource partialPrefab)
    {
        if (partialPrefab.UnityImportManifest?.OwnedOutputPaths is not { Count: > 0 } ownedOutputPaths)
            return false;

        bool createdMetadata = false;
        foreach (string serializedPath in ownedOutputPaths)
        {
            if (!TryResolveOwnedOutputPath(assets, serializedPath, out string? assetPath)
                || assetPath is null
                || !string.Equals(Path.GetExtension(assetPath), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase)
                || !assets.TryGetMetadataPath(assetPath, out string metadataPath, out _)
                || File.Exists(metadataPath))
            {
                continue;
            }

            assets.EnsureMetadataForAssetPath(assetPath, isDirectory: false);
            createdMetadata |= File.Exists(metadataPath);
        }

        return createdMetadata;
    }

    private static bool TryResolveOwnedOutputPath(
        AssetManager assets,
        string serializedPath,
        out string? assetPath)
    {
        assetPath = null;
        if (string.IsNullOrWhiteSpace(serializedPath) || string.IsNullOrWhiteSpace(assets.GameAssetsPath))
            return false;

        try
        {
            if (!Path.IsPathRooted(serializedPath))
            {
                string reference = string.Concat(AssetReferencePath.GamePrefix, serializedPath.Replace('\\', '/'));
                return AssetReferencePath.TryResolve(
                    reference,
                    assets.GameAssetsPath,
                    assets.EngineAssetsPath,
                    out assetPath)
                    && File.Exists(assetPath);
            }

            string candidate = Path.GetFullPath(serializedPath);
            if (File.Exists(candidate)
                && AssetReferencePath.TryCreate(
                    assets.GameAssetsPath,
                    candidate,
                    AssetReferencePath.GamePrefix,
                    out _))
            {
                assetPath = candidate;
                return true;
            }

            string normalizedPath = serializedPath.Replace('\\', '/');
            int assetsSegment = normalizedPath.LastIndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (assetsSegment < 0)
                return false;

            string rebasedReference = string.Concat(
                AssetReferencePath.GamePrefix,
                normalizedPath[(assetsSegment + "/Assets/".Length)..]);
            return AssetReferencePath.TryResolve(
                rebasedReference,
                assets.GameAssetsPath,
                assets.EngineAssetsPath,
                out assetPath)
                && File.Exists(assetPath);
        }
        catch
        {
            assetPath = null;
            return false;
        }
    }
}
