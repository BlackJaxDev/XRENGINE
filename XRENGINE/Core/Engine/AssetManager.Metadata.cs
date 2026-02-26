using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Diagnostics;

namespace XREngine
{
    public partial class AssetManager
    {
        public void SyncMetadataWithAssets()
        {
            if (string.IsNullOrWhiteSpace(GameAssetsPath) || string.IsNullOrWhiteSpace(GameMetadataPath))
                return;

            string assetsRoot = Path.GetFullPath(GameAssetsPath);
            string metadataRoot = Path.GetFullPath(GameMetadataPath);
            if (!Directory.Exists(assetsRoot))
                return;

            Directory.CreateDirectory(metadataRoot);

            foreach (string directory in Directory.EnumerateDirectories(assetsRoot, "*", SearchOption.AllDirectories))
                EnsureMetadataForAssetPath(directory, true);

            foreach (string file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
                EnsureMetadataForAssetPath(file, false);
        }

        private void HandleMetadataCreated(string path)
        {
            if (!IsPathUnderGameAssets(path))
                return;

            EnsureMetadataForAssetPath(path, SafeIsDirectory(path));
        }

        private void HandleMetadataChanged(string path)
        {
            if (!IsPathUnderGameAssets(path))
                return;

            EnsureMetadataForAssetPath(path, SafeIsDirectory(path));
        }

        private void HandleMetadataDeleted(string path)
        {
            RemoveMetadataForPath(path, SafeIsDirectory(path));
        }

        private void HandleMetadataRenamed(string oldPath, string newPath)
        {
            bool isDirectory = SafeIsDirectory(newPath) || SafeIsDirectory(oldPath);
            MoveMetadataForPath(oldPath, newPath, isDirectory);
        }

        private bool IsPathUnderGameAssets(string path)
        {
            if (string.IsNullOrWhiteSpace(GameAssetsPath) || string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                string normalizedAssets = Path.GetFullPath(GameAssetsPath);
                string normalizedPath = Path.GetFullPath(path);

                string relative = Path.GetRelativePath(normalizedAssets, normalizedPath);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeIsDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    return true;
                if (File.Exists(path))
                    return false;

                // For deleted/renamed watcher events, the path may no longer exist. Avoid File.GetAttributes,
                // which throws for missing paths and can spam first-chance exceptions under a debugger.
                return !Path.HasExtension(path);
            }
            catch
            {
                return !Path.HasExtension(path);
            }
        }

        private bool TryGetMetadataPath(string assetPath, out string metadataPath, out string relativePath)
        {
            metadataPath = string.Empty;
            relativePath = string.Empty;

            if (!IsPathUnderGameAssets(assetPath))
                return false;

            string assetsRoot = Path.GetFullPath(GameAssetsPath);
            string assetFullPath = Path.GetFullPath(assetPath);
            relativePath = Path.GetRelativePath(assetsRoot, assetFullPath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || relativePath == ".")
                return false;

            string target = Path.Combine(GameMetadataPath!, relativePath);
            metadataPath = $"{target}.meta";
            return true;
        }

        private void EnsureMetadataForAssetPath(string assetPath, bool isDirectory)
        {
            if (!TryGetMetadataPath(assetPath, out string metaPath, out string relativePath))
                return;

            lock (_metadataLock)
            {
                string? directory = Path.GetDirectoryName(metaPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                AssetMetadata meta = TryReadMetadata(metaPath) ?? new AssetMetadata();
                meta.Name = Path.GetFileName(assetPath);
                meta.RelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                meta.IsDirectory = isDirectory;

                if (isDirectory)
                {
                    if (meta.Guid == Guid.Empty)
                        meta.Guid = Guid.NewGuid();
                    meta.Import = null;
                }
                else
                {
                    string ext = Path.GetExtension(assetPath);
                    bool isAssetFile = ext.Equals($".{AssetExtension}", StringComparison.OrdinalIgnoreCase);

                    if (isAssetFile)
                    {
                        if (meta.Guid == Guid.Empty)
                        {
                            Guid extracted = TryExtractGuidFromAsset(assetPath);
                            meta.Guid = extracted == Guid.Empty ? Guid.NewGuid() : extracted;
                        }

                        meta.Import = null;
                    }
                    else
                    {
                        if (meta.Guid == Guid.Empty)
                            meta.Guid = Guid.NewGuid();

                        meta.Import ??= new AssetImportMetadata();
                        meta.Import.SourceExtension = ext.StartsWith(".", StringComparison.Ordinal) ? ext[1..] : ext;
                        meta.Import.SourceLastWriteTimeUtc = SafeGetLastWriteTimeUtc(assetPath);
                        meta.Import.ImporterType = ResolveImporterNameForExtension(ext);
                    }
                }

                meta.LastSyncedUtc = DateTime.UtcNow;
                WriteMetadataFile(metaPath, meta);
            }
        }

        private void RemoveMetadataForPath(string assetPath, bool isDirectory)
        {
            if (!TryGetMetadataPath(assetPath, out string metaPath, out _))
                return;

            lock (_metadataLock)
            {
                if (File.Exists(metaPath))
                    File.Delete(metaPath);

                TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(metaPath));
            }
        }

        private void MoveMetadataForPath(string oldPath, string newPath, bool isDirectory)
        {
            if (!TryGetMetadataPath(oldPath, out string oldMeta, out _))
            {
                EnsureMetadataForAssetPath(newPath, isDirectory);
                return;
            }

            if (!TryGetMetadataPath(newPath, out string newMeta, out _))
            {
                RemoveMetadataForPath(oldPath, isDirectory);
                return;
            }

            lock (_metadataLock)
            {
                string? newDir = Path.GetDirectoryName(newMeta);
                if (!string.IsNullOrWhiteSpace(newDir))
                    Directory.CreateDirectory(newDir);

                if (File.Exists(oldMeta))
                {
                    try
                    {
                        File.Move(oldMeta, newMeta, true);
                    }
                    catch (System.IO.IOException)
                    {
                        File.Copy(oldMeta, newMeta, true);
                        File.Delete(oldMeta);
                    }
                }

                TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(oldMeta));
            }

            EnsureMetadataForAssetPath(newPath, isDirectory);
        }

