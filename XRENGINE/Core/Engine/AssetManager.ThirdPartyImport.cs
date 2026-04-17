using Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Serialization;

namespace XREngine
{
    public partial class AssetManager
    {
        private static Dictionary<string, Type> CreateThirdPartyExtensionMap()
        {
            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            Type baseType = typeof(XRAsset);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type?[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types ?? [];
                }

                foreach (Type? type in types)
                {
                    if (type is null || !baseType.IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                        continue;

                    var attribute = type.GetCustomAttribute<XR3rdPartyExtensionsAttribute>();
                    if (attribute is null)
                        continue;

                    foreach (var (ext, _) in attribute.Extensions)
                    {
                        if (string.IsNullOrWhiteSpace(ext))
                            continue;

                        string normalized = ext.TrimStart('.');
                        if (!map.TryGetValue(normalized, out var existing))
                        {
                            map.Add(normalized, type);
                            continue;
                        }

                        // If multiple asset types claim the same extension, prefer prefab sources.
                        // This ensures model formats (fbx/obj/etc.) import into scene hierarchies with bones + ModelComponents
                        // rather than collapsing into a single Model asset.
                        if (typeof(XREngine.Scene.Prefabs.XRPrefabSource).IsAssignableFrom(type) &&
                            !typeof(XREngine.Scene.Prefabs.XRPrefabSource).IsAssignableFrom(existing))
                        {
                            map[normalized] = type;
                        }
                    }
                }
            }

            return map;
        }

        private static bool HasNativeAssetExtension(string filePath)
            => string.Equals(Path.GetExtension(filePath), $".{AssetExtension}", StringComparison.OrdinalIgnoreCase);

        private static bool TryGetThirdPartyExtension(string filePath, [NotNullWhen(true)] out string? normalizedExtension)
        {
            normalizedExtension = null;
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            // Handle multi-dot extensions first (Path.GetExtension only returns the last segment).
            if (filePath.EndsWith(".mesh.xml", StringComparison.OrdinalIgnoreCase))
            {
                normalizedExtension = "mesh.xml";
                return true;
            }

            string ext = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(ext) || ext.Length <= 1)
                return false;

            normalizedExtension = ext[1..].ToLowerInvariant();
            if (string.Equals(normalizedExtension, AssetExtension, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private bool TryResolveGeneratedAssetPathForThirdPartySource(string sourcePath, out string generatedAssetPath)
        {
            generatedAssetPath = string.Empty;
            if (string.IsNullOrWhiteSpace(sourcePath))
                return false;

            if (!IsPathUnderGameAssets(sourcePath))
                return false;

            if (HasNativeAssetExtension(sourcePath))
                return false;

            string? directory = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            string name = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(name))
                return false;

            generatedAssetPath = Path.Combine(directory, $"{name}.{AssetExtension}");
            return true;
        }

        private static Type ResolveImportOptionsType(Type assetType)
        {
            var attr = assetType.GetCustomAttribute<XR3rdPartyExtensionsAttribute>();
            var optionsType = attr?.ImportOptionsType;
            return optionsType ?? typeof(XREngine.Data.XRDefault3rdPartyImportOptions);
        }

        private bool TryResolveImportOptionsPath(string sourcePath, Type assetType, out string importOptionsPath)
        {
            importOptionsPath = string.Empty;

            EnsureGameMetadataPathInitialized();
            EnsureGameCachePathInitialized();
            if (string.IsNullOrWhiteSpace(GameCachePath) || string.IsNullOrWhiteSpace(GameAssetsPath))
                return false;

            string normalizedAssets = Path.GetFullPath(GameAssetsPath);
            string normalizedSource = Path.GetFullPath(sourcePath);
            string relativePath = Path.GetRelativePath(normalizedAssets, normalizedSource);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
                return false;

            string? relativeDirectory = Path.GetDirectoryName(relativePath);
            string originalFileName = Path.GetFileName(relativePath);
            string typeSuffix = assetType.FullName ?? assetType.Name;
            string fileName = $"{originalFileName}.{typeSuffix}.{ImportOptionsFileExtension}";

            importOptionsPath = string.IsNullOrWhiteSpace(relativeDirectory)
                ? Path.Combine(GameCachePath!, fileName)
                : Path.Combine(GameCachePath!, relativeDirectory, fileName);
            return true;
        }

        public bool TryGetThirdPartyImportContext(string sourcePath, Type assetType, [NotNullWhen(true)] out object? importOptions, out string importOptionsPath, out string generatedAssetPath)
        {
            importOptions = null;
            importOptionsPath = string.Empty;
            generatedAssetPath = string.Empty;

            if (string.IsNullOrWhiteSpace(sourcePath) || assetType is null)
                return false;

            if (!TryResolveGeneratedAssetPathForThirdPartySource(sourcePath, out generatedAssetPath))
                return false;

            if (!TryResolveImportOptionsPath(sourcePath, assetType, out importOptionsPath))
                return false;

            importOptions = GetOrCreateThirdPartyImportOptions(sourcePath, assetType);
            return importOptions is not null;
        }

        public object? GetOrCreateThirdPartyImportOptions(string sourcePath, Type assetType)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || assetType is null)
                return null;

