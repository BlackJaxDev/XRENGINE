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
            [NotNullWhen(true)] out T? asset,
            out bool searchedWithoutEntry) where T : XRAsset, new()
        {
            asset = default;
            if (TryLoadPublishedAssetFromArchive(filePath, typeof(T), out XRAsset? loaded, out searchedWithoutEntry) && loaded is T typed)
            {
                asset = typed;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Loads a cooked asset from the published archives through the long-lived archive handles.
        /// The payload is leased, never copied into a transient array. A published build deserializes it through the
        /// published reader, which performs no reflection. A development build that reads the archives uses the
        /// authoring reader, which also reads the reflective formats of a non-AOT cook.
        /// </summary>
        /// <remarks>
        /// A published build logs a failed read as a warning and returns false. A development build that reads the archives
        /// must not use a loose file in place of an archive entry, because that hides cooked content that does not load. So
        /// when at least one archive root is configured for the request, an entry that exists but does not read, has a stale
        /// contract, or deserializes to null throws an error that names the asset and the archive. A missing entry is not an
        /// error of the entry: the method records one missing-asset diagnostic, sets <paramref name="searchedWithoutEntry"/>,
        /// and returns false. The caller then returns null and does not read the loose file.
        /// </remarks>
        /// <param name="filePath">The requested asset path.</param>
        /// <param name="expectedType">The type that the caller expects.</param>
        /// <param name="asset">Receives the loaded asset.</param>
        /// <param name="searchedWithoutEntry">
        /// True when a development archive build searched at least one configured archive and found no entry. The caller
        /// must not use the loose file.
        /// </param>
        /// <exception cref="InvalidDataException">A development archive build cannot read an existing archive entry.</exception>
        internal static bool TryLoadPublishedAssetFromArchive(
            string filePath,
            Type expectedType,
            [NotNullWhen(true)] out XRAsset? asset,
            out bool searchedWithoutEntry)
        {
            asset = null;
            searchedWithoutEntry = false;

            if (!XRRuntimeEnvironment.ReadsPublishedArchives)
                return false;

            if (!string.Equals(Path.GetExtension(filePath), $".{AssetExtension}", StringComparison.OrdinalIgnoreCase))
                return false;

            Span<EPublishedContentRoot> rootOrder = stackalloc EPublishedContentRoot[3];
            int rootCount = ResolvePublishedRootOrder(filePath, rootOrder);
            if (rootCount == 0)
                return false;

            using AotParityPlayerPathScope parityScope = AotParityDiagnostics.EnterPlayerPath(EAotParityPlayerPathKind.PublishedContentLoad);

            bool failLoudly = !XRRuntimeEnvironment.IsPublishedBuild;
            List<string>? searchedArchives = null;
            List<string> assetPathCandidates = CollectArchiveAssetPathCandidates(filePath);
            for (int rootIndex = 0; rootIndex < rootCount; rootIndex++)
            {
                if (!PublishedArchiveRegistry.TryGetRoot(rootOrder[rootIndex], out PublishedArchiveHandle handle))
                    continue;

                if (failLoudly)
                    (searchedArchives ??= new List<string>(rootCount)).Add(handle.FilePath);

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
                            asset = ReadArchiveAsset(lease.Span, expectedType);
                        }
                        finally
                        {
                            lease.Dispose();
                        }

                        if (asset is not null)
                            return true;
                    }
                    catch (Exception ex) when (failLoudly)
                    {
                        throw new InvalidDataException(
                            $"Development archive build: asset '{candidateAssetPath}' in archive '{handle.FilePath}' did not load: {ex.Message} The loose file is not used in place of the archive entry. Re-cook the content.",
                            ex);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to load published asset '{candidateAssetPath}' from '{handle.FilePath}': {ex.Message}");
                    }

                    if (failLoudly)
                    {
                        throw new InvalidDataException(
                            $"Development archive build: asset '{candidateAssetPath}' in archive '{handle.FilePath}' did not deserialize as '{expectedType}'. The loose file is not used in place of the archive entry.");
                    }
                }
            }

            if (failLoudly && searchedArchives is not null)
            {
                AssetDiagnostics.RecordMissingAsset(
                    filePath,
                    expectedType.Name,
                    $"Development archive build: no entry in the configured archives ({string.Join(", ", searchedArchives)}). The loose file is not used in place of the archive entry.");
                searchedWithoutEntry = true;
            }

            return false;
        }

        /// <summary>
        /// Deserializes one archive payload. A published build always uses the published reader. A development build
        /// that reads the archives uses the authoring reader. A published engine build compiles that branch out.
        /// </summary>
        private static XRAsset? ReadArchiveAsset(ReadOnlySpan<byte> payload, Type expectedType)
        {
#if !XRE_PUBLISHED
            if (!XRRuntimeEnvironment.IsPublishedBuild)
                return ReadDevelopmentArchiveAsset(payload, expectedType);
#endif
            return PublishedCookedAssetReader.LoadAsset(payload, expectedType) as XRAsset;
        }

#if !XRE_PUBLISHED
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only a non-AOT development build that reads the archives runs this reader. A published engine build compiles it out.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only a non-AOT development build that reads the archives runs this reader. A published engine build compiles it out.")]
        private static XRAsset? ReadDevelopmentArchiveAsset(ReadOnlySpan<byte> payload, Type expectedType)
            => CookedAssetReader.LoadAsset(payload, expectedType) as XRAsset;
#endif

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
