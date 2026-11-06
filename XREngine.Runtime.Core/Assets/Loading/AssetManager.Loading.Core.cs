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
        private static bool EnterAssetLoadGate(object loadGate, CancellationToken cancellationToken)
        {
            bool taken = false;
            if (!cancellationToken.CanBeCanceled)
            {
                Monitor.Enter(loadGate, ref taken);
                return taken;
            }

            // Poll so that a canceled caller does not wait for a different caller's load to finish.
            while (!taken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.TryEnter(loadGate, AssetLoadGatePollMilliseconds, ref taken);
            }
            return taken;
        }

        private const int AssetLoadGatePollMilliseconds = 10;

        /// <summary>
        /// Removes an asset that a canceled load made but did not publish.
        /// </summary>
        private static void DiscardCanceledAsset(XRAsset? asset)
        {
            if (asset is null || asset.IsDestroyed)
                return;

            try
            {
                asset.Destroy();
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"Unable to destroy the asset that a canceled load made for '{asset.FilePath ?? asset.Name}'.");
            }
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
                bool gateTaken = EnterAssetLoadGate(loadGate, cancellationToken);
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

                    if (normalizedExtension == AssetExtension && TryLoadPublishedAssetFromArchive(filePath, out T? publishedAsset))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            DiscardCanceledAsset(publishedAsset);
                            cancellationToken.ThrowIfCancellationRequested();
                        }

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
                        normalizedExtension == AssetExtension ? 0.35f : 0.35f);
                    file = normalizedExtension == AssetExtension
                        ? DeserializeAssetFile<T>(filePath)
                        : Load3rdPartyWithCache<T>(filePath, normalizedExtension);

                    // The asset is not in the cache yet. A cancel here drops it, so the cache gets no entry.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        DiscardCanceledAsset(file);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Finalizing, "Finalizing asset graph...", 0.95f);
                    PostLoaded(filePath, file);
                    AssetLoadProgressContext.ReportStage(file is null ? AssetLoadProgressStage.Failed : AssetLoadProgressStage.Completed, file is null ? "Asset load failed." : "Asset ready.", file is null ? 1.0f : 1.0f);
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
                    file = normalizedExtension == AssetExtension
                        ? DeserializeAssetFile(filePath, type)
                        : Load3rdPartyWithCache(filePath, normalizedExtension, type);
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
