using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Runtime.AotParity;
using XREngine.Diagnostics;

namespace XREngine
{
    public partial class AssetManager
    {
        /// <summary>
        /// Assigns the archives for the three published content roots. Each archive opens once on
        /// first use and stays open until the root is reconfigured or the runtime shuts down.
        /// </summary>
        public static void ConfigurePublishedArchives(
            string? configArchivePath,
            string? gameContentArchivePath,
            string? engineContentArchivePath)
        {
            if (!string.IsNullOrWhiteSpace(configArchivePath)
                || !string.IsNullOrWhiteSpace(gameContentArchivePath)
                || !string.IsNullOrWhiteSpace(engineContentArchivePath))
                EnsureDirectHostAssetFileAccess();
            _publishedConfigArchivePath = NormalizeExistingArchivePath(configArchivePath);
            _publishedGameContentArchivePath = NormalizeExistingArchivePath(gameContentArchivePath);
            _publishedEngineContentArchivePath = NormalizeExistingArchivePath(engineContentArchivePath);

            PublishedArchiveRegistry.ConfigureRoot(EPublishedContentRoot.Config, _publishedConfigArchivePath);
            PublishedArchiveRegistry.ConfigureRoot(EPublishedContentRoot.GameContent, _publishedGameContentArchivePath);
            PublishedArchiveRegistry.ConfigureRoot(EPublishedContentRoot.CommonAssets, _publishedEngineContentArchivePath);
        }

        /// <summary>Closes every published archive. Called when content is swapped or the runtime shuts down.</summary>
        public static void ClosePublishedArchives()
            => PublishedArchiveRegistry.Reset();

        private static bool TryLoadPublishedAssetFromArchive<T>(
            string filePath,
            [NotNullWhen(true)] out T? asset) where T : XRAsset, new()
        {
            asset = default;
            if (TryLoadPublishedAssetFromArchive(filePath, typeof(T), out XRAsset? loaded) && loaded is T typed)
            {
                asset = typed;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Loads a cooked asset from the published archives through the long-lived archive handles.
        /// The payload is leased, never copied into a transient array, and deserialized through the
        /// published reader, which performs no reflection.
        /// </summary>
        private static bool TryLoadPublishedAssetFromArchive(
            string filePath,
            Type expectedType,
            [NotNullWhen(true)] out XRAsset? asset)
        {
            asset = null;

            if (!XRRuntimeEnvironment.IsPublishedBuild)
                return false;

            if (!string.Equals(Path.GetExtension(filePath), $".{AssetExtension}", StringComparison.OrdinalIgnoreCase))
                return false;

            Span<EPublishedContentRoot> rootOrder = stackalloc EPublishedContentRoot[3];
            int rootCount = ResolvePublishedRootOrder(filePath, rootOrder);
            if (rootCount == 0)
                return false;

            using AotParityPlayerPathScope parityScope = AotParityDiagnostics.EnterPlayerPath(EAotParityPlayerPathKind.PublishedContentLoad);

            List<string> assetPathCandidates = CollectArchiveAssetPathCandidates(filePath);
            for (int rootIndex = 0; rootIndex < rootCount; rootIndex++)
            {
                if (!PublishedArchiveRegistry.TryGetRoot(rootOrder[rootIndex], out PublishedArchiveHandle handle))
                    continue;

                for (int candidateIndex = 0; candidateIndex < assetPathCandidates.Count; candidateIndex++)
                {
                    string candidateAssetPath = assetPathCandidates[candidateIndex];
                    if (!handle.TryFindEntry(candidateAssetPath, out int entryIndex))
                        continue;

                    try
                    {
                        CookedPayloadLease lease = handle.ReadEntry(entryIndex);
                        try
                        {
                            asset = PublishedCookedAssetReader.LoadAsset(lease.Span, expectedType) as XRAsset;
                        }
                        finally
                        {
                            lease.Dispose();
                        }

                        if (asset is not null)
                            return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to load published asset '{candidateAssetPath}' from '{handle.FilePath}': {ex.Message}");
                    }
                }
            }

            return false;
        }

        /// <summary>Orders the three roots by how the request path classifies, writing into <paramref name="order"/>.</summary>
        private static int ResolvePublishedRootOrder(string filePath, Span<EPublishedContentRoot> order)
        {
            string normalizedPath = filePath.Replace('\\', '/');
            string fileName = Path.GetFileName(normalizedPath);
            bool looksLikeEngineAsset = normalizedPath.Contains("/Build/CommonAssets/", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.Contains("/CommonAssets/", StringComparison.OrdinalIgnoreCase);
            bool looksLikeConfigAsset = normalizedPath.Contains("/Config/", StringComparison.OrdinalIgnoreCase)
                || IsPublishedConfigAssetName(fileName);
            bool looksLikeGameAsset = normalizedPath.Contains("/Assets/", StringComparison.OrdinalIgnoreCase)
                || (!looksLikeEngineAsset && !looksLikeConfigAsset);

            int count = 0;
            if (looksLikeConfigAsset)
            {
                order[count++] = EPublishedContentRoot.Config;
                order[count++] = EPublishedContentRoot.GameContent;
                order[count++] = EPublishedContentRoot.CommonAssets;
            }
            else if (looksLikeEngineAsset)
            {
                order[count++] = EPublishedContentRoot.CommonAssets;
                order[count++] = EPublishedContentRoot.GameContent;
                order[count++] = EPublishedContentRoot.Config;
            }
            else
            {
                if (looksLikeGameAsset)
                {
                    order[count++] = EPublishedContentRoot.GameContent;
                    order[count++] = EPublishedContentRoot.CommonAssets;
                }

                order[count++] = EPublishedContentRoot.Config;
            }

            return count;
        }

        private static bool IsPublishedConfigAssetName(string fileName)
            => string.Equals(fileName, "startup.asset", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "game_settings.asset", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "user_settings.asset", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "editor_preferences.asset", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "build_settings.asset", StringComparison.OrdinalIgnoreCase);

        private static List<string> CollectArchiveAssetPathCandidates(string filePath)
        {
            List<string> results = new(6);
            string normalized = filePath.Replace('\\', '/');

            AddCandidate(results, TryExtractAfterSegment(normalized, "/Config/"));
            AddCandidate(results, TryExtractAfterSegment(normalized, "/Assets/"));
            AddCandidate(results, TryExtractAfterSegment(normalized, "/Build/CommonAssets/"));
            AddCandidate(results, TryExtractAfterSegment(normalized, "/CommonAssets/"));

            if (!Path.IsPathRooted(filePath))
                AddCandidate(results, filePath);

            AddCandidate(results, Path.GetFileName(filePath));
            return results;
        }

        private static void AddCandidate(List<string> results, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string normalized = path.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            for (int i = 0; i < results.Count; i++)
            {
                if (string.Equals(results[i], normalized, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            results.Add(normalized);
        }

        private static string? TryExtractAfterSegment(string normalizedPath, string segment)
        {
            int index = normalizedPath.LastIndexOf(segment, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return null;

            string candidate = normalizedPath[(index + segment.Length)..].TrimStart('/');
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
        }

        private static string? NormalizeExistingArchivePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                string fullPath = Path.GetFullPath(path);
                return File.Exists(fullPath) ? fullPath : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