            Type optionsType = ResolveImportOptionsType(assetType);
            if (!TryResolveImportOptionsPath(sourcePath, assetType, out string optionsPath))
                return Activator.CreateInstance(optionsType);

            if (_thirdPartyImportOptionsCache.TryGetValue(optionsPath, out object? cached))
                return cached;

            try
            {
                if (File.Exists(optionsPath))
                {
                    string yaml = File.ReadAllText(optionsPath);
                    var loaded = Deserializer.Deserialize(yaml, optionsType);
                    if (loaded is not null)
                    {
                        _thirdPartyImportOptionsCache[optionsPath] = loaded;
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to read import options '{optionsPath}'. {ex.Message}");
            }

            object? created = null;
            try
            {
                created = Activator.CreateInstance(optionsType);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to create import options of type '{optionsType.FullName}'. {ex.Message}");
                return null;
            }

            try
            {
                string? directory = Path.GetDirectoryName(optionsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string yaml = Serializer.Serialize(created);
                File.WriteAllText(optionsPath, yaml, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to write default import options '{optionsPath}'. {ex.Message}");
            }

            if (created is not null)
                _thirdPartyImportOptionsCache[optionsPath] = created;

            return created;
        }

        public bool SaveThirdPartyImportOptions(string sourcePath, Type assetType, object importOptions)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || assetType is null || importOptions is null)
                return false;

            if (!TryResolveImportOptionsPath(sourcePath, assetType, out string optionsPath))
                return false;

            try
            {
                string? directory = Path.GetDirectoryName(optionsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string yaml = Serializer.Serialize(importOptions);
                File.WriteAllText(optionsPath, yaml, Encoding.UTF8);
                _thirdPartyImportOptionsCache[optionsPath] = importOptions;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to save import options '{optionsPath}'. {ex.Message}");
                return false;
            }
        }

        public bool ReimportThirdPartyFile(string sourcePath)
            => RunOnJobThreadBlocking(() => ImportThirdPartyToNativeAssetCore(sourcePath, forceOverwrite: true), JobPriority.Normal, bypassJobThread: false);

        public Task<bool> ReimportThirdPartyFileAsync(string sourcePath)
            => RunOnJobThreadAsync(() => ImportThirdPartyToNativeAssetCore(sourcePath, forceOverwrite: true), JobPriority.Normal, bypassJobThread: false);

        private void TryQueueAutoImportForThirdPartyFile(string path, string reason)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            // Skip native assets, directories, and anything outside the project's Assets root.
            if (HasNativeAssetExtension(path) || Directory.Exists(path) || !IsPathUnderGameAssets(path))
                return;

            if (!TryGetThirdPartyExtension(path, out var normalizedExtension))
                return;

            var map = ThirdPartyExtensionMap.Value;
            if (!map.TryGetValue(normalizedExtension, out var assetType))
                return;

            // Schedule on the job system so file watcher threads stay lightweight.
            _ = RunOnJobThreadAsync(() =>
            {
                try
                {
                    ImportThirdPartyToNativeAssetCore(path, forceOverwrite: false);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Auto-import failed for '{path}' ({reason}). {ex.Message}");
                }
                return true;
            }, JobPriority.Low, bypassJobThread: false);
        }

        private bool ImportThirdPartyToNativeAssetCore(string sourcePath, bool forceOverwrite)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                Debug.LogWarning("Source path is null or empty.");
                return false;
            }

            if (HasNativeAssetExtension(sourcePath))
            {
                Debug.LogWarning($"Source path '{sourcePath}' has native asset extension; skipping third-party import.");
                return false;
            }

            if (!File.Exists(sourcePath))
            {
                Debug.LogWarning($"Source file '{sourcePath}' does not exist.");
                return false;
            }

            if (!TryGetThirdPartyExtension(sourcePath, out var normalizedExtension))
            {
                Debug.LogWarning($"Source file '{sourcePath}' does not have a recognized third-party extension.");
                return false;
            }

            var map = ThirdPartyExtensionMap.Value;
            if (!map.TryGetValue(normalizedExtension, out var assetType))
            {
                Debug.LogWarning($"No asset type registered for third-party extension '{normalizedExtension}'.");
                return false;
            }

            if (!TryResolveGeneratedAssetPathForThirdPartySource(sourcePath, out string generatedAssetPath))
            {
                Debug.LogWarning($"Failed to resolve generated asset path for source '{sourcePath}'.");
                return false;
            }

