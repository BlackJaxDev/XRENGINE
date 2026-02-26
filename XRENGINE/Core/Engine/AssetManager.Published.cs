using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Diagnostics;

namespace XREngine
{
    public partial class AssetManager
    {
        public static void ConfigurePublishedArchives(
            string? configArchivePath,
            string? gameContentArchivePath,
            string? engineContentArchivePath)
        {
#if XRE_PUBLISHED
            _publishedConfigArchivePath = NormalizeExistingArchivePath(configArchivePath);
            _publishedGameContentArchivePath = NormalizeExistingArchivePath(gameContentArchivePath);
            _publishedEngineContentArchivePath = NormalizeExistingArchivePath(engineContentArchivePath);
#endif
        }

#if XRE_PUBLISHED
        [RequiresUnreferencedCode("Cooked asset loading from archives uses reflection and requires runtime metadata.")]
        [RequiresDynamicCode("Cooked asset loading from archives uses reflection and cannot be fully AOT-analyzed.")]
        private static bool TryLoadPublishedAssetFromArchive<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
            string filePath,
            [NotNullWhen(true)] out T? asset) where T : XRAsset, new()
        {
            asset = default;

            if (!TryResolvePublishedArchiveRequest(filePath, out string? archivePath, out string? archiveAssetPath))
                return false;

            foreach (var (candidateArchivePath, candidateAssetPath) in EnumeratePublishedArchiveRequests(filePath))
            {
                try
                {
                    byte[] cookedBytes = AssetPacker.GetAsset(candidateArchivePath, candidateAssetPath);
                    object? loaded = CookedAssetReader.LoadAsset(cookedBytes, typeof(T));
                    asset = loaded as T;
                    if (asset is not null)
                        return true;
                }
                catch (FileNotFoundException)
                {
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to load published asset '{candidateAssetPath}' from '{candidateArchivePath}': {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        [RequiresUnreferencedCode("Cooked asset loading from archives uses reflection and requires runtime metadata.")]
        [RequiresDynamicCode("Cooked asset loading from archives uses reflection and cannot be fully AOT-analyzed.")]
        private static bool TryLoadPublishedAssetFromArchive(
            string filePath,
            Type expectedType,
            [NotNullWhen(true)] out XRAsset? asset)
        {
            asset = null;

            if (!TryResolvePublishedArchiveRequest(filePath, out _, out _))
                return false;

            foreach (var (candidateArchivePath, candidateAssetPath) in EnumeratePublishedArchiveRequests(filePath))
            {
                try
                {
                    byte[] cookedBytes = AssetPacker.GetAsset(candidateArchivePath, candidateAssetPath);
                    asset = CookedAssetReader.LoadAsset(cookedBytes, expectedType) as XRAsset;
                    if (asset is not null)
                        return true;
                }
                catch (FileNotFoundException)
                {
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to load published asset '{candidateAssetPath}' from '{candidateArchivePath}': {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        private static bool TryResolvePublishedArchiveRequest(string filePath, out string archivePath, out string archiveAssetPath)
        {
            archivePath = string.Empty;
            archiveAssetPath = string.Empty;

            if (!string.Equals(Path.GetExtension(filePath), $".{AssetExtension}", StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (var (candidateArchivePath, candidateAssetPath) in EnumeratePublishedArchiveRequests(filePath))
            {
                archivePath = candidateArchivePath;
                archiveAssetPath = candidateAssetPath;
                return true;
            }

            return false;
        }

        private static IEnumerable<(string archivePath, string assetPath)> EnumeratePublishedArchiveRequests(string filePath)
        {
            foreach (string archivePath in EnumerateArchivePathCandidates(filePath))
            {
                foreach (string assetPath in EnumerateArchiveAssetPathCandidates(filePath))
                    yield return (archivePath, assetPath);
            }
        }

        private static IEnumerable<string> EnumerateArchivePathCandidates(string filePath)
        {
            string normalizedPath = filePath.Replace('\\', '/');
            bool looksLikeEngineAsset = normalizedPath.Contains("/Build/CommonAssets/", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.Contains("/CommonAssets/", StringComparison.OrdinalIgnoreCase);
            bool looksLikeConfigAsset = normalizedPath.Contains("/Config/", StringComparison.OrdinalIgnoreCase);
            bool looksLikeGameAsset = normalizedPath.Contains("/Assets/", StringComparison.OrdinalIgnoreCase)
                || (!looksLikeEngineAsset && !looksLikeConfigAsset);

            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            static void YieldIfExists(string? path, HashSet<string> seen, List<string> output)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !seen.Add(path))
                    return;

                output.Add(path);
            }

            var results = new List<string>();
            if (looksLikeConfigAsset)
            {
                YieldIfExists(_publishedConfigArchivePath, yielded, results);
                YieldIfExists(_publishedGameContentArchivePath, yielded, results);
                YieldIfExists(_publishedEngineContentArchivePath, yielded, results);
            }
            else if (looksLikeEngineAsset)
            {
                YieldIfExists(_publishedEngineContentArchivePath, yielded, results);
                YieldIfExists(_publishedGameContentArchivePath, yielded, results);
                YieldIfExists(_publishedConfigArchivePath, yielded, results);
            }
            else
            {
                if (looksLikeGameAsset)
                {
                    YieldIfExists(_publishedGameContentArchivePath, yielded, results);
                    YieldIfExists(_publishedEngineContentArchivePath, yielded, results);
                }

                YieldIfExists(_publishedConfigArchivePath, yielded, results);
            }

            foreach (string path in results)
                yield return path;
        }

        private static IEnumerable<string> EnumerateArchiveAssetPathCandidates(string filePath)
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            static void EmitIfValid(string? path, HashSet<string> seen, List<string> output)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                string normalized = path.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                    return;

                output.Add(normalized);
            }

            var results = new List<string>();
            string normalized = filePath.Replace('\\', '/');

            EmitIfValid(TryExtractAfterSegment(normalized, "/Config/"), emitted, results);
            EmitIfValid(TryExtractAfterSegment(normalized, "/Assets/"), emitted, results);
            EmitIfValid(TryExtractAfterSegment(normalized, "/Build/CommonAssets/"), emitted, results);
            EmitIfValid(TryExtractAfterSegment(normalized, "/CommonAssets/"), emitted, results);

            if (!Path.IsPathRooted(filePath))
                EmitIfValid(filePath, emitted, results);

            EmitIfValid(Path.GetFileName(filePath), emitted, results);

            foreach (string candidate in results)
                yield return candidate;
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
#endif
    }
}
