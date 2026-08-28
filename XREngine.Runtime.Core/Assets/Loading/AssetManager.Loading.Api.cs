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
            ? PreparePrefabPartialLoad(filePath)
            : await Task.Run(() => PreparePrefabPartialLoad(filePath)).ConfigureAwait(false);
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

    private static PrefabPartialLoadPlan? PreparePrefabPartialLoad(string filePath)
    {
        AssetManager.EnsureYamlAssetRuntimeSupported(filePath);
        filePath = Path.GetFullPath(filePath);
        if (!File.Exists(filePath))
            return null;

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

}
