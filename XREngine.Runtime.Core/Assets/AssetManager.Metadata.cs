using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;

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

            (IAssetFileSystem fileSystem, IAssetMetadataFileBackend files) = CaptureMetadataFileBackend();

            string assetsRoot = Path.GetFullPath(GameAssetsPath);
            string metadataRoot = Path.GetFullPath(GameMetadataPath);
            if (!files.DirectoryExists(assetsRoot))
                return;

            files.CreateDirectory(metadataRoot);

            PruneStaleMetadataEntries(assetsRoot, metadataRoot, fileSystem, files);

            foreach (string directory in fileSystem.EnumerateDirectories(assetsRoot, "*", SearchOption.AllDirectories))
                EnsureMetadataForAssetPath(directory, true, fileSystem, files);

            foreach (string file in fileSystem.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
                EnsureMetadataForAssetPath(file, false, fileSystem, files);
        }

        private (IAssetFileSystem FileSystem, IAssetMetadataFileBackend Files) CaptureMetadataFileBackend()
        {
            EnsureHostFileAssetAccess();
            IAssetFileSystem fileSystem = AssetFileSystemServices.Required;
            if (fileSystem is not IAssetMetadataFileBackend files)
                throw new NotSupportedException("AssetSource.MetadataFileUnavailable: the installed asset file system does not provide metadata file operations.");

            return (fileSystem, files);
        }

        /// <summary>
        /// Deletes metadata files that no longer have a corresponding asset file or directory.
        /// Also deletes transient metadata files (e.g. from temp files during import).
        /// </summary>
        /// <param name="assetsRoot">The root directory of the assets.</param>
        /// <param name="metadataRoot">The root directory of the metadata.</param>
        private void PruneStaleMetadataEntries(string assetsRoot, string metadataRoot, IAssetFileSystem fileSystem, IAssetMetadataFileBackend files)
        {
            lock (_metadataLock)
            {
                foreach (string metaFile in fileSystem.EnumerateFiles(metadataRoot, "*.meta", SearchOption.AllDirectories).ToArray())
                {
                    if (!ShouldDeleteMetadataFile(metaFile, assetsRoot, files))
                        continue;

                    if (!TryDeleteMetadataFile(metaFile, files))
                        continue;

                    TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(metaFile), fileSystem, files);
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

            (IAssetFileSystem fileSystem, IAssetMetadataFileBackend files) = CaptureMetadataFileBackend();
            EnsureMetadataForAssetPath(path, SafeIsDirectory(path, files), fileSystem, files);
        }

        /// <summary>
        /// Ensures that there is a metadata file for the given asset path, creating or updating it as necessary.
        /// </summary>
        /// <param name="path">The path of the asset.</param>
        private void HandleMetadataChanged(string path)
        {
            if (!IsPathUnderGameAssets(path))
                return;

            (IAssetFileSystem fileSystem, IAssetMetadataFileBackend files) = CaptureMetadataFileBackend();
            EnsureMetadataForAssetPath(path, SafeIsDirectory(path, files), fileSystem, files);
        }

        /// <summary>
        /// Deletes the metadata file for the given asset path if it exists.
        /// If the asset is a directory, also deletes metadata for all nested assets.
        /// Also prunes empty metadata directories up the hierarchy.
        /// </summary>
        /// <param name="path">The path of the asset.</param>
        private void HandleMetadataDeleted(string path)
        {
            (IAssetFileSystem fileSystem, IAssetMetadataFileBackend files) = CaptureMetadataFileBackend();
            RemoveMetadataForPath(path, fileSystem, files);
        }

        /// <summary>
        /// Moves the metadata file for the given asset path if it exists, otherwise creates a new metadata file for the new path.
        /// If the asset is a directory, also moves metadata for all nested assets.
        /// </summary>
        /// <param name="oldPath">The old path of the asset.</param>
        /// <param name="newPath">The new path of the asset.</param>
        private void HandleMetadataRenamed(string oldPath, string newPath)
        {
            (IAssetFileSystem fileSystem, IAssetMetadataFileBackend files) = CaptureMetadataFileBackend();
            bool isDirectory = SafeIsDirectory(newPath, files) || SafeIsDirectory(oldPath, files);
            MoveMetadataForPath(oldPath, newPath, isDirectory, fileSystem, files);
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
        private static bool SafeIsDirectory(string path, IAssetMetadataFileBackend files)
        {
            try
            {
                if (files.DirectoryExists(path))
                    return true;
                if (files.FileExists(path))
                    return false;

                // For deleted/renamed watcher events, the path may no longer exist. Avoid File.GetAttributes,
                // which throws for missing paths and can spam first-chance exceptions under a debugger.
                return !Path.HasExtension(path);
            }
            catch (NotSupportedException)
            {
                throw;
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
        public bool TryGetMetadataPath(string assetPath, out string metadataPath, out string relativePath)
        {
            metadataPath = string.Empty;
            relativePath = string.Empty;

            if (string.IsNullOrWhiteSpace(GameMetadataPath) || !IsPathUnderGameAssets(assetPath))
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
        public void EnsureMetadataForAssetPath(string assetPath, bool isDirectory)
        {
            if (!TryGetMetadataPath(assetPath, out string metaPath, out string relativePath))
                return;

            (IAssetFileSystem fileSystem, IAssetMetadataFileBackend files) = CaptureMetadataFileBackend();
            EnsureMetadataForAssetPath(assetPath, isDirectory, metaPath, relativePath, fileSystem, files);
        }

        private void EnsureMetadataForAssetPath(string assetPath, bool isDirectory, IAssetFileSystem fileSystem, IAssetMetadataFileBackend files)
        {
            if (!TryGetMetadataPath(assetPath, out string metaPath, out string relativePath))
                return;

            EnsureMetadataForAssetPath(assetPath, isDirectory, metaPath, relativePath, fileSystem, files);
        }

        private void EnsureMetadataForAssetPath(
            string assetPath, bool isDirectory, string metaPath, string relativePath,
            IAssetFileSystem fileSystem, IAssetMetadataFileBackend files)
        {
            EnsureHostFileAssetAccess();
            lock (_metadataLock)
            {
                if (!AssetPathExists(assetPath, isDirectory, files))
                    return;

                string? directory = Path.GetDirectoryName(metaPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    files.CreateDirectory(directory);

                AssetMetadata meta = TryReadMetadata(metaPath, files) ?? new AssetMetadata();
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
                        Guid extracted = files.TryExtractAssetGuid(assetPath);
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
                        meta.Import.SourceLastWriteTimeUtc = SafeGetLastWriteTimeUtc(assetPath, files);
                        meta.Import.ImporterType = ResolveImporterNameForExtension(ext);
                    }
                }

                meta.LastSyncedUtc = DateTime.UtcNow;
                try
                {
                    WriteMetadataFile(metaPath, meta, files);
                }
                catch (DirectoryNotFoundException) when (!AssetPathExists(assetPath, isDirectory, files))
                {
                    // A delayed AssetChangeMonitor create/change callback can race the
                    // corresponding delete callback. The delete owns final metadata state.
                }
                catch (IOException) when (!AssetPathExists(assetPath, isDirectory, files))
                {
                    // The asset disappeared while the metadata file was being published.
                }
            }
        }

        private static bool AssetPathExists(string assetPath, bool isDirectory, IAssetMetadataFileBackend files)
            => isDirectory ? files.DirectoryExists(assetPath) : files.FileExists(assetPath);

        /// <summary>
        /// Deletes the metadata file for the given asset path if it exists.
        /// If the asset is a directory, also deletes metadata for all nested assets.
        /// </summary>
        /// <param name="assetPath">The path of the asset.</param>
        private void RemoveMetadataForPath(string assetPath, IAssetFileSystem fileSystem, IAssetMetadataFileBackend files)
        {
            if (!TryGetMetadataPath(assetPath, out string metaPath, out _))
                return;

            RemoveMetadataFileAtPath(metaPath, fileSystem, files);
        }

        private void RemoveMetadataFileAtPath(string metaPath, IAssetFileSystem fileSystem, IAssetMetadataFileBackend files)
        {
            lock (_metadataLock)
            {
                if (!TryDeleteMetadataFile(metaPath, files))
                    return;

                TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(metaPath), fileSystem, files);
            }
        }

        /// <summary>
        /// Deletes a metadata file without allowing transient Windows file locks to escape
        /// a <see cref="AssetChangeMonitor"/> callback and terminate the process.
        /// </summary>
        private static bool TryDeleteMetadataFile(string metaPath, IAssetMetadataFileBackend files)
        {
            try
            {
                files.DeleteFile(metaPath);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Moves the metadata file for the given asset path if it exists, otherwise creates a new metadata file for the new path.
        /// If the asset is a directory, also moves metadata for all nested assets.
        /// </summary>
        /// <param name="oldPath">The current path of the asset.</param>
        /// <param name="newPath">The new path of the asset.</param>
        /// <param name="isDirectory">Indicates whether the asset is a directory.</param>
        private void MoveMetadataForPath(
            string oldPath, string newPath, bool isDirectory,
            IAssetFileSystem fileSystem, IAssetMetadataFileBackend files)
        {
            if (!TryGetMetadataPath(oldPath, out string oldMeta, out _))
            {
                EnsureMetadataForAssetPath(newPath, isDirectory, fileSystem, files);
                return;
            }

            if (!TryGetMetadataPath(newPath, out string newMeta, out _))
            {
                RemoveMetadataForPath(oldPath, fileSystem, files);
                return;
            }

            lock (_metadataLock)
            {
                string? newDir = Path.GetDirectoryName(newMeta);
                if (!string.IsNullOrWhiteSpace(newDir))
                    files.CreateDirectory(newDir);

                if (files.FileExists(oldMeta))
                {
                    try
                    {
                        files.MoveFile(oldMeta, newMeta, true);
                    }
                    catch (System.IO.IOException)
                    {
                        files.CopyFile(oldMeta, newMeta, true);
                        TryDeleteMetadataFile(oldMeta, files);
                    }
                }

                TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(oldMeta), fileSystem, files);
            }

            EnsureMetadataForAssetPath(newPath, isDirectory, fileSystem, files);
        }

        /// <summary>
        /// Ensures that for every file and directory under the GameAssetsPath, there is a corresponding metadata file under GameMetadataPath.
        /// Also prunes metadata files that no longer have a corresponding asset.
        /// </summary>
        /// <param name="metaPath">The path of the metadata file.</param>
        /// <returns>The deserialized metadata if it exists and is valid; otherwise, null.</returns>
        private static AssetMetadata? TryReadMetadata(string metaPath, IAssetMetadataFileBackend files)
        {
            if (!files.FileExists(metaPath))
                return null;

            string text;
            try
            {
                text = files.ReadAllText(metaPath);
            }
            catch (NotSupportedException)
            {
                throw;
            }
            catch
            {
                return null;
            }

            try
            {
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
        private static void WriteMetadataFile(string metaPath, AssetMetadata meta, IAssetMetadataFileBackend files)
        {
            string? directory = Path.GetDirectoryName(metaPath);
            if (!string.IsNullOrWhiteSpace(directory))
                files.CreateDirectory(directory);

            string yaml = Serializer.Serialize(meta);
            files.WriteAllText(metaPath, yaml);
        }

        /// <summary>
        /// Determines whether the given metadata file should be deleted due to not having a corresponding asset or being a transient metadata file (e.g. from temp files during import).
        /// Accounts for the possibility that the asset file or directory may not exist (e.g. deleted or renamed assets during watcher events).
        /// Also accounts for the possibility that the metadata file may be malformed or missing required information.
        /// </summary>
        /// <param name="metaPath">The path of the metadata file.</param>
        /// <param name="assetsRoot">The root directory of the assets.</param>
        /// <returns>True if the metadata file should be deleted; otherwise, false.</returns>
        private static bool ShouldDeleteMetadataFile(string metaPath, string assetsRoot, IAssetMetadataFileBackend files)
        {
            if (IsTransientMetadataPath(metaPath))
                return true;

            AssetMetadata? meta = TryReadMetadata(metaPath, files);
            if (meta is null || string.IsNullOrWhiteSpace(meta.RelativePath))
                return false;

            string candidate = Path.Combine(assetsRoot, meta.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            return !files.FileExists(candidate) && !files.DirectoryExists(candidate);
        }

        /// <summary>
        /// Determines whether the given path is a transient metadata file, such as those created from temporary files during asset import.
        /// </summary>
        /// <param name="metaPath">The path of the metadata file.</param>
        /// <returns>True if the metadata file is transient; otherwise, false.</returns>
        private static bool IsTransientMetadataPath(string metaPath)
            => Path.GetFileName(metaPath).EndsWith(".tmp.meta", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Safely gets the last write time of the specified file in UTC, returning null if the file does not exist or an error occurs.
        /// </summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The last write time in UTC if available; otherwise, null.</returns>
        /// <remarks>This method accounts for the possibility that the file may be transiently locked or deleted during asset import, and avoids throwing exceptions in those cases.</remarks>
        private static DateTime? SafeGetLastWriteTimeUtc(string path, IAssetMetadataFileBackend files)
        {
            try
            {
                DateTime timestamp = files.GetLastWriteTimeUtc(path);
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

            if (!ThirdPartyAssetTypeRegistry.TryResolve(extension, out Type? type, out _) || type is null)
                return null;

            return type.FullName ?? type.Name;
        }

        /// <summary>
        /// Attempts to prune empty metadata directories up the hierarchy starting from the specified directory, stopping when a non-empty directory is found or the GameMetadataPath root is reached.
        /// </summary>
        /// <param name="directory">The starting directory to attempt pruning.</param>
        private void TryPruneEmptyMetadataDirectories(
            string? directory, IAssetFileSystem fileSystem, IAssetMetadataFileBackend files)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(GameMetadataPath))
                return;

            string root = Path.GetFullPath(GameMetadataPath);
            string current = Path.GetFullPath(directory);

            while (current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (!files.DirectoryExists(current))
                    break;

                bool hasEntries;
                try
                {
                    using var enumerator = fileSystem.EnumerateFileSystemEntries(current).GetEnumerator();
                    hasEntries = enumerator.MoveNext();
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }
                catch (IOException)
                {
                    // File-system watcher callbacks can race another callback or a test cleanup.
                    // Metadata pruning is best-effort, so leave the directory for a later pass.
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    // Antivirus and indexers can briefly retain handles on Windows.
                    break;
                }

                if (hasEntries)
                    break;

                try
                {
                    files.DeleteDirectory(current);
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }
                catch (IOException)
                {
                    // Another watcher callback may already be deleting the same directory.
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    // Treat transient Windows access denial as deferred cleanup.
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
