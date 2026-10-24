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
            EnsureHostFileAssetAccess();
            lock (_metadataLock)
            {
                AuthoringContext? context = TryCaptureAuthoringContext();
                if (context is null || !context.Files.DirectoryExists(context.AssetsRoot))
                    return;

                context.Files.CreateDirectory(context.MetadataRoot);
                PruneStaleMetadataEntries(context);
                foreach (string directory in context.FileSystem.EnumerateDirectories(context.AssetsRoot, "*", SearchOption.AllDirectories))
                    EnsureMetadataForAssetPath(directory, true, context);
                foreach (string file in context.FileSystem.EnumerateFiles(context.AssetsRoot, "*", SearchOption.AllDirectories))
                    EnsureMetadataForAssetPath(file, false, context);
            }
        }

        /// <summary>
        /// Deletes metadata files that no longer have a corresponding asset file or directory.
        /// Also deletes transient metadata files (e.g. from temp files during import).
        /// </summary>
        private void PruneStaleMetadataEntries(AuthoringContext context)
        {
            foreach (string metaFile in context.FileSystem.EnumerateFiles(context.MetadataRoot, "*.meta", SearchOption.AllDirectories).ToArray())
            {
                if (!ShouldDeleteMetadataFile(metaFile, context.AssetsRoot, context.Files))
                    continue;

                if (!TryDeleteMetadataFile(metaFile, context.Files))
                    continue;

                TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(metaFile), context);
            }
        }

        /// <summary>
        /// Ensures that there is a metadata file for the given asset path, creating or updating it as necessary.
        /// </summary>
        /// <param name="path">The path of the asset.</param>
        private void HandleMetadataCreated(string path, AuthoringContext context)
        {
            if (!TryMapMetadataPath(context, path, out _, out _) || !IsLivePath(context, path))
                return;
            EnsureMetadataForAssetPath(path, context.Files.DirectoryExists(path), context);
        }

        /// <summary>
        /// Ensures that there is a metadata file for the given asset path, creating or updating it as necessary.
        /// </summary>
        /// <param name="path">The path of the asset.</param>
        private void HandleMetadataChanged(string path, AuthoringContext context)
        {
            if (!TryMapMetadataPath(context, path, out _, out _) || !IsLivePath(context, path))
                return;
            EnsureMetadataForAssetPath(path, context.Files.DirectoryExists(path), context);
        }

        /// <summary>
        /// Deletes the metadata file for the given asset path if it exists.
        /// Deletes only the selected path's metadata and prunes empty metadata directories.
        /// </summary>
        /// <param name="path">The path of the asset.</param>
        private void HandleMetadataDeleted(string path, AuthoringContext context)
        {
            if (!IsLivePath(context, path))
                RemoveMetadataForPath(path, context);
        }

        /// <summary>
        /// Moves the metadata file for the given asset path if it exists, otherwise creates a new metadata file for the new path.
        /// For a directory, moves only that directory's metadata.
        /// </summary>
        /// <param name="oldPath">The old path of the asset.</param>
        /// <param name="newPath">The new path of the asset.</param>
        private bool HandleMetadataRenamed(string oldPath, string newPath, AuthoringContext context)
        {
            if (!IsLivePath(context, oldPath) && IsLivePath(context, newPath))
                return MoveMetadataForPath(oldPath, newPath, context.Files.DirectoryExists(newPath), context);
            return false;
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
            lock (_metadataLock)
            {
                AuthoringContext? context = TryCaptureAuthoringContext();
                if (context is not null)
                    return TryMapMetadataPath(context, assetPath, out metadataPath, out relativePath);
                metadataPath = string.Empty;
                relativePath = string.Empty;
                return false;
            }
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
            EnsureHostFileAssetAccess();
            lock (_metadataLock)
            {
                AuthoringContext? context = TryCaptureAuthoringContext();
                if (context is not null)
                    EnsureMetadataForAssetPath(assetPath, isDirectory, context);
            }
        }

        private void EnsureMetadataForAssetPath(string assetPath, bool isDirectory, AuthoringContext context)
        {
            if (!TryMapMetadataPath(context, assetPath, out string metaPath, out string relativePath))
                return;

            EnsureMetadataForAssetPath(assetPath, isDirectory, metaPath, relativePath, context.Files);
        }

        private void EnsureMetadataForAssetPath(
            string assetPath, bool isDirectory, string metaPath, string relativePath,
            IAssetMetadataFileBackend files)
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
        /// For a directory, deletes only that directory's metadata.
        /// </summary>
        /// <param name="assetPath">The path of the asset.</param>
        private void RemoveMetadataForPath(string assetPath, AuthoringContext context)
        {
            if (!TryMapMetadataPath(context, assetPath, out string metaPath, out _))
                return;

            RemoveMetadataFileAtPath(metaPath, context);
        }

        private void RemoveMetadataFileAtPath(string metaPath, AuthoringContext context)
        {
            lock (_metadataLock)
            {
                if (!TryDeleteMetadataFile(metaPath, context.Files))
                    return;

                TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(metaPath), context);
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
        /// For a directory, moves only that directory's metadata.
        /// </summary>
        /// <param name="oldPath">The current path of the asset.</param>
        /// <param name="newPath">The new path of the asset.</param>
        /// <param name="isDirectory">Indicates whether the asset is a directory.</param>
        private bool MoveMetadataForPath(
            string oldPath, string newPath, bool isDirectory,
            AuthoringContext context)
        {
            if (!TryMapMetadataPath(context, oldPath, out string oldMeta, out _))
            {
                EnsureMetadataForAssetPath(newPath, isDirectory, context);
                return true;
            }

            if (!TryMapMetadataPath(context, newPath, out string newMeta, out _))
            {
                RemoveMetadataForPath(oldPath, context);
                return true;
            }

            lock (_metadataLock)
            {
                IAssetMetadataFileBackend files = context.Files;
                if (files.FileExists(newMeta))
                    return false;
                string? newDir = Path.GetDirectoryName(newMeta);
                if (!string.IsNullOrWhiteSpace(newDir))
                    files.CreateDirectory(newDir);

                if (files.FileExists(oldMeta))
                    files.MoveFile(oldMeta, newMeta, false);

                TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(oldMeta), context);
            }

            EnsureMetadataForAssetPath(newPath, isDirectory, context);
            return true;
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
            string? directory, AuthoringContext context)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            IAssetFileSystem fileSystem = context.FileSystem;
            IAssetMetadataFileBackend files = context.Files;
            string root = context.MetadataRoot;
            string current = Path.GetFullPath(directory);

            while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase) &&
                current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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