            // If the target asset already exists, only overwrite when it's linked to this source.
            if (File.Exists(generatedAssetPath))
            {
                // For a user-triggered forced reimport we will overwrite the generated asset
                // unconditionally; avoid deserializing the old YAML since it's about to be replaced.
                if (!forceOverwrite)
                {
                    bool linked = false;
                    try
                    {
                        var existing = DeserializeAssetFile(generatedAssetPath, assetType);
                        if (existing is not null && string.Equals(existing.OriginalPath, sourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            // Only re-import if source is newer.
                            var sourceTime = SafeGetLastWriteTimeUtc(sourcePath);
                            if (sourceTime is not null && (existing.OriginalLastWriteTimeUtc is null || existing.OriginalLastWriteTimeUtc.Value < sourceTime.Value))
                                linked = true;
                        }
                        else
                        {
                            // Debug.LogWarning($"Existing asset '{generatedAssetPath}' is not linked to source '{sourcePath}'.");
                        }
                    }
                    catch
                    {
                        linked = false;
                        Debug.LogWarning($"Failed to deserialize existing asset '{generatedAssetPath}'.");
                    }

                    if (!linked)
                        return false;
                }
            }

            object? importOptions = GetOrCreateThirdPartyImportOptions(sourcePath, assetType);

            // When generating a prefab asset we must serialize sub-assets (Models, SubMeshes, etc.)
            // immediately after import. Force synchronous mesh processing so Model.Meshes is fully
            // populated before ExternalizeEmbeddedAssetsForPrefabImport runs.
            if (typeof(XRPrefabSource).IsAssignableFrom(assetType) && importOptions is ModelImportOptions modelOpts)
                modelOpts.ProcessMeshesAsynchronously ??= false;

            if (Activator.CreateInstance(assetType) is not XRAsset asset)
            {
                Debug.LogWarning($"Failed to create asset instance for '{assetType.FullName}'.");
                return false;
            }

            asset.Name = Path.GetFileNameWithoutExtension(sourcePath);
            asset.OriginalPath = sourcePath;
            asset.OriginalLastWriteTimeUtc = SafeGetLastWriteTimeUtc(sourcePath);

            // Make the generated asset path available during import so importers can resolve
            // stable sibling output paths (e.g., externalized meshes/materials/textures).
            asset.FilePath = generatedAssetPath;

            bool ok = asset.Import3rdParty(sourcePath, importOptions);
            if (!ok)
                return false;

            // For PrefabSource model imports, export generated sub-assets (materials/textures/meshes/models)
            // into standalone .asset files so the prefab does not embed them.
            if (asset is XRPrefabSource)
                ExternalizeEmbeddedAssetsForPrefabImport(asset, generatedAssetPath);

            string? directory = Path.GetDirectoryName(generatedAssetPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            MarkRecentlySaved(generatedAssetPath);
            asset.SerializeTo(generatedAssetPath, Serializer);

            MarkRecentlySaved(generatedAssetPath);
            return true;
        }

        private void ExternalizeEmbeddedAssetsForPrefabImport(XRAsset rootAsset, string rootAssetPath)
        {
            ArgumentNullException.ThrowIfNull(rootAsset);

            Debug.Log(ELogCategory.General, "[ExternalizeEmbedded] Starting externalization for '{0}' (root type: {1})", rootAssetPath, rootAsset.GetType().Name);

            string? directory = Path.GetDirectoryName(rootAssetPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                Debug.LogWarning($"[ExternalizeEmbedded] Cannot externalize: directory is null/empty for path '{rootAssetPath}'");
                return;
            }

            string rootName = Path.GetFileNameWithoutExtension(rootAssetPath);
            string prefabFolderName = SanitizeExternalizedFileName(rootName);
            string prefabFolderPath = Path.Combine(directory, prefabFolderName);

            static bool IsPathUnderDirectory(string filePath, string directoryPath)
            {
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(directoryPath))
                    return false;

                string fullFilePath = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullDirectoryPath = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(fullFilePath, fullDirectoryPath, StringComparison.OrdinalIgnoreCase)
                    || fullFilePath.StartsWith(fullDirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || fullFilePath.StartsWith(fullDirectoryPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }

            bool suspendGameWatcher = IsPathUnderDirectory(rootAssetPath, GameAssetsPath);
            bool suspendEngineWatcher = IsPathUnderDirectory(rootAssetPath, EngineAssetsPath);
            bool previousGameWatcherState = MonitorGameAssetsForChanges;
            bool previousEngineWatcherState = MonitorEngineAssetsForChanges;

            try
            {
                if (suspendGameWatcher && previousGameWatcherState)
                    MonitorGameAssetsForChanges = false;
                if (suspendEngineWatcher && previousEngineWatcherState)
                    MonitorEngineAssetsForChanges = false;

                // ── Phase A: Discovery ───────────────────────────────────────
                List<XRAsset> discovered = DiscoverRelevantSubAssets(rootAsset);
                Debug.Log(ELogCategory.General, "[ExternalizeEmbedded] Discovered {0} relevant sub-assets", discovered.Count);
                {
                    int textures = discovered.Count(IsExternalizableTexture);
                    int materials = discovered.Count(IsExternalizableMaterial);
                    int subMeshes = discovered.Count(IsExternalizableSubMesh);
                    int meshes = discovered.Count(IsExternalizableMesh);
                    int models = discovered.Count(IsExternalizableModel);
                    int animations = discovered.Count(IsExternalizableAnimationClip);
                    Debug.Log(ELogCategory.General, "[ExternalizeEmbedded] Breakdown: Texture={0}, Material={1}, SubMesh={2}, Mesh={3}, Model={4}, AnimationClip={5}", textures, materials, subMeshes, meshes, models, animations);
                }

                // ── Phase B: Pre-assign paths + write placeholder files ──────
                var createdPlaceholders = new List<string>();
                try
                {
                    PreAssignExternalizationPaths(discovered, prefabFolderPath, rootName, createdPlaceholders);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, "[ExternalizeEmbedded] Phase B (pre-assign) failed; rolling back placeholders.");
                    DeletePlaceholders(createdPlaceholders);
                    throw;
                }

                // ── Phase C: Topological write, leaves-first ─────────────────
                int exportedCount = 0;
                int skippedCount = 0;
                int failedCount = 0;
                var overwrittenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    foreach (XRAsset subAsset in discovered.OrderBy(KindOrderLeavesFirst))
                    {
                        string? targetPath = subAsset.FilePath;
                        if (string.IsNullOrWhiteSpace(targetPath))
                        {
                            skippedCount++;
                            continue;
                        }

                        try
                        {
                            Debug.Log(ELogCategory.General, "[ExternalizeEmbedded] Exporting {0} '{1}' -> '{2}'", subAsset.GetType().Name, subAsset.Name ?? string.Empty, targetPath);
                            SaveAssetToPathCore(subAsset, targetPath);
                            EnsureMetadataForAssetPath(targetPath, isDirectory: false);
                            overwrittenPaths.Add(targetPath);
                            exportedCount++;
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            Debug.LogException(ex, $"[ExternalizeEmbedded] Failed exporting {subAsset.GetType().Name} '{subAsset.Name}' -> '{targetPath}'");
                        }
                    }
                }
                finally
                {
                    // Delete any placeholders that were never overwritten by a real serialization.
                    List<string> orphans = createdPlaceholders.Where(p => !overwrittenPaths.Contains(p)).ToList();
                    if (orphans.Count > 0)
                    {
                        Debug.LogWarning($"[ExternalizeEmbedded] Cleaning up {orphans.Count} placeholder file(s) that were never written.");
                        DeletePlaceholders(orphans);
                    }
                }

                Debug.Log(ELogCategory.General, "[ExternalizeEmbedded] Externalization complete: {0} exported, {1} skipped, {2} failed", exportedCount, skippedCount, failedCount);

                // Recompute to ensure the root no longer treats these as embedded.
                XRAssetGraphUtility.RefreshAssetGraph(rootAsset);
            }
            finally
            {
                if (suspendGameWatcher)
                    MonitorGameAssetsForChanges = previousGameWatcherState;
                if (suspendEngineWatcher)
                    MonitorEngineAssetsForChanges = previousEngineWatcherState;
            }
        }

