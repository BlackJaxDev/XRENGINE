using XREngine.Core.Engine;

namespace XREngine
{
    public partial class AssetManager
    {
        /// <summary>
        /// Ensures that for every file and directory under the GameAssetsPath, there is a corresponding metadata file under GameMetadataPath.
        /// Also prunes metadata files that no longer have a corresponding asset.
        /// </summary>
        public void SyncMetadataWithAssets()
        {
            if (string.IsNullOrWhiteSpace(GameAssetsPath) || string.IsNullOrWhiteSpace(GameMetadataPath))
                return;

            string assetsRoot = Path.GetFullPath(GameAssetsPath);
            string metadataRoot = Path.GetFullPath(GameMetadataPath);
            if (!Directory.Exists(assetsRoot))
                return;

            Directory.CreateDirectory(metadataRoot);

            PruneStaleMetadataEntries(assetsRoot, metadataRoot);

            foreach (string directory in Directory.EnumerateDirectories(assetsRoot, "*", SearchOption.AllDirectories))
                EnsureMetadataForAssetPath(directory, true);

            foreach (string file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
                EnsureMetadataForAssetPath(file, false);
        }

        /// <summary>
        /// Deletes metadata files that no longer have a corresponding asset file or directory.
        /// Also deletes transient metadata files (e.g. from temp files during import).
        /// </summary>
        /// <param name="assetsRoot">The root directory of the assets.</param>
        /// <param name="metadataRoot">The root directory of the metadata.</param>
        private void PruneStaleMetadataEntries(string assetsRoot, string metadataRoot)
        {
            lock (_metadataLock)
            {
                foreach (string metaFile in Directory.EnumerateFiles(metadataRoot, "*.meta", SearchOption.AllDirectories).ToArray())
                {
                    if (!ShouldDeleteMetadataFile(metaFile, assetsRoot))
                        continue;

                    try
                    {
                        File.Delete(metaFile);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(metaFile));
                }
            }
        }

        /// <summary>
        /// Ensures that there is a metadata file for the given asset path, creating or updating it as necessary.
        /// </summary>
        /// <param name="path">The path of the asset.</param>
        private void HandleMetadataCreated(string path)
        {
            if (!IsPathUnderGameAssets(path))
                return;

            EnsureMetadataForAssetPath(path, SafeIsDirectory(path));
        }

        /// <summary>
        /// Ensures that there is a metadata file for the given asset path, creating or updating it as necessary.
        /// </summary>
        /// <param name="path">The path of the asset.</param>
        private void HandleMetadataChanged(string path)
        {
            if (!IsPathUnderGameAssets(path))
                return;

            EnsureMetadataForAssetPath(path, SafeIsDirectory(path));
        }

        /// <summary>
        /// Deletes the metadata file for the given asset path if it exists.
        /// If the asset is a directory, also deletes metadata for all nested assets.
        /// Also prunes empty metadata directories up the hierarchy.
        /// </summary>
        /// <param name="path">The path of the asset.</param>
        private void HandleMetadataDeleted(string path)
        {
            RemoveMetadataForPath(path, SafeIsDirectory(path));
        }

        /// <summary>
        /// Moves the metadata file for the given asset path if it exists, otherwise creates a new metadata file for the new path.
        /// If the asset is a directory, also moves metadata for all nested assets.
        /// </summary>
        /// <param name="oldPath">The old path of the asset.</param>
        /// <param name="newPath">The new path of the asset.</param>
        private void HandleMetadataRenamed(string oldPath, string newPath)
        {
            bool isDirectory = SafeIsDirectory(newPath) || SafeIsDirectory(oldPath);
            MoveMetadataForPath(oldPath, newPath, isDirectory);
        }

        /// <summary>
        /// Determines whether the given path is under the GameAssetsPath, accounting for relative paths, symbolic links, and case sensitivity.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <returns>True if the path is under the GameAssetsPath; otherwise, false.</returns>
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

        /// <summary>
        /// Determines whether the given path is a directory, accounting for the possibility that the path may not exist (e.g. deleted or renamed assets during watcher events).
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <returns>True if the path is a directory; otherwise, false.</returns>
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

        /// <summary>
        /// Attempts to get the corresponding metadata file path for a given asset path, returning false if the asset path is not under the GameAssetsPath.
        /// </summary>
        /// <param name="assetPath">The path of the asset.</param>
        /// <param name="metadataPath">The corresponding metadata file path.</param>
        /// <param name="relativePath">The relative path of the asset within the GameAssetsPath.</param>
        /// <returns>True if the metadata path was successfully determined; otherwise, false.</returns>
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

        /// <summary>
        /// Ensures that there is a metadata file for the given asset path, creating or updating it as necessary.
        /// If the asset is a file, also attempts to extract the GUID from the asset file if it is an .asset file.
        /// If the asset is a directory, ensures it has a GUID and does not have import metadata.
        /// </summary>
        /// <param name="assetPath">The path of the asset.</param>
        /// <param name="isDirectory">Indicates whether the asset is a directory.</param>
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
                        Guid extracted = TryExtractGuidFromAsset(assetPath);
                        if (extracted != Guid.Empty)
                            meta.Guid = extracted;
                        else if (meta.Guid == Guid.Empty)
                            meta.Guid = Guid.NewGuid();

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

        /// <summary>
        /// Deletes the metadata file for the given asset path if it exists.
        /// If the asset is a directory, also deletes metadata for all nested assets.
        /// </summary>
        /// <param name="assetPath">The path of the asset.</param>
        /// <param name="isDirectory">Indicates whether the asset is a directory.</param>
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

        /// <summary>
        /// Moves the metadata file for the given asset path if it exists, otherwise creates a new metadata file for the new path.
        /// If the asset is a directory, also moves metadata for all nested assets.
        /// </summary>
        /// <param name="oldPath">The current path of the asset.</param>
        /// <param name="newPath">The new path of the asset.</param>
        /// <param name="isDirectory">Indicates whether the asset is a directory.</param>
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

        /// <summary>
        /// Ensures that for every file and directory under the GameAssetsPath, there is a corresponding metadata file under GameMetadataPath.
        /// Also prunes metadata files that no longer have a corresponding asset.
        /// </summary>
        /// <param name="metaPath">The path of the metadata file.</param>
        /// <returns>The deserialized metadata if it exists and is valid; otherwise, null.</returns>
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

        /// <summary>
        /// Writes the given metadata to the specified metadata file path, creating or overwriting it as necessary.
        /// </summary>
        /// <param name="metaPath">The path of the metadata file.</param>
        /// <param name="meta">The metadata to write.</param>
        private static void WriteMetadataFile(string metaPath, AssetMetadata meta)
        {
            string? directory = Path.GetDirectoryName(metaPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string yaml = Serializer.Serialize(meta);
            File.WriteAllText(metaPath, yaml);
        }

        /// <summary>
        /// Determines whether the given metadata file should be deleted due to not having a corresponding asset or being a transient metadata file (e.g. from temp files during import).
        /// Accounts for the possibility that the asset file or directory may not exist (e.g. deleted or renamed assets during watcher events).
        /// Also accounts for the possibility that the metadata file may be malformed or missing required information.
        /// </summary>
        /// <param name="metaPath">The path of the metadata file.</param>
        /// <param name="assetsRoot">The root directory of the assets.</param>
        /// <returns>True if the metadata file should be deleted; otherwise, false.</returns>
        private static bool ShouldDeleteMetadataFile(string metaPath, string assetsRoot)
        {
            if (IsTransientMetadataPath(metaPath))
                return true;

            AssetMetadata? meta = TryReadMetadata(metaPath);
            if (meta is null || string.IsNullOrWhiteSpace(meta.RelativePath))
                return false;

            string candidate = Path.Combine(assetsRoot, meta.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            return !File.Exists(candidate) && !Directory.Exists(candidate);
        }

        /// <summary>
        /// Determines whether the given path is a transient metadata file, such as those created from temporary files during asset import.
        /// </summary>
        /// <param name="metaPath">The path of the metadata file.</param>
        /// <returns>True if the metadata file is transient; otherwise, false.</returns>
        private static bool IsTransientMetadataPath(string metaPath)
            => Path.GetFileName(metaPath).EndsWith(".tmp.meta", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Attempts to extract a GUID from the given asset file if it is an .asset file, returning Guid.Empty if the GUID cannot be extracted.
        /// </summary>
        /// <param name="assetPath">The path of the asset file.</param>
        /// <returns>The extracted GUID if successful; otherwise, Guid.Empty.</returns>
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
                        if (string.IsNullOrWhiteSpace(line) || char.IsWhiteSpace(line[0]))
                            continue;

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

        /// <summary>
        /// Safely gets the last write time of the specified file in UTC, returning null if the file does not exist or an error occurs.
        /// </summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The last write time in UTC if available; otherwise, null.</returns>
        /// <remarks>This method accounts for the possibility that the file may be transiently locked or deleted during asset import, and avoids throwing exceptions in those cases.</remarks>
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
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves the importer type name for a given file extension, returning null if the extension is null, empty, or does not have a registered importer.
         /// The extension should be provided with or without a leading dot (e.g. "fbx" or ".fbx").
        /// </summary>
        /// <param name="extension">The file extension.</param>
        /// <returns>The fully qualified name of the importer type if found; otherwise, null.</returns>
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

        /// <summary>
        /// Attempts to prune empty metadata directories up the hierarchy starting from the specified directory, stopping when a non-empty directory is found or the GameMetadataPath root is reached.
        /// </summary>
        /// <param name="directory">The starting directory to attempt pruning.</param>
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

                bool hasEntries;
                try
                {
                    using var enumerator = Directory.EnumerateFileSystemEntries(current).GetEnumerator();
                    hasEntries = enumerator.MoveNext();
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }

                if (hasEntries)
                    break;

                try
                {
                    Directory.Delete(current);
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent))
                    break;

                current = parent;
            }
        }
    }
}
