using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Diagnostics;
using XRAsset = XREngine.Core.Files.XRAsset;

namespace XREngine
{
    public partial class AssetManager
    {
        /// <summary>The time in milliseconds that a cancelable load waits on a held path gate before it checks its token again.</summary>
        private const int AssetLoadGatePollMilliseconds = 10;

        /// <summary>
        /// The number of loads on this thread that publish their objects in their own batch.
        /// A load nested in such a load also uses its own batch, so it does not join the batch of the outer load.
        /// </summary>
        [ThreadStatic]
        private static int t_isolatedPublicationLoadDepth;

        /// <summary>
        /// Occurs on the waiting thread when a cancelable load finds that a different load holds the path gate.
        /// The argument is the full asset path.
        /// </summary>
        internal event Action<string>? AssetLoadGateContended;

        private object GetAssetLoadGate(string filePath)
            => _assetLoadGates.GetOrAdd(filePath, static _ => new object());

        private void EvictMismatchedPathCacheEntry(string filePath, XRAsset existingAsset, Type requestedType)
        {
            Debug.LogWarning(
                $"Reloading asset at '{filePath}' because the path cache contains '{existingAsset.GetType().Name}' " +
                $"but '{requestedType.Name}' was requested.");

            LoadedAssetsByPathInternal.TryRemove(filePath, out _);

            if (!string.IsNullOrWhiteSpace(existingAsset.OriginalPath)
                && string.Equals(Path.GetFullPath(existingAsset.OriginalPath), filePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadedAssetsByOriginalPathInternal.TryRemove(existingAsset.OriginalPath, out _);
            }

            if (existingAsset.ID != Guid.Empty
                && LoadedAssetsByIDInternal.TryGetValue(existingAsset.ID, out XRAsset? cachedById)
                && ReferenceEquals(cachedById, existingAsset))
            {
                LoadedAssetsByIDInternal.TryRemove(existingAsset.ID, out _);
                DirtyAssets.TryRemove(existingAsset.ID, out _);
            }
        }

        private T? LoadCore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
            => LoadCore<T>(filePath, CancellationToken.None);

        /// <summary>
        /// Gets the path gate. A token that can be canceled stops the wait when the caller cancels.
        /// </summary>
        /// <returns>True when this call holds the gate and must release it.</returns>
        private bool EnterAssetLoadGate(string filePath, object loadGate, CancellationToken cancellationToken)
        {
            bool taken = false;
            if (!cancellationToken.CanBeCanceled)
            {
                Monitor.Enter(loadGate, ref taken);
                return taken;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Monitor.TryEnter(loadGate, ref taken);
            if (taken)
                return true;

            AssetLoadGateContended?.Invoke(filePath);

            // Poll so that a canceled caller does not wait for a different caller's load to finish.
            while (!taken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.TryEnter(loadGate, AssetLoadGatePollMilliseconds, ref taken);
            }
            return true;
        }

        private T? LoadCore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, CancellationToken cancellationToken) where T : XRAsset, new()
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureHostFileAssetAccess();
            T? file;
            filePath = Path.GetFullPath(filePath);
            using var progressScope = AssetLoadProgressContext.EnterAsset(filePath);
            object loadGate = GetAssetLoadGate(filePath);
#if !DEBUG
            try
            {
#endif
                bool gateTaken = EnterAssetLoadGate(filePath, loadGate, cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.CheckingCache, "Checking asset cache...", 0.05f);
                    if (TryGetAssetByPath(filePath, out XRAsset? existingAsset))
                    {
                        if (existingAsset is T tAsset)
                        {
                            AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Completed, "Using cached asset.", 1.0f);
                            return tAsset;
                        }

                        EvictMismatchedPathCacheEntry(filePath, existingAsset, typeof(T));
                    }

                    string extension = Path.GetExtension(filePath);
                    if (string.IsNullOrWhiteSpace(extension) || extension.Length <= 1)
                    {
                        Debug.LogWarning($"Unable to load asset at '{filePath}' because the file has no extension.");
                        return null;
                    }

                    string normalizedExtension = extension[1..].ToLowerInvariant();

                    ObjectCachePublicationScope? publication = BeginLoadObjectPublication(cancellationToken);
                    try
                    {
                        file = LoadUncachedCore<T>(filePath, normalizedExtension, publication, cancellationToken);
                    }
                    finally
                    {
                        EndLoadObjectPublication(publication);
                    }
                }
                finally
                {
                    if (gateTaken)
                        Monitor.Exit(loadGate);
                }
#if !DEBUG
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"An error occurred while loading the asset at '{filePath}'.");
                return null;
            }