        // ── Classification helpers ───────────────────────────────────────

        private static bool IsExternalizableTexture(XRAsset a) => a is XRTexture;
        private static bool IsExternalizableMaterial(XRAsset a) => a is XRMaterialBase;
        private static bool IsExternalizableSubMesh(XRAsset a) => a is SubMesh;
        private static bool IsExternalizableMesh(XRAsset a) => a is XRMesh;
        private static bool IsExternalizableModel(XRAsset a) => a is Model;
        private static bool IsExternalizableAnimationClip(XRAsset a) => a is AnimationClip;

        private static bool IsExternalizable(XRAsset a)
            => IsExternalizableTexture(a)
            || IsExternalizableMaterial(a)
            || IsExternalizableSubMesh(a)
            || IsExternalizableMesh(a)
            || IsExternalizableModel(a)
            || IsExternalizableAnimationClip(a);

        /// <summary>
        /// Leaves-first ordering so nested references resolve to already-written files.
        /// </summary>
        private static int KindOrderLeavesFirst(XRAsset a)
        {
            if (IsExternalizableTexture(a)) return 0;
            if (IsExternalizableMaterial(a)) return 1;
            if (IsExternalizableSubMesh(a)) return 2;
            if (IsExternalizableMesh(a)) return 3;
            if (IsExternalizableModel(a)) return 4;
            if (IsExternalizableAnimationClip(a)) return 5;
            return 10;
        }