        private static AssetMetadata? TryReadMetadata(string metaPath)
        {
            if (!File.Exists(metaPath))
                return null;

            try
            {
                string text = File.ReadAllText(metaPath);
                return Deserializer.Deserialize<AssetMetadata>(text);
            }
            catch
            {
                return null;
            }
        }

        private static void WriteMetadataFile(string metaPath, AssetMetadata meta)
        {
            string yaml = Serializer.Serialize(meta);
            File.WriteAllText(metaPath, yaml);
        }

        private static Guid TryExtractGuidFromAsset(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
                return Guid.Empty;

            // Asset files can be transiently locked by the editor (thumbnail generation, importers, etc).
            // Prefer shared reads to avoid throwing IO exceptions during normal operation.
            const int maxAttempts = 3;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    using var stream = new FileStream(
                        assetPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        string trimmed = line.Trim();
                        if (!trimmed.StartsWith("ID:", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string guidText = trimmed[3..].Trim();
                        if (Guid.TryParse(guidText, out Guid guid))
                            return guid;
                    }

                    break;
                }
                catch (IOException)
                {
                    if (attempt == maxAttempts - 1)
                        break;

                    Thread.Sleep(15 * (attempt + 1));
                }
                catch (UnauthorizedAccessException)
                {
                    break;
                }
            }

            return Guid.Empty;
        }

        private static DateTime? SafeGetLastWriteTimeUtc(string path)
        {
            try
            {
                DateTime timestamp = File.GetLastWriteTimeUtc(path);
                return timestamp == DateTime.MinValue ? null : timestamp;
            }
            catch (System.IO.IOException)
            {
                return null;
            }
        }

        private static string? ResolveImporterNameForExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return null;

            string normalized = extension.TrimStart('.').ToLowerInvariant();
            var map = ThirdPartyExtensionMap.Value;

            return map.TryGetValue(normalized, out Type? type)
                ? type.FullName ?? type.Name
                : null;
        }

        private void TryPruneEmptyMetadataDirectories(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(GameMetadataPath))
                return;

            string root = Path.GetFullPath(GameMetadataPath);
            string current = Path.GetFullPath(directory);

            while (current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(current))
                    break;

                using var enumerator = Directory.EnumerateFileSystemEntries(current).GetEnumerator();
                if (enumerator.MoveNext())
                    break;

                Directory.Delete(current);

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent))
                    break;

                current = parent;
            }
        }
    }
}