#endif
            return file;
        }

        /// <summary>
        /// Starts a publication batch for a load that can be canceled, or for a load nested in one.
        /// </summary>
        /// <remarks>
        /// The objects that the load makes stay out of the global object cache until the last token check.
        /// A cancel disposes the batch, which destroys every object of the load at once. Thus no object
        /// of a canceled load keeps a persistent ID that a later load of the same path needs.
        /// A load nested in such a load gets its own batch, so a cancel of the outer load does not destroy
        /// the objects of an inner asset that is already in the asset cache.
        /// </remarks>
        /// <returns>The batch, or <c>null</c> when the load publishes its objects at once, as before.</returns>
        private static ObjectCachePublicationScope? BeginLoadObjectPublication(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled && t_isolatedPublicationLoadDepth == 0)
                return null;

            ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            t_isolatedPublicationLoadDepth++;
            return publication;
        }

        /// <summary>
        /// Publishes the objects of a load batch. Objects that the load already destroyed are not published.
        /// </summary>
        private static void CompleteLoadObjectPublication(ObjectCachePublicationScope? publication)
        {
            // The ownership list is not kept. The asset cache, not the batch, owns the loaded objects.
            _ = publication?.CompleteWithOwnership();
        }

        /// <summary>
        /// Ends a batch from <see cref="BeginLoadObjectPublication"/>. A batch that did not complete is aborted.
        /// </summary>
        private static void EndLoadObjectPublication(ObjectCachePublicationScope? publication)
        {
            if (publication is null)
                return;

            t_isolatedPublicationLoadDepth--;
            publication.Dispose();
        }

        private T? LoadUncachedCore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
            string filePath,
            string normalizedExtension,
            ObjectCachePublicationScope? publication,
            CancellationToken cancellationToken)
            where T : XRAsset, new()
        {
            if (normalizedExtension == AssetExtension && TryLoadPublishedAssetFromArchive(filePath, out T? publishedAsset))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CompleteLoadObjectPublication(publication);
                AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Completed, "Loaded published asset from archive.", 1.0f);
                PostLoaded(filePath, publishedAsset);
                return publishedAsset;
            }

            if (!File.Exists(filePath))
            {
                AssetDiagnostics.RecordMissingAsset(filePath, typeof(T).Name, $"{nameof(AssetManager)}.{nameof(Load)}");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            AssetLoadProgressContext.ReportStage(
                normalizedExtension == AssetExtension ? AssetLoadProgressStage.ParsingAssetGraph : AssetLoadProgressStage.ImportingThirdParty,
                normalizedExtension == AssetExtension ? "Reading serialized asset graph..." : "Importing third-party asset...",
                0.35f);
            T? file = normalizedExtension == AssetExtension
                ? DeserializeAssetFile<T>(filePath)
                : Load3rdPartyWithCache<T>(filePath, normalizedExtension);

            // The objects of this load are not published yet. A cancel here aborts the publication batch,
            // which destroys every object of this load, so the caches get no entry.
            cancellationToken.ThrowIfCancellationRequested();
            CompleteLoadObjectPublication(publication);

            AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Finalizing, "Finalizing asset graph...", 0.95f);
            PostLoaded(filePath, file);
            AssetLoadProgressContext.ReportStage(file is null ? AssetLoadProgressStage.Failed : AssetLoadProgressStage.Completed, file is null ? "Asset load failed." : "Asset ready.", 1.0f);
            return file;
        }

        private XRAsset? LoadCore(string filePath, Type type)
        {
            EnsureHostFileAssetAccess();
            XRAsset? file;
            filePath = Path.GetFullPath(filePath);
            using var progressScope = AssetLoadProgressContext.EnterAsset(filePath);
            object loadGate = GetAssetLoadGate(filePath);
#if !DEBUG
            try
            {
#endif
                lock (loadGate)
                {
                    AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.CheckingCache, "Checking asset cache...", 0.05f);
                    if (TryGetAssetByPath(filePath, out XRAsset? existingAsset))
                    {
                        if (existingAsset.GetType().IsAssignableTo(type))
                        {
                            AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Completed, "Using cached asset.", 1.0f);
                            return existingAsset;
                        }

                        EvictMismatchedPathCacheEntry(filePath, existingAsset, type);
                    }

                    string extension = Path.GetExtension(filePath);
                    if (string.IsNullOrWhiteSpace(extension) || extension.Length <= 1)
                    {
                        Debug.LogWarning($"Unable to load asset at '{filePath}' because the file has no extension.");
                        return null;
                    }

                    string normalizedExtension = extension[1..].ToLowerInvariant();

                    if (normalizedExtension == AssetExtension && TryLoadPublishedAssetFromArchive(filePath, type, out XRAsset? publishedAsset))
                    {
                        AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Completed, "Loaded published asset from archive.", 1.0f);
                        PostLoaded(filePath, publishedAsset);
                        return publishedAsset;
                    }

                    if (!File.Exists(filePath))
                    {
                        AssetDiagnostics.RecordMissingAsset(filePath, type.Name, $"{nameof(AssetManager)}.{nameof(Load)}");
                        return null;
                    }

                    AssetLoadProgressContext.ReportStage(
                        normalizedExtension == AssetExtension ? AssetLoadProgressStage.ParsingAssetGraph : AssetLoadProgressStage.ImportingThirdParty,
                        normalizedExtension == AssetExtension ? "Reading serialized asset graph..." : "Importing third-party asset...",
                        0.35f);
                    ObjectCachePublicationScope? publication = BeginLoadObjectPublication(CancellationToken.None);
                    try
                    {
                        file = normalizedExtension == AssetExtension
                            ? DeserializeAssetFile(filePath, type)
                            : Load3rdPartyWithCache(filePath, normalizedExtension, type);
                        CompleteLoadObjectPublication(publication);
                    }
                    finally
                    {
                        EndLoadObjectPublication(publication);
                    }
                    AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Finalizing, "Finalizing asset graph...", 0.95f);
                    PostLoaded(filePath, file);
                    AssetLoadProgressContext.ReportStage(file is null ? AssetLoadProgressStage.Failed : AssetLoadProgressStage.Completed, file is null ? "Asset load failed." : "Asset ready.", 1.0f);
                }
#if !DEBUG
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"An error occurred while loading the asset at '{filePath}'.");
                return null;
            }
#endif
            return file;
        }
    }
}
