using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Diagnostics;
using XREngine.Data.Core;
using XRAsset = XREngine.Core.Files.XRAsset;

namespace XREngine
{
    public partial class AssetManager
    {
        private object GetAssetLoadGate(string filePath)
            => _assetLoadGates.GetOrAdd(filePath, static _ => new object());

        private T? LoadCore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
        {
            T? file;
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
                        AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Completed, "Using cached asset.", 1.0f);
                        return existingAsset is T tAsset ? tAsset : null;
                    }

                    string extension = Path.GetExtension(filePath);
                    if (string.IsNullOrWhiteSpace(extension) || extension.Length <= 1)
                    {
                        Debug.LogWarning($"Unable to load asset at '{filePath}' because the file has no extension.");
                        return null;
                    }

                    string normalizedExtension = extension[1..].ToLowerInvariant();

#if XRE_PUBLISHED
                    if (normalizedExtension == AssetExtension && TryLoadPublishedAssetFromArchive(filePath, out T? publishedAsset))
                    {
                        AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Completed, "Loaded published asset from archive.", 1.0f);
                        PostLoaded(filePath, publishedAsset);
                        return publishedAsset;
                    }
#endif

                    if (!File.Exists(filePath))
                    {
                        AssetDiagnostics.RecordMissingAsset(filePath, typeof(T).Name, $"{nameof(AssetManager)}.{nameof(Load)}");
                        return null;
                    }

                    AssetLoadProgressContext.ReportStage(
                        normalizedExtension == AssetExtension ? AssetLoadProgressStage.ParsingAssetGraph : AssetLoadProgressStage.ImportingThirdParty,
                        normalizedExtension == AssetExtension ? "Reading serialized asset graph..." : "Importing third-party asset...",
                        normalizedExtension == AssetExtension ? 0.35f : 0.35f);
                    file = normalizedExtension == AssetExtension
                        ? DeserializeAssetFile<T>(filePath)
                        : Load3rdPartyWithCache<T>(filePath, normalizedExtension);
                    AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Finalizing, "Finalizing asset graph...", 0.95f);
                    PostLoaded(filePath, file);
                    AssetLoadProgressContext.ReportStage(file is null ? AssetLoadProgressStage.Failed : AssetLoadProgressStage.Completed, file is null ? "Asset load failed." : "Asset ready.", file is null ? 1.0f : 1.0f);
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

        private XRAsset? LoadCore(string filePath, Type type)
        {
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
                        AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Completed, "Using cached asset.", 1.0f);
                        return existingAsset.GetType().IsAssignableTo(type) ? existingAsset : null;
                    }

                    string extension = Path.GetExtension(filePath);
                    if (string.IsNullOrWhiteSpace(extension) || extension.Length <= 1)
                    {
                        Debug.LogWarning($"Unable to load asset at '{filePath}' because the file has no extension.");
                        return null;
                    }

                    string normalizedExtension = extension[1..].ToLowerInvariant();

#if XRE_PUBLISHED
                    if (normalizedExtension == AssetExtension && TryLoadPublishedAssetFromArchive(filePath, type, out XRAsset? publishedAsset))
                    {
                        AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.Completed, "Loaded published asset from archive.", 1.0f);
                        PostLoaded(filePath, publishedAsset);
                        return publishedAsset;
                    }
#endif

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