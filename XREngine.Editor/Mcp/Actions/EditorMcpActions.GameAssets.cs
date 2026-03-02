using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        // ═══════════════════════════════════════════════════════════════════
        // P3.1 — File-System Operations
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lists files and folders in the game project's assets directory.
        /// </summary>
        [XRMcp(Name = "list_game_assets", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List files and folders in the game project's assets directory with optional filtering.")]
        public static Task<McpToolResponse> ListGameAssetsAsync(
            McpToolContext context,
            [McpName("path"), Description("Relative subfolder within game assets. Empty for root.")]
            string path = "",
            [McpName("recursive"), Description("Search subdirectories recursively.")]
            bool recursive = true,
            [McpName("filter"), Description("Glob-style filter pattern (e.g., '*.asset', '*.cs'). Empty for all files.")]
            string filter = "",
            [McpName("include_metadata"), Description("Include file size and last-modified date.")]
            bool includeMetadata = false)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string targetDir = ResolveAndValidateGamePath(assetsPath, path, out error, mustExist: true, expectDirectory: true);
            if (error is not null)
                return Task.FromResult(error);

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string searchPattern = string.IsNullOrWhiteSpace(filter) ? "*" : filter;

            // Directories
            var directories = Directory.GetDirectories(targetDir, "*", searchOption)
                .Select(d => new
                {
                    path = Path.GetRelativePath(assetsPath, d).Replace('\\', '/'),
                    name = Path.GetFileName(d),
                    type = "directory"
                })
                .OrderBy(d => d.path)
                .ToArray();

            // Files
            var filesRaw = Directory.GetFiles(targetDir, searchPattern, searchOption);
            object[] files;
            if (includeMetadata)
            {
                files = filesRaw.Select(f =>
                {
                    var fi = new FileInfo(f);
                    return (object)new
                    {
                        path = Path.GetRelativePath(assetsPath, f).Replace('\\', '/'),
                        name = Path.GetFileName(f),
                        type = "file",
                        extension = Path.GetExtension(f).TrimStart('.'),
                        size = fi.Length,
                        lastModified = fi.LastWriteTimeUtc.ToString("o")
                    };
                })
                .OrderBy(f => ((dynamic)f).path)
                .ToArray();
            }
            else
            {
                files = filesRaw.Select(f => (object)new
                {
                    path = Path.GetRelativePath(assetsPath, f).Replace('\\', '/'),
                    name = Path.GetFileName(f),
                    type = "file",
                    extension = Path.GetExtension(f).TrimStart('.')
                })
                .OrderBy(f => ((dynamic)f).path)
                .ToArray();
            }

            return Task.FromResult(new McpToolResponse(
                $"Found {directories.Length} folder(s) and {files.Length} file(s).",
                new { directoryCount = directories.Length, fileCount = files.Length, directories, files }));
        }

        /// <summary>
        /// Reads raw text contents of an asset file (.asset, .json, .xml, .yaml, etc.).
        /// </summary>
        [XRMcp(Name = "read_game_asset", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Read the raw text contents of a file from the game project's assets directory (.asset, .json, .xml, .yaml, .cs, etc.).")]
        public static Task<McpToolResponse> ReadGameAssetAsync(
            McpToolContext context,
            [McpName("path"), Description("Relative path to the file within game assets.")]
            string path)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string fullPath = ResolveAndValidateGamePath(assetsPath, path, out error, mustExist: true, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            string content = File.ReadAllText(fullPath);
            int lineCount = content.Split('\n').Length;

            return Task.FromResult(new McpToolResponse(
                $"Read '{Path.GetFileName(fullPath)}' ({lineCount} lines, {content.Length} chars).",
                new
                {
                    path = Path.GetRelativePath(assetsPath, fullPath).Replace('\\', '/'),
                    extension = Path.GetExtension(fullPath).TrimStart('.'),
                    content,
                    lineCount,
                    charCount = content.Length
                }));
        }

        /// <summary>
        /// Writes or overwrites a text asset file in the game assets directory.
        /// </summary>
        [XRMcp(Name = "write_game_asset", Permission = McpPermissionLevel.Destructive, PermissionReason = "Creates or overwrites a file on disk.")]
        [Description("Write or overwrite a text asset file in the game project's assets directory.")]
        public static Task<McpToolResponse> WriteGameAssetAsync(
            McpToolContext context,
            [McpName("path"), Description("Relative path for the file within game assets.")]
            string path,
            [McpName("content"), Description("The text content to write to the file.")]
            string content,
            [McpName("create_dirs"), Description("Create parent directories if they don't exist.")]
            bool createDirs = true)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string fullPath = ResolveAndValidateGamePath(assetsPath, path, out error, mustExist: false, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                if (!createDirs)
                    return Task.FromResult(new McpToolResponse($"Parent directory does not exist and create_dirs is false.", isError: true));
                Directory.CreateDirectory(dir);
            }

            bool isNew = !File.Exists(fullPath);
            File.WriteAllText(fullPath, content);

            string verb = isNew ? "Created" : "Updated";
            string relativePath = Path.GetRelativePath(assetsPath, fullPath).Replace('\\', '/');

            return Task.FromResult(new McpToolResponse(
                $"{verb} asset file '{relativePath}'.",
                new { path = relativePath, isNew }));
        }

        /// <summary>
        /// Deletes a file or empty directory from game assets.
        /// </summary>
        [XRMcp(Name = "delete_game_asset", Permission = McpPermissionLevel.Destructive, PermissionReason = "Permanently deletes files or directories from disk.")]
        [Description("Delete a file or directory from the game project's assets directory.")]
        public static Task<McpToolResponse> DeleteGameAssetAsync(
            McpToolContext context,
            [McpName("path"), Description("Relative path to the file or directory within game assets.")]
            string path,
            [McpName("recursive"), Description("For directories: delete contents recursively. Defaults to false for safety.")]
            bool recursive = false)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            // Don't require existence for either file or dir — check manually
            string fullPath = ResolveAndValidateGamePath(assetsPath, path, out error, mustExist: false, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            string relativePath = Path.GetRelativePath(assetsPath, fullPath).Replace('\\', '/');

            if (Directory.Exists(fullPath))
            {
                if (!recursive && Directory.EnumerateFileSystemEntries(fullPath).Any())
                    return Task.FromResult(new McpToolResponse($"Directory '{relativePath}' is not empty. Use recursive=true to delete contents.", isError: true));

                Directory.Delete(fullPath, recursive);
                return Task.FromResult(new McpToolResponse(
                    $"Deleted directory '{relativePath}'.",
                    new { path = relativePath, type = "directory", recursive, deleted = true }));
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return Task.FromResult(new McpToolResponse(
                    $"Deleted file '{relativePath}'.",
                    new { path = relativePath, type = "file", deleted = true }));
            }

            return Task.FromResult(new McpToolResponse($"Path not found: '{relativePath}'.", isError: true));
        }

        /// <summary>
        /// Renames or moves a file within game assets.
        /// </summary>
        [XRMcp(Name = "rename_game_asset", Permission = McpPermissionLevel.Destructive, PermissionReason = "Moves/renames a file on disk.")]
        [Description("Rename or move a file or directory within the game project's assets directory.")]
        public static Task<McpToolResponse> RenameGameAssetAsync(
            McpToolContext context,
            [McpName("old_path"), Description("Current relative path within game assets.")]
            string oldPath,
            [McpName("new_path"), Description("New relative path within game assets.")]
            string newPath)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string fullOldPath = ResolveAndValidateGamePath(assetsPath, oldPath, out error, mustExist: true, expectDirectory: false);
            if (error is not null)
            {
                // Maybe it's a directory
                fullOldPath = ResolveAndValidateGamePath(assetsPath, oldPath, out error, mustExist: true, expectDirectory: true);
                if (error is not null)
                    return Task.FromResult(error);
            }

            string fullNewPath = ResolveAndValidateGamePath(assetsPath, newPath, out error, mustExist: false, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            if (File.Exists(fullNewPath) || Directory.Exists(fullNewPath))
                return Task.FromResult(new McpToolResponse($"Destination already exists: '{newPath}'.", isError: true));

            // Ensure destination parent directory exists
            string? destDir = Path.GetDirectoryName(fullNewPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            bool isDir = Directory.Exists(fullOldPath);
            if (isDir)
                Directory.Move(fullOldPath, fullNewPath);
            else
                File.Move(fullOldPath, fullNewPath);

            return Task.FromResult(new McpToolResponse(
                $"Renamed '{oldPath}' to '{newPath}'.",
                new
                {
                    oldPath = Path.GetRelativePath(assetsPath, fullOldPath).Replace('\\', '/'),
                    newPath = Path.GetRelativePath(assetsPath, fullNewPath).Replace('\\', '/'),
                    type = isDir ? "directory" : "file",
                    renamed = true
                }));
        }

        /// <summary>
        /// Copies a file within game assets.
        /// </summary>
        [XRMcp(Name = "copy_game_asset", Permission = McpPermissionLevel.Destructive, PermissionReason = "Creates a new file on disk.")]
        [Description("Copy a file within the game project's assets directory.")]
        public static Task<McpToolResponse> CopyGameAssetAsync(
            McpToolContext context,
            [McpName("source_path"), Description("Source relative path within game assets.")]
            string sourcePath,
            [McpName("dest_path"), Description("Destination relative path within game assets.")]
            string destPath,
            [McpName("overwrite"), Description("Overwrite if destination exists.")]
            bool overwrite = false)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string fullSourcePath = ResolveAndValidateGamePath(assetsPath, sourcePath, out error, mustExist: true, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            string fullDestPath = ResolveAndValidateGamePath(assetsPath, destPath, out error, mustExist: false, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            if (!overwrite && File.Exists(fullDestPath))
                return Task.FromResult(new McpToolResponse($"Destination already exists: '{destPath}'. Set overwrite=true to replace.", isError: true));

            // Ensure destination parent directory exists
            string? destDir = Path.GetDirectoryName(fullDestPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Copy(fullSourcePath, fullDestPath, overwrite);

            return Task.FromResult(new McpToolResponse(
                $"Copied '{sourcePath}' to '{destPath}'.",
                new
                {
                    sourcePath = Path.GetRelativePath(assetsPath, fullSourcePath).Replace('\\', '/'),
                    destPath = Path.GetRelativePath(assetsPath, fullDestPath).Replace('\\', '/'),
                    overwritten = overwrite && File.Exists(fullDestPath),
                    copied = true
                }));
        }

        /// <summary>
        /// Gets the full directory tree as nested JSON.
        /// </summary>
        [XRMcp(Name = "get_game_asset_tree", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Get the full directory tree of the game project's assets directory as nested JSON.")]
        public static Task<McpToolResponse> GetGameAssetTreeAsync(
            McpToolContext context,
            [McpName("max_depth"), Description("Maximum depth to traverse. 0 for unlimited.")]
            int maxDepth = 0,
            [McpName("include_file_info"), Description("Include file size and extension info for each file.")]
            bool includeFileInfo = false)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            var tree = BuildDirectoryTree(assetsPath, assetsPath, maxDepth, 0, includeFileInfo);
            int totalFiles = CountFilesInTree(tree);
            int totalDirs = CountDirsInTree(tree);

            return Task.FromResult(new McpToolResponse(
                $"Asset tree: {totalDirs} folder(s) and {totalFiles} file(s).",
                tree));
        }

        // ═══════════════════════════════════════════════════════════════════
        // P3.2 — Engine-Aware Asset Operations
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a new typed engine asset and saves to game assets.
        /// </summary>
        [XRMcp(Name = "create_asset", Permission = McpPermissionLevel.Destructive, PermissionReason = "Creates a new engine asset file on disk.")]
        [Description("Create a new typed engine asset (e.g., material, texture, animation) and save it to the game project's assets directory.")]
        public static Task<McpToolResponse> CreateAssetAsync(
            McpToolContext context,
            [McpName("asset_type"), Description("Fully-qualified or short type name of the asset to create (must derive from XRAsset).")]
            string assetType,
            [McpName("name"), Description("Display name for the new asset.")]
            string name,
            [McpName("dest_folder"), Description("Relative folder within game assets where the .asset file will be saved. Empty for root.")]
            string destFolder = "",
            [McpName("properties"), Description("Optional JSON object of property name/value pairs to set on the asset after creation.")]
            Dictionary<string, object>? properties = null)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            // Resolve the type
            if (!TryResolveAnyType(assetType, out var type))
                return Task.FromResult(new McpToolResponse($"Type '{assetType}' not found in any loaded assembly.", isError: true));

            if (!typeof(XRAsset).IsAssignableFrom(type))
                return Task.FromResult(new McpToolResponse($"Type '{type.FullName}' does not derive from XRAsset.", isError: true));

            if (type.IsAbstract || type.IsInterface)
                return Task.FromResult(new McpToolResponse($"Cannot instantiate abstract type '{type.FullName}'.", isError: true));

            // Create instance
            XRAsset? asset;
            try
            {
                asset = Activator.CreateInstance(type) as XRAsset;
            }
            catch (Exception ex)
            {
                return Task.FromResult(new McpToolResponse($"Failed to create instance of '{type.FullName}': {ex.Message}", isError: true));
            }

            if (asset is null)
                return Task.FromResult(new McpToolResponse($"Failed to create instance of '{type.FullName}'.", isError: true));

            asset.Name = name;

            // Apply optional properties via reflection
            if (properties is not null)
            {
                var errors = new List<string>();
                foreach (var kvp in properties)
                {
                    var prop = type.GetProperty(kvp.Key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop is null || !prop.CanWrite)
                    {
                        errors.Add($"Property '{kvp.Key}' not found or not writable.");
                        continue;
                    }

                    try
                    {
                        object? convertedValue = ConvertPropertyValue(kvp.Value, prop.PropertyType);
                        prop.SetValue(asset, convertedValue);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to set '{kvp.Key}': {ex.Message}");
                    }
                }

                if (errors.Count > 0 && properties.Count == errors.Count)
                    return Task.FromResult(new McpToolResponse(
                        $"All property assignments failed: {string.Join("; ", errors)}",
                        isError: true));
            }

            // Resolve destination folder
            string targetDir = string.IsNullOrWhiteSpace(destFolder)
                ? assetsPath
                : ResolveAndValidateGamePath(assetsPath, destFolder, out error, mustExist: false, expectDirectory: true);

            if (error is not null)
                return Task.FromResult(error);

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            // Save asset
            Engine.Assets.SaveTo(asset, targetDir);

            string relativePath = asset.FilePath is not null
                ? Path.GetRelativePath(assetsPath, asset.FilePath).Replace('\\', '/')
                : $"{destFolder}/{name}.asset";

            return Task.FromResult(new McpToolResponse(
                $"Created {type.Name} asset '{name}' at '{relativePath}'.",
                new
                {
                    path = relativePath,
                    assetType = type.FullName,
                    name,
                    assetId = asset.ID.ToString(),
                    created = true
                }));
        }

        /// <summary>
        /// Imports a third-party file (GLTF, FBX, OBJ, PNG, WAV, etc.) into game assets using the engine's import pipeline.
        /// </summary>
        [XRMcp(Name = "import_third_party_asset", Permission = McpPermissionLevel.Destructive, PermissionReason = "Copies and imports files into the game project.")]
        [Description("Import a third-party file (GLTF, FBX, OBJ, PNG, WAV, etc.) into game assets using the engine's import pipeline.")]
        public static Task<McpToolResponse> ImportThirdPartyAssetAsync(
            McpToolContext context,
            [McpName("source_path"), Description("Absolute path to the source file to import.")]
            string sourcePath,
            [McpName("dest_folder"), Description("Relative folder within game assets to copy the source file into. Empty for root.")]
            string destFolder = "")
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            // Validate source exists
            if (!File.Exists(sourcePath))
                return Task.FromResult(new McpToolResponse($"Source file not found: '{sourcePath}'.", isError: true));

            // Resolve destination
            string targetDir = string.IsNullOrWhiteSpace(destFolder)
                ? assetsPath
                : ResolveAndValidateGamePath(assetsPath, destFolder, out error, mustExist: false, expectDirectory: true);

            if (error is not null)
                return Task.FromResult(error);

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            // Copy source file into game assets if not already there
            string destFilePath = Path.Combine(targetDir, Path.GetFileName(sourcePath));
            string sourceFullPath = Path.GetFullPath(sourcePath);
            string normalizedRoot = Path.GetFullPath(assetsPath);

            // Only copy if source is outside game assets
            if (!sourceFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(destFilePath))
                    return Task.FromResult(new McpToolResponse(
                        $"File '{Path.GetFileName(sourcePath)}' already exists in destination. Delete it first or rename.",
                        isError: true));

                File.Copy(sourceFullPath, destFilePath);
            }
            else
            {
                destFilePath = sourceFullPath;
            }

            // Trigger engine load which auto-dispatches to the 3rd party import pipeline
            XRAsset? loaded = null;
            try
            {
                loaded = Engine.Assets.Load(destFilePath, typeof(XRAsset));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new McpToolResponse(
                    $"Import failed: {ex.Message}",
                    isError: true));
            }

            if (loaded is null)
            {
                string relativeDest = Path.GetRelativePath(assetsPath, destFilePath).Replace('\\', '/');
                return Task.FromResult(new McpToolResponse(
                    $"File copied to '{relativeDest}' but engine could not import it. The file extension may not have a registered importer.",
                    new { path = relativeDest, imported = false, copied = true }));
            }

            string relPath = Path.GetRelativePath(assetsPath, destFilePath).Replace('\\', '/');
            return Task.FromResult(new McpToolResponse(
                $"Imported '{Path.GetFileName(sourcePath)}' as {loaded.GetType().Name} at '{relPath}'.",
                new
                {
                    path = relPath,
                    assetType = loaded.GetType().FullName,
                    assetId = loaded.ID.ToString(),
                    name = loaded.Name,
                    imported = true
                }));
        }

        /// <summary>
        /// Force-reloads an asset from disk after external edits.
        /// </summary>
        [XRMcp(Name = "reload_asset", Permission = McpPermissionLevel.Mutate)]
        [Description("Force-reload an asset from disk after external edits. Specify by asset GUID or file path.")]
        public static Task<McpToolResponse> ReloadAssetAsync(
            McpToolContext context,
            [McpName("asset_id"), Description("GUID of the asset to reload. Takes priority over asset_path if both provided.")]
            string? assetId = null,
            [McpName("asset_path"), Description("Relative path to the asset file within game assets.")]
            string? assetPath = null)
        {
            if (string.IsNullOrWhiteSpace(assetId) && string.IsNullOrWhiteSpace(assetPath))
                return Task.FromResult(new McpToolResponse("Provide either asset_id or asset_path.", isError: true));

            XRAsset? asset = null;

            // Try GUID lookup first
            if (!string.IsNullOrWhiteSpace(assetId) && Guid.TryParse(assetId, out Guid guid))
            {
                Engine.Assets?.TryGetAssetByID(guid, out asset);
            }

            // Try path lookup
            if (asset is null && !string.IsNullOrWhiteSpace(assetPath))
            {
                if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                    return Task.FromResult(error!);

                string fullPath = ResolveAndValidateGamePath(assetsPath, assetPath, out error, mustExist: true, expectDirectory: false);
                if (error is not null)
                    return Task.FromResult(error);

                Engine.Assets?.TryGetAssetByPath(fullPath, out asset);

                // If not cached, try loading it fresh
                if (asset is null)
                {
                    asset = Engine.Assets?.Load(fullPath, typeof(XRAsset));
                    if (asset is not null)
                    {
                        return Task.FromResult(new McpToolResponse(
                            $"Loaded asset '{asset.Name ?? Path.GetFileName(fullPath)}' (was not previously cached).",
                            new
                            {
                                assetId = asset.ID.ToString(),
                                name = asset.Name,
                                type = asset.GetType().FullName,
                                filePath = asset.FilePath,
                                reloaded = true
                            }));
                    }
                }
            }

            if (asset is null)
                return Task.FromResult(new McpToolResponse("Asset not found in loaded assets.", isError: true));

            asset.Reload();

            return Task.FromResult(new McpToolResponse(
                $"Reloaded asset '{asset.Name}'.",
                new
                {
                    assetId = asset.ID.ToString(),
                    name = asset.Name,
                    type = asset.GetType().FullName,
                    filePath = asset.FilePath,
                    reloaded = true
                }));
        }

        /// <summary>
        /// Lists all assets referenced by a given asset by walking its embedded asset graph.
        /// </summary>
        [XRMcp(Name = "get_asset_dependencies", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List all assets referenced/embedded by a given asset. Specify by asset GUID or file path.")]
        public static Task<McpToolResponse> GetAssetDependenciesAsync(
            McpToolContext context,
            [McpName("asset_id"), Description("GUID of the asset.")]
            string? assetId = null,
            [McpName("asset_path"), Description("Relative path to the asset file within game assets.")]
            string? assetPath = null)
        {
            if (string.IsNullOrWhiteSpace(assetId) && string.IsNullOrWhiteSpace(assetPath))
                return Task.FromResult(new McpToolResponse("Provide either asset_id or asset_path.", isError: true));

            XRAsset? asset = ResolveAsset(assetId, assetPath, out McpToolResponse? error);
            if (error is not null)
                return Task.FromResult(error);
            if (asset is null)
                return Task.FromResult(new McpToolResponse("Asset not found.", isError: true));

            // Refresh the asset graph to ensure embedded assets are up to date
            XRAssetGraphUtility.RefreshAssetGraph(asset);

            var embeddedAssets = asset.EmbeddedAssets
                .Where(e => e is not null)
                .Select(e => new
                {
                    assetId = e.ID.ToString(),
                    name = e.Name,
                    type = e.GetType().FullName,
                    filePath = e.FilePath,
                    originalPath = e.OriginalPath
                })
                .ToArray();

            return Task.FromResult(new McpToolResponse(
                $"Asset '{asset.Name}' has {embeddedAssets.Length} embedded/referenced asset(s).",
                new
                {
                    assetId = asset.ID.ToString(),
                    name = asset.Name,
                    type = asset.GetType().FullName,
                    dependencyCount = embeddedAssets.Length,
                    dependencies = embeddedAssets
                }));
        }

        /// <summary>
        /// Reverse lookup: finds all loaded assets and scene nodes that reference a given asset.
        /// </summary>
        [XRMcp(Name = "get_asset_references", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Find all loaded assets and scene nodes that reference a given asset. Specify by asset GUID or file path.")]
        public static Task<McpToolResponse> GetAssetReferencesAsync(
            McpToolContext context,
            [McpName("asset_id"), Description("GUID of the asset to find references for.")]
            string? assetId = null,
            [McpName("asset_path"), Description("Relative path to the asset file within game assets.")]
            string? assetPath = null)
        {
            if (string.IsNullOrWhiteSpace(assetId) && string.IsNullOrWhiteSpace(assetPath))
                return Task.FromResult(new McpToolResponse("Provide either asset_id or asset_path.", isError: true));

            XRAsset? targetAsset = ResolveAsset(assetId, assetPath, out McpToolResponse? error);
            if (error is not null)
                return Task.FromResult(error);
            if (targetAsset is null)
                return Task.FromResult(new McpToolResponse("Asset not found.", isError: true));

            var referencingAssets = new List<object>();

            // Scan all loaded assets for ones that embed this asset
            if (Engine.Assets is not null)
            {
                foreach (var kvp in Engine.Assets.LoadedAssetsByIDInternal)
                {
                    var candidate = kvp.Value;
                    if (candidate == targetAsset)
                        continue;

                    if (candidate.EmbeddedAssets.Contains(targetAsset))
                    {
                        referencingAssets.Add(new
                        {
                            assetId = candidate.ID.ToString(),
                            name = candidate.Name,
                            type = candidate.GetType().FullName,
                            filePath = candidate.FilePath
                        });
                    }
                }
            }

            // Also scan active world scenes for scene nodes using this asset
            var referencingNodes = new List<object>();
            var world = context.WorldInstance;
            if (world is not null)
            {
                // Traverse all scenes and their node trees
                foreach (var scene in world.TargetWorld?.Scenes ?? [])
                {
                    foreach (var rootNode in scene.RootNodes)
                    {
                        if (rootNode is null) continue;
                        CollectAssetReferences(rootNode, targetAsset, referencingNodes);
                    }
                }
            }

            return Task.FromResult(new McpToolResponse(
                $"Found {referencingAssets.Count} referencing asset(s) and {referencingNodes.Count} scene node reference(s).",
                new
                {
                    assetId = targetAsset.ID.ToString(),
                    name = targetAsset.Name,
                    referencingAssetCount = referencingAssets.Count,
                    referencingAssets,
                    sceneNodeReferenceCount = referencingNodes.Count,
                    sceneNodeReferences = referencingNodes
                }));
        }

        /// <summary>
        /// Triggers cooking/packaging of an asset for runtime use using the cooked binary serializer.
        /// </summary>
        [XRMcp(Name = "cook_asset", Permission = McpPermissionLevel.Destructive, PermissionReason = "Creates cooked binary output files on disk.")]
        [Description("Cook/package an asset for optimized runtime loading. Creates a cooked binary file at the specified output location.")]
        public static Task<McpToolResponse> CookAssetAsync(
            McpToolContext context,
            [McpName("asset_path"), Description("Relative path to the asset file within game assets.")]
            string assetPath,
            [McpName("output_dir"), Description("Relative output directory within game assets for the cooked file. Defaults to Build/ in the project root.")]
            string? outputDir = null)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string fullAssetPath = ResolveAndValidateGamePath(assetsPath, assetPath, out error, mustExist: true, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            // Load the asset
            XRAsset? asset = Engine.Assets?.Load(fullAssetPath, typeof(XRAsset));
            if (asset is null)
                return Task.FromResult(new McpToolResponse($"Could not load asset at '{assetPath}'.", isError: true));

            // Resolve output directory
            string cookedOutputDir;
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                cookedOutputDir = ResolveAndValidateGamePath(assetsPath, outputDir, out error, mustExist: false, expectDirectory: true);
                if (error is not null)
                    return Task.FromResult(error);
            }
            else
            {
                // Default to project Build directory
                var project = Engine.CurrentProject;
                cookedOutputDir = project?.BuildDirectory
                    ?? Path.Combine(Path.GetDirectoryName(assetsPath) ?? assetsPath, "Build");
            }

            if (!Directory.Exists(cookedOutputDir))
                Directory.CreateDirectory(cookedOutputDir);

            // Save the asset to the cooked output using the standard engine serializer
            try
            {
                Engine.Assets?.SaveTo(asset, cookedOutputDir);
            }
            catch (Exception ex)
            {
                return Task.FromResult(new McpToolResponse($"Cook failed: {ex.Message}", isError: true));
            }

            string cookedPath = asset.FilePath ?? Path.Combine(cookedOutputDir, $"{asset.Name}.asset");
            string relCookedPath = Path.GetRelativePath(assetsPath, cookedPath).Replace('\\', '/');

            return Task.FromResult(new McpToolResponse(
                $"Cooked asset '{asset.Name}' to '{relCookedPath}'.",
                new
                {
                    sourcePath = assetPath,
                    cookedPath = relCookedPath,
                    assetType = asset.GetType().FullName,
                    assetId = asset.ID.ToString(),
                    cooked = true
                }));
        }

        // ═══════════════════════════════════════════════════════════════════
        // Game Assets Helpers
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolves an asset from either GUID or relative path.
        /// </summary>
        private static XRAsset? ResolveAsset(string? assetId, string? assetPath, out McpToolResponse? error)
        {
            error = null;
            XRAsset? asset = null;

            if (!string.IsNullOrWhiteSpace(assetId) && Guid.TryParse(assetId, out Guid guid))
                Engine.Assets?.TryGetAssetByID(guid, out asset);

            if (asset is null && !string.IsNullOrWhiteSpace(assetPath))
            {
                if (!TryGetGameAssetsPath(out string assetsPath, out error))
                    return null;

                string fullPath = ResolveAndValidateGamePath(assetsPath, assetPath, out error, mustExist: true, expectDirectory: false);
                if (error is not null)
                    return null;

                Engine.Assets?.TryGetAssetByPath(fullPath, out asset);

                // If not in cache, try loading it
                asset ??= Engine.Assets?.Load(fullPath, typeof(XRAsset));
            }

            return asset;
        }

        /// <summary>
        /// Builds a nested directory tree structure.
        /// </summary>
        private static object BuildDirectoryTree(string rootPath, string currentPath, int maxDepth, int currentDepth, bool includeFileInfo)
        {
            string relativePath = Path.GetRelativePath(rootPath, currentPath).Replace('\\', '/');
            if (relativePath == ".")
                relativePath = "";

            var children = new List<object>();

            // Add files
            try
            {
                foreach (string file in Directory.GetFiles(currentPath))
                {
                    if (includeFileInfo)
                    {
                        var fi = new FileInfo(file);
                        children.Add(new
                        {
                            name = Path.GetFileName(file),
                            type = "file",
                            extension = Path.GetExtension(file).TrimStart('.'),
                            size = fi.Length,
                            lastModified = fi.LastWriteTimeUtc.ToString("o")
                        });
                    }
                    else
                    {
                        children.Add(new
                        {
                            name = Path.GetFileName(file),
                            type = "file",
                            extension = Path.GetExtension(file).TrimStart('.')
                        });
                    }
                }
            }
            catch { /* Skip inaccessible directories */ }

            // Add subdirectories (recurse if within depth limit)
            if (maxDepth <= 0 || currentDepth < maxDepth)
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(currentPath))
                    {
                        children.Add(BuildDirectoryTree(rootPath, dir, maxDepth, currentDepth + 1, includeFileInfo));
                    }
                }
                catch { /* Skip inaccessible directories */ }
            }

            return new
            {
                name = Path.GetFileName(currentPath),
                path = relativePath,
                type = "directory",
                children
            };
        }

        /// <summary>
        /// Counts total files in a nested tree structure.
        /// </summary>
        private static int CountFilesInTree(object tree)
        {
            if (tree is not { } obj)
                return 0;

            int count = 0;
            var type = obj.GetType();
            var childrenProp = type.GetProperty("children");
            if (childrenProp?.GetValue(obj) is IEnumerable<object> children)
            {
                foreach (var child in children)
                {
                    var childType = child.GetType();
                    var typeProp = childType.GetProperty("type");
                    string? nodeType = typeProp?.GetValue(child)?.ToString();

                    if (nodeType == "file")
                        count++;
                    else if (nodeType == "directory")
                        count += CountFilesInTree(child);
                }
            }

            return count;
        }

        /// <summary>
        /// Counts total directories in a nested tree structure.
        /// </summary>
        private static int CountDirsInTree(object tree)
        {
            if (tree is not { } obj)
                return 0;

            int count = 0;
            var type = obj.GetType();
            var childrenProp = type.GetProperty("children");
            if (childrenProp?.GetValue(obj) is IEnumerable<object> children)
            {
                foreach (var child in children)
                {
                    var childType = child.GetType();
                    var typeProp = childType.GetProperty("type");
                    string? nodeType = typeProp?.GetValue(child)?.ToString();

                    if (nodeType == "directory")
                    {
                        count++;
                        count += CountDirsInTree(child);
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Converts a JSON-deserialized value to the target property type.
        /// </summary>
        private static object? ConvertPropertyValue(object? value, Type targetType)
        {
            if (value is null)
                return null;

            if (targetType.IsAssignableFrom(value.GetType()))
                return value;

            // Handle JsonElement
            if (value is System.Text.Json.JsonElement je)
            {
                if (targetType == typeof(string))
                    return je.GetString();
                if (targetType == typeof(int))
                    return je.GetInt32();
                if (targetType == typeof(float))
                    return je.GetSingle();
                if (targetType == typeof(double))
                    return je.GetDouble();
                if (targetType == typeof(bool))
                    return je.GetBoolean();
                if (targetType == typeof(long))
                    return je.GetInt64();

                return System.Text.Json.JsonSerializer.Deserialize(je.GetRawText(), targetType);
            }

            // Standard conversion
            if (targetType.IsEnum && value is string enumStr)
                return Enum.Parse(targetType, enumStr, ignoreCase: true);

            return Convert.ChangeType(value, targetType);
        }

        /// <summary>
        /// Recursively collects scene nodes that reference a target asset via their components' properties.
        /// </summary>
        private static void CollectAssetReferences(SceneNode node, XRAsset targetAsset, List<object> results)
        {
            foreach (var comp in node.Components)
            {
                foreach (var prop in comp.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!typeof(XRAsset).IsAssignableFrom(prop.PropertyType))
                        continue;

                    try
                    {
                        var val = prop.GetValue(comp);
                        if (val == targetAsset)
                        {
                            results.Add(new
                            {
                                nodeId = node.ID.ToString(),
                                nodeName = node.Name,
                                componentType = comp.GetType().Name,
                                propertyName = prop.Name
                            });
                        }
                    }
                    catch
                    {
                        // Skip properties that throw on read
                    }
                }
            }

            // Recurse into children
            foreach (var child in GetChildren(node))
                CollectAssetReferences(child, targetAsset, results);
        }
    }
}