        private static string KindFolderNameFor(XRAsset a)
        {
            if (IsExternalizableTexture(a)) return "Textures";
            if (IsExternalizableMaterial(a)) return "Materials";
            if (IsExternalizableSubMesh(a)) return "SubMeshes";
            if (IsExternalizableMesh(a)) return "Meshes";
            if (IsExternalizableModel(a)) return "Models";
            if (IsExternalizableAnimationClip(a)) return "Animations";
            return "Assets";
        }

        private static string SanitizeExternalizedFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Asset";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            name = name.Trim().TrimEnd('.', ' ');
            return string.IsNullOrWhiteSpace(name) ? "Asset" : name;
        }

        private static string EnsureAssetHasName(XRAsset asset, string rootName)
        {
            if (!string.IsNullOrWhiteSpace(asset.Name))
                return asset.Name!;

            string shortId = asset.ID == Guid.Empty
                ? Guid.NewGuid().ToString("N")[..8]
                : asset.ID.ToString("N")[..8];
            asset.Name = $"{rootName}_{asset.GetType().Name}_{shortId}";
            return asset.Name;
        }

        private static bool HasValidExistingAssetFile(XRAsset a)
        {
            string? path = a.FilePath;
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (!string.Equals(Path.GetExtension(path), $".{AssetExtension}", StringComparison.OrdinalIgnoreCase))
                return false;
            return File.Exists(path);
        }

        // ── Phase A: Discovery ───────────────────────────────────────────

        private List<XRAsset> DiscoverRelevantSubAssets(XRAsset rootAsset)
        {
            XRAssetGraphUtility.RefreshAssetGraph(rootAsset);

            IEnumerable<XRAsset> fromGraph = rootAsset.EmbeddedAssets
                .Where(a => a is not null && !ReferenceEquals(a, rootAsset) && IsExternalizable(a));

            object traversalRoot = rootAsset;
            if (rootAsset is XRPrefabSource prefab && prefab.RootNode is not null)
                traversalRoot = prefab.RootNode;

            IEnumerable<XRAsset> fromReachable = CollectReachableAssets(traversalRoot)
                .Where(a => a is not null && !ReferenceEquals(a, rootAsset) && IsExternalizable(a));

            return fromGraph
                .Concat(fromReachable)
                .Distinct(XRAssetReferenceEqualityComparer.Instance)
                .ToList();
        }

        // ── Phase B: Pre-assign paths and touch placeholder files ────────

        private void PreAssignExternalizationPaths(
            IReadOnlyList<XRAsset> discovered,
            string prefabFolderPath,
            string rootName,
            List<string> createdPlaceholders)
        {
            if (discovered.Count == 0)
                return;

            // Track claimed filenames per kind folder so collisions are deduped deterministically.
            var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (XRAsset subAsset in discovered)
            {
                // Embedded assets typically have SourceAsset == rootAsset. Self-root first so FilePath
                // reflects this sub-asset's own path, enabling correct reference emission later.
                if (!ReferenceEquals(subAsset.SourceAsset, subAsset))
                    subAsset.SourceAsset = subAsset;

                if (subAsset.ID == Guid.Empty)
                    Debug.LogWarning($"[ExternalizeEmbedded] Sub-asset {subAsset.GetType().Name} has empty ID; references to it may fail to resolve.");

                // Shared-asset case: asset already has a valid on-disk .asset file under our control.
                if (HasValidExistingAssetFile(subAsset))
                {
                    claimedPaths.Add(subAsset.FilePath!);
                    continue;
                }

                string kindFolder = KindFolderNameFor(subAsset);
                string kindFolderPath = Path.Combine(prefabFolderPath, kindFolder);

                Directory.CreateDirectory(prefabFolderPath);
                Directory.CreateDirectory(kindFolderPath);
                EnsureMetadataForAssetPath(prefabFolderPath, isDirectory: true);
                EnsureMetadataForAssetPath(kindFolderPath, isDirectory: true);

                string displayName = EnsureAssetHasName(subAsset, rootName);
                string safeName = SanitizeExternalizedFileName(displayName);

                string candidatePath = Path.Combine(kindFolderPath, $"{safeName}.{AssetExtension}");
                string targetPath = ReserveUniqueAssetPath(candidatePath, subAsset, claimedPaths);
                claimedPaths.Add(targetPath);

                subAsset.FilePath = targetPath;

                // Write a placeholder file so XRAssetYamlConverter.ShouldWriteReference's File.Exists
                // check passes while nested references are emitted during Phase C.
                try
                {
                    if (!File.Exists(targetPath))
                    {
                        using (var _ = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                        createdPlaceholders.Add(targetPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"[ExternalizeEmbedded] Failed creating placeholder '{targetPath}' for {subAsset.GetType().Name} '{displayName}'.");
                    throw;
                }
            }
        }

        private static string ReserveUniqueAssetPath(string candidatePath, XRAsset subAsset, HashSet<string> claimedPaths)
        {
            if (!File.Exists(candidatePath) && !claimedPaths.Contains(candidatePath))
                return candidatePath;

            string directory = Path.GetDirectoryName(candidatePath) ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(candidatePath);
            string extension = Path.GetExtension(candidatePath);

            string shortId = subAsset.ID == Guid.Empty
                ? Guid.NewGuid().ToString("N")[..8]
                : subAsset.ID.ToString("N")[..8];

            string idScopedPath = Path.Combine(directory, $"{fileName}_{shortId}{extension}");
            if (!File.Exists(idScopedPath) && !claimedPaths.Contains(idScopedPath))
                return idScopedPath;

            // Last-resort numeric dedup.
            for (int i = 2; i < 10000; i++)
            {
                string numbered = Path.Combine(directory, $"{fileName}_{shortId}_{i}{extension}");
                if (!File.Exists(numbered) && !claimedPaths.Contains(numbered))
                    return numbered;
            }

            throw new IOException($"Could not reserve unique asset path near '{candidatePath}'.");
        }

        private static void DeletePlaceholders(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var fi = new FileInfo(path);
                        // Only delete zero-byte files — anything larger has real content now.
                        if (fi.Length == 0)
                            File.Delete(path);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"[ExternalizeEmbedded] Failed to clean up placeholder '{path}'.");
                }
            }
        }

        private void SaveAssetToPathCore(XRAsset asset, string filePath)
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            asset.FilePath = filePath;
            MarkRecentlySaved(filePath);
            XRAssetGraphUtility.RefreshAssetGraph(asset);
            asset.SerializeTo(filePath, Serializer);
            PostSaved(asset, newAsset: true);
        }

        private static IEnumerable<XRAsset> CollectReachableAssets(object? root)
        {
            if (root is null)
            {
                //Debug.Log(ELogCategory.General, "[CollectReachable] Root is null, returning empty.");
                yield break;
            }

            //Debug.Log(ELogCategory.General, "[CollectReachable] Starting traversal from root type: {0}", root.GetType().Name);

            static bool ShouldReflectInto(Type type)
            {
                // Avoid reflecting into framework/runtime/serializer internals; it can be slow and
                // (in some cases) crash the runtime (e.g., RuntimeTypeCache).
                if (type.IsPrimitive || type.IsEnum || type.IsPointer || type.IsValueType)
                    return false;

                if (typeof(string).IsAssignableFrom(type))
                    return false;

                if (typeof(Type).IsAssignableFrom(type))
                    return false;

                if (typeof(MemberInfo).IsAssignableFrom(type) || typeof(Assembly).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type))
                    return false;

                string? ns = type.Namespace;
                if (!string.IsNullOrWhiteSpace(ns))
                {
                    if (ns.StartsWith("System", StringComparison.Ordinal)
                        || ns.StartsWith("Microsoft", StringComparison.Ordinal)
                        || ns.StartsWith("YamlDotNet", StringComparison.Ordinal)
                        || ns.StartsWith("Newtonsoft", StringComparison.Ordinal))
                        return false;

                    if (ns.StartsWith("XREngine", StringComparison.Ordinal)
                        || ns.StartsWith("XRENGINE", StringComparison.Ordinal)
                        || ns.StartsWith("Extensions", StringComparison.Ordinal))
                        return true;
                }

                // Heuristic for user/game assemblies: allow reflection for assemblies built against the engine.
                // This keeps traversal focused on engine + user project code, while skipping unrelated libraries.
                string engineAssemblyName = typeof(XRAsset).Assembly.GetName().Name ?? string.Empty;
                try
                {
                    return type.Assembly == typeof(XRAsset).Assembly
                        || type.Assembly.GetReferencedAssemblies().Any(a => string.Equals(a.Name, engineAssemblyName, StringComparison.Ordinal));
                }
                catch
                {
                    return false;
                }
            }

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            stack.Push(root);

            const int maxDepth = 64;
            var depths = new Dictionary<object, int>(ReferenceEqualityComparer.Instance)
            {
                [root] = 0,
            };

            int visitedCount = 0;
            int assetsFound = 0;

            static IEnumerable<FieldInfo> GetAllInstanceFields(Type type, Func<Type, bool> shouldReflectInto)
            {
                // Important: Type.GetFields(BindingFlags.Instance|Public|NonPublic) does NOT include
                // private fields declared on base classes.
                // Many core relationships (e.g., TransformBase._children) live in base private fields,
                // so we must walk the inheritance chain and include DeclaredOnly fields at each level.
                for (Type? t = type; t is not null; t = t.BaseType)
                {
                    if (!shouldReflectInto(t))
                        continue;

                    foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        yield return field;
                }
            }

            static bool TryGetEnumerableElementType(Type enumerableType, [NotNullWhen(true)] out Type? elementType)
            {
                elementType = null;

                if (enumerableType.IsArray)
                {
                    elementType = enumerableType.GetElementType();
                    return elementType is not null;
                }

                foreach (Type iface in enumerableType.GetInterfaces())
                {
                    if (!iface.IsGenericType)
                        continue;

                    if (iface.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                        continue;

                    elementType = iface.GetGenericArguments()[0];
                    return true;
                }

                return false;
            }

            static bool ShouldSkipEnumerableTraversal(Type elementType)
            {
                // Skip trivially-safe/value-only containers; they cannot hold XRAsset references.
                if (elementType.IsPrimitive || elementType.IsEnum || elementType.IsPointer || elementType.IsValueType)
                    return true;

                // Also skip common "pair" wrappers that only contain value types.
                if (elementType.IsGenericType)
                {
                    Type def = elementType.GetGenericTypeDefinition();
                    if (def == typeof(Tuple<,>))
                    {
                        var args = elementType.GetGenericArguments();
                        if (args.Length == 2 && args[0].IsValueType && args[1].IsValueType)
                            return true;
                    }
                }

                return false;
            }

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current is null)
                    continue;

                if (!visited.Add(current))
                    continue;

                visitedCount++;

                int depth = depths.TryGetValue(current, out int d) ? d : 0;
                if (depth > maxDepth)
                {
                    //Debug.Log(ELogCategory.General, "[CollectReachable] Max depth reached for {0}", current.GetType().Name);
                    continue;
                }

                if (current is XRAsset asset)
                {
                    assetsFound++;
                    //Debug.Log(ELogCategory.General, "[CollectReachable] Found XRAsset: {0} '{1}' at depth {2}", asset.GetType().Name, asset.Name ?? "(unnamed)", depth);
                    yield return asset;
                }

                Type type = current.GetType();
                if (type.IsPrimitive || type.IsEnum || type.IsPointer || type.IsValueType || current is string || current is Type)
                    continue;

                // Renderers are thread-affine and often expose properties that call into native APIs (OpenGL/Vulkan).
                // Asset graph traversal should never reflect into them.
                if (current is AbstractRenderer)
                    continue;

                if (current is IDictionary dictionary)
                {
                    int i = 0;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Key is not null)
                        {
                            depths[entry.Key] = depth;
                            stack.Push(entry.Key);
                        }
                        if (entry.Value is not null)
                        {
                            depths[entry.Value] = depth;
                            stack.Push(entry.Value);
                        }
                        if (++i > 2000)
                            break;
                    }
                    continue;
                }

                if (current is IEnumerable enumerable && current is not string)
                {
                    if (TryGetEnumerableElementType(type, out Type? elementType) && elementType is not null)
                    {
                        if (ShouldSkipEnumerableTraversal(elementType))
                            continue;
                    }

                    int count = 0;
                    const int maxItems = 2000;

                    // Prefer non-enumerator traversal to avoid List<T>'s versioned enumerator throwing
                    // "Collection was modified" while we are reflecting through live runtime objects.
                    if (current is Array array)
                    {
                        int n = Math.Min(array.Length, maxItems);
                        for (int idx = 0; idx < n; idx++)
                        {
                            object? item = array.GetValue(idx);
                            if (item is null)
                                continue;
                            depths[item] = depth;
                            stack.Push(item);
                            count++;
                        }
                    }
                    else if (current is IList list)
                    {
                        int n;
                        try { n = Math.Min(list.Count, maxItems); }
                        catch (InvalidOperationException) { continue; }

                        for (int idx = 0; idx < n; idx++)
                        {
                            object? item;
                            try { item = list[idx]; }
                            catch (InvalidOperationException) { break; }
                            catch (ArgumentOutOfRangeException) { break; }

                            if (item is null)
                                continue;
                            depths[item] = depth;
                            stack.Push(item);
                            count++;
                        }
                    }
                    else
                    {
                        try
                        {
                            int i = 0;
                            foreach (var item in enumerable)
                            {
                                if (item is null)
                                    continue;
                                depths[item] = depth;
                                stack.Push(item);
                                count++;
                                if (++i > maxItems)
                                    break;
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // Collection was modified during traversal; ignore and continue.
                        }
                    }

                    // Always log for EventList types to help diagnose empty children issues
                    //if (type.Name.Contains("EventList") || (count > 0 && depth <= 5))
                    //    Debug.Log(ELogCategory.General, "[CollectReachable] Traversing IEnumerable {0} with {1} items at depth {2}", type.Name, count, depth);
                    continue;
                }

                // Only reflect into engine/user code objects; skip framework/runtime/library internals.
                if (!ShouldReflectInto(type))
                    continue;

                // Important: do NOT call property getters here.
                // Some getters have side effects or require a specific thread/context (e.g., OpenGLRenderer.Version).
                // Field traversal (incl. auto-property backing fields) is sufficient for asset reference discovery.

                var fields = GetAllInstanceFields(type, ShouldReflectInto).ToArray();
                
                // Extra detailed logging for types likely to contain materials/meshes
                bool isInterestingType = type.Name.Contains("SubMesh", StringComparison.Ordinal)
                    || type.Name.Contains("Model", StringComparison.Ordinal)
                    || type.Name.Contains("Material", StringComparison.Ordinal)
                    || type.Name.Contains("Mesh", StringComparison.Ordinal)
                    || type.Name.Contains("SceneNode", StringComparison.Ordinal)
                    || type.Name.Contains("Transform", StringComparison.Ordinal)
                    || type.Name.Contains("Component", StringComparison.Ordinal);
                
                if (depth <= 5 || isInterestingType)
                {
                    //Debug.Log(ELogCategory.General, "[CollectReachable] Reflecting into {0} at depth {1}, fields: {2}", type.Name, depth, fields.Length);
                    if (isInterestingType)
                    {
                        foreach (var f in fields)
                        {
                            object? val = null;
                            try { val = f.GetValue(current); }
                            catch { }
                            //Debug.Log(ELogCategory.General, "[CollectReachable]   field '{0}' ({1}) = {2}", 
                            //    f.Name, 
                            //    f.FieldType.Name, 
                            //    val is null ? "null" : val.GetType().Name);
                        }
                    }
                }

                static bool ShouldSkipField(FieldInfo field)
                {
                    // These fields tend to drag traversal into runtime/editor state (world, renderer, etc)
                    // and produce lots of non-import assets. They are not part of prefab content.
                    if (field.Name.Contains("RenderInfo", StringComparison.Ordinal))
                        return true;
                    if (string.Equals(field.Name, "_world", StringComparison.Ordinal))
                        return true;
                    return false;
                }

                foreach (var field in fields)
                {
                    if (ShouldSkipField(field))
                        continue;

                    object? value;
                    try { value = field.GetValue(current); }
                    catch { continue; }

                    if (value is null)
                        continue;

                    // Special logging for _children field to diagnose traversal issues
                    if (field.Name == "_children" && value is IEnumerable childList)
                    {
                        int childCount = 0;
                        foreach (var _ in childList) childCount++;
                        //Debug.Log(ELogCategory.General, "[CollectReachable] Found _children field on {0} (declared on {1}) with {2} children", type.Name, field.DeclaringType?.Name ?? "<unknown>", childCount);
                    }

                    depths[value] = depth + 1;
                    stack.Push(value);
                }
            }

            //Debug.Log(ELogCategory.General, "[CollectReachable] Traversal complete: visited {0} objects, found {1} XRAssets", visitedCount, assetsFound);
        }

        private sealed class XRAssetReferenceEqualityComparer : IEqualityComparer<XRAsset>
        {
            public static XRAssetReferenceEqualityComparer Instance { get; } = new();

            public bool Equals(XRAsset? x, XRAsset? y)
                => ReferenceEquals(x, y);

            public int GetHashCode(XRAsset obj)
                => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private void HandleThirdPartyImportOptionsRenamed(string oldPath, string newPath)
        {
            if (!IsPathUnderGameAssets(oldPath) || !IsPathUnderGameAssets(newPath))
                return;

            if (HasNativeAssetExtension(oldPath) || HasNativeAssetExtension(newPath))
                return;

            if (!TryGetThirdPartyExtension(newPath, out var newExt))
                return;

            var map = ThirdPartyExtensionMap.Value;
            if (!map.TryGetValue(newExt, out var assetType))
                return;

            if (!TryResolveImportOptionsPath(oldPath, assetType, out string oldOptionsPath))
                return;
            if (!TryResolveImportOptionsPath(newPath, assetType, out string newOptionsPath))
                return;

            try
            {
                if (!File.Exists(oldOptionsPath))
                    return;

                string? newDir = Path.GetDirectoryName(newOptionsPath);
                if (!string.IsNullOrWhiteSpace(newDir))
                    Directory.CreateDirectory(newDir);

                File.Move(oldOptionsPath, newOptionsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to move import options '{oldOptionsPath}' -> '{newOptionsPath}'. {ex.Message}");
            }
        }

        private void HandleThirdPartyImportOptionsDeleted(string sourcePath)
        {
            if (!IsPathUnderGameAssets(sourcePath))
                return;
            if (HasNativeAssetExtension(sourcePath))
                return;

            if (!TryGetThirdPartyExtension(sourcePath, out var ext))
                return;

            var map = ThirdPartyExtensionMap.Value;
            if (!map.TryGetValue(ext, out var assetType))
                return;

            if (!TryResolveImportOptionsPath(sourcePath, assetType, out string optionsPath))
                return;

            try
            {
                if (File.Exists(optionsPath))
                    File.Delete(optionsPath);
            }
            catch
            {
                // best-effort cleanup.
            }
        }
    }
}
