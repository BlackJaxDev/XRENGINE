using Extensions;
using Microsoft.DotNet.PlatformAbstractions;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace XREngine
{
    public enum RemoteAssetLoadMode
    {
        None = 0,
        RequestFromRemote = 1,
        SendLocalCopy = 2,
    }

    public class AssetManager
    {
        public const string AssetExtension = "asset";
        private const string ImportOptionsFileExtension = "import.yaml";

        private static bool IsOnJobThread
            => JobManager.IsJobWorkerThread;

        private static void RunOnJobThreadBlocking(Action action, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
            => RunOnJobThreadBlocking(() => { action(); return true; }, priority, bypassJobThread);

        private static T RunOnJobThreadBlocking<T>(Func<T> work, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
        {
            ArgumentNullException.ThrowIfNull(work);

            // Never schedule+block from inside a job worker thread; doing so can exhaust the worker pool
            // and deadlock (no workers left to run the scheduled continuation).
            if (bypassJobThread || IsOnJobThread)
                return work();

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Engine.Jobs.Schedule(() => JobRoutine(work, tcs), priority: priority);
            return tcs.Task.GetAwaiter().GetResult();
        }

        private static Task RunOnJobThreadAsync(Action action, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
            => RunOnJobThreadAsync(() => { action(); return true; }, priority, bypassJobThread);

        private static Task<T> RunOnJobThreadAsync<T>(Func<T> work, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
        {
            ArgumentNullException.ThrowIfNull(work);

            // If we're already on a job worker thread, execute inline.
            if (bypassJobThread || IsOnJobThread)
                return Task.FromResult(work());

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Engine.Jobs.Schedule(() => JobRoutine(work, tcs), priority: priority);
            return tcs.Task;
        }

        private static IEnumerable JobRoutine<T>(Func<T> work, TaskCompletionSource<T> tcs)
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            yield break;
        }

        public AssetManager(string? engineAssetsDirPath = null)
        {
            string? resolvedEngineAssetsPath = null;
            if (!string.IsNullOrWhiteSpace(engineAssetsDirPath) && Directory.Exists(engineAssetsDirPath))
            {
                resolvedEngineAssetsPath = engineAssetsDirPath;
            }
            else
            {
                string? basePath = ApplicationEnvironment.ApplicationBasePath;
                //Iterate up the directory tree until we find a Build directory that contains CommonAssets
                while (basePath is not null)
                {
                    string candidate = Path.Combine(basePath, "Build", "CommonAssets");
                    if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "Shaders")))
                    {
                        resolvedEngineAssetsPath = candidate;
                        break;
                    }
                    basePath = Path.GetDirectoryName(basePath);
                }

                resolvedEngineAssetsPath ??= Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Build", "CommonAssets");
            }

            EngineAssetsPath = resolvedEngineAssetsPath;

            // In sandbox mode (no project loaded), the editor commonly runs with
            // Environment.CurrentDirectory pointing at the repo/project root.
            // Prefer an existing '<cwd>/Assets' over '<exe>/Assets' to avoid writing
            // import options/cache next to the build output.
            string cwdAssets = Path.Combine(Environment.CurrentDirectory, "Assets");
            if (!Directory.Exists(GameAssetsPath) && Directory.Exists(cwdAssets))
                GameAssetsPath = cwdAssets;

            VerifyDirectoryExists(GameAssetsPath);

            // Best-effort defaults for sandbox mode. Project load will overwrite these.
            EnsureGameMetadataPathInitialized();
            EnsureGameCachePathInitialized();
            if (!Directory.Exists(EngineAssetsPath))
                throw new DirectoryNotFoundException($"Could not find the engine assets directory at '{EngineAssetsPath}'.");

            GameWatcher.Path = GameAssetsPath;
            GameWatcher.Filter = "*.*";
            GameWatcher.IncludeSubdirectories = true;
            GameWatcher.EnableRaisingEvents = true;
            GameWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;
            GameWatcher.Created += OnGameFileCreated;
            GameWatcher.Changed += OnGameFileChanged;
            GameWatcher.Deleted += OnGameFileDeleted;
            GameWatcher.Error += OnGameFileError;
            GameWatcher.Renamed += OnGameFileRenamed;

            EngineWatcher.Path = EngineAssetsPath;
            EngineWatcher.Filter = "*.*";
            EngineWatcher.IncludeSubdirectories = true;
            EngineWatcher.EnableRaisingEvents = true;
            EngineWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;
            EngineWatcher.Created += OnEngineFileCreated;
            EngineWatcher.Changed += OnEngineFileChanged;
            EngineWatcher.Deleted += OnEngineFileDeleted;
            EngineWatcher.Error += OnEngineFileError;
            EngineWatcher.Renamed += OnEngineFileRenamed;
        }

        public bool MonitorGameAssetsForChanges
        {
            get => GameWatcher.EnableRaisingEvents;
            set => GameWatcher.EnableRaisingEvents = value;
        }

        public bool MonitorEngineAssetsForChanges
        {
            get => EngineWatcher.EnableRaisingEvents;
            set => EngineWatcher.EnableRaisingEvents = value;
        }

        private static bool VerifyDirectoryExists(string? directoryPath)
        {
            //If the path is null or empty, return false
            if (string.IsNullOrWhiteSpace(directoryPath))
                return false;

            //If the path is a file, get the directory
            if (Path.HasExtension(directoryPath))
            {
                directoryPath = Path.GetDirectoryName(directoryPath);

                //If the directory is null or empty, return false
                if (string.IsNullOrWhiteSpace(directoryPath))
                    return false;
            }

            //If the current directory exists, return true
            if (Directory.Exists(directoryPath))
                return true;

            //Recursively create the parent directories
            string? parent = Path.GetDirectoryName(directoryPath);
            if (VerifyDirectoryExists(parent))
                Directory.CreateDirectory(directoryPath);

            return true;
        }

        private static string NormalizeDirectoryPath(string path, string argumentName)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException($"{argumentName} cannot be null or empty.", argumentName);

            string fullPath = Path.GetFullPath(path);
            VerifyDirectoryExists(fullPath);
            return fullPath;
        }

        private void EnsureGameCachePathInitialized()
        {
            if (!string.IsNullOrWhiteSpace(_gameCachePath))
                return;

            if (string.IsNullOrWhiteSpace(GameAssetsPath))
                return;

            if (!TryInferProjectRootFromGameAssetsPath(out string? projectRoot) || string.IsNullOrWhiteSpace(projectRoot))
                return;

            // Default to '<ProjectRoot>/Cache' which matches the project's structure.
            GameCachePath = Path.Combine(projectRoot, "Cache");
        }

        private void EnsureGameMetadataPathInitialized()
        {
            if (!string.IsNullOrWhiteSpace(_gameMetadataPath))
                return;

            if (!TryInferProjectRootFromGameAssetsPath(out string? projectRoot) || string.IsNullOrWhiteSpace(projectRoot))
                return;

            // Default to '<ProjectRoot>/Metadata' which matches XRProject structure.
            GameMetadataPath = Path.Combine(projectRoot, "Metadata");
        }

        private bool TryInferProjectRootFromGameAssetsPath([NotNullWhen(true)] out string? projectRoot)
        {
            projectRoot = null;

            if (string.IsNullOrWhiteSpace(GameAssetsPath))
                return false;

            try
            {
                string assetsPath = Path.GetFullPath(GameAssetsPath);

                // Common case: GameAssetsPath points at '<ProjectRoot>/Assets'.
                if (string.Equals(Path.GetFileName(assetsPath), "Assets", StringComparison.OrdinalIgnoreCase))
                {
                    projectRoot = Path.GetDirectoryName(assetsPath);
                    return !string.IsNullOrWhiteSpace(projectRoot);
                }

                // If GameAssetsPath is actually the project root, prefer it when it contains an Assets folder.
                if (Directory.Exists(Path.Combine(assetsPath, "Assets")))
                {
                    projectRoot = assetsPath;
                    return true;
                }

                // Fallback: use the parent directory of the provided assets path.
                projectRoot = Path.GetDirectoryName(assetsPath);
                return !string.IsNullOrWhiteSpace(projectRoot);
            }
            catch
            {
                return false;
            }
        }

        private static string? NormalizeOptionalDirectoryPath(string? path, string argumentName)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return NormalizeDirectoryPath(path, argumentName);
        }

        private void UpdateGameAssetsPath(string path)
        {
            string normalized = NormalizeDirectoryPath(path, nameof(GameAssetsPath));
            if (string.Equals(_gameAssetsPath, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _gameAssetsPath = normalized;
            GameWatcher.Path = _gameAssetsPath;
        }

        public event Action<FileSystemEventArgs>? EngineFileCreated;
        public event Action<FileSystemEventArgs>? EngineFileChanged;
        public event Action<FileSystemEventArgs>? EngineFileDeleted;
        public event Action<RenamedEventArgs>? EngineFileRenamed;

        public event Action<FileSystemEventArgs>? GameFileCreated;
        public event Action<FileSystemEventArgs>? GameFileChanged;
        public event Action<FileSystemEventArgs>? GameFileDeleted;
        public event Action<RenamedEventArgs>? GameFileRenamed;

        void OnEngineFileCreated(object sender, FileSystemEventArgs args)
        {
            OnFileCreated(args);
            EngineFileCreated?.Invoke(args);
        }
        void OnGameFileCreated(object sender, FileSystemEventArgs args)
        {
            OnFileCreated(args);
            HandleMetadataCreated(args.FullPath);
            TryQueueAutoImportForThirdPartyFile(args.FullPath, reason: "created");
            GameFileCreated?.Invoke(args);
        }
        private static void OnFileCreated(FileSystemEventArgs args)
        {
            Debug.Out($"File '{args.FullPath}' was created.");
        }

        async void OnEngineFileChanged(object sender, FileSystemEventArgs args)
        {
            await OnFileChanged(args);
            EngineFileChanged?.Invoke(args);
        }
        async void OnGameFileChanged(object sender, FileSystemEventArgs args)
        {
            await OnFileChanged(args);
            HandleMetadataChanged(args.FullPath);
            TryQueueAutoImportForThirdPartyFile(args.FullPath, reason: "changed");
            GameFileChanged?.Invoke(args);
        }
        private async Task OnFileChanged(FileSystemEventArgs args)
        {
            // Skip reload if we just saved this file (within last 2 seconds)
            if (_recentlySavedPaths.TryGetValue(args.FullPath, out var saveTime))
            {
                if ((DateTime.UtcNow - saveTime).TotalSeconds < 2)
                {
                    _recentlySavedPaths.TryRemove(args.FullPath, out _);
                    return;
                }
                _recentlySavedPaths.TryRemove(args.FullPath, out _);
            }
            
            Debug.Out($"File '{args.FullPath}' was changed.");
            var asset = GetAssetByPath(args.FullPath);
            if (asset is not null)
                await asset.ReloadAsync(args.FullPath);
        }

        void OnEngineFileDeleted(object sender, FileSystemEventArgs args)
        {
            OnFileDeleted(args);
            EngineFileDeleted?.Invoke(args);
        }
        void OnGameFileDeleted(object sender, FileSystemEventArgs args)
        {
            OnFileDeleted(args);
            HandleMetadataDeleted(args.FullPath);
            HandleThirdPartyImportOptionsDeleted(args.FullPath);
            GameFileDeleted?.Invoke(args);
        }
        private static void OnFileDeleted(FileSystemEventArgs args)
        {
            Debug.Out($"File '{args.FullPath}' was deleted.");
            //Leave files intact
            //if (LoadedAssetsByPathInternal.TryGetValue(args.FullPath, out var list))
            //{
            //    foreach (var asset in list)
            //        asset.Destroy();
            //    LoadedAssetsByPathInternal.Remove(args.FullPath);
            //}
        }

        void OnGameFileError(object sender, ErrorEventArgs args)
        {
            OnFileError(args);
        }
        void OnEngineFileError(object sender, ErrorEventArgs args)
        {
            OnFileError(args);
        }
        private static void OnFileError(ErrorEventArgs args)
        {
            Debug.LogWarning($"An error occurred in the file system watcher: {args.GetException().Message}");
        }

        void OnGameFileRenamed(object sender, RenamedEventArgs args)
        {
            OnFileRenamed(args);
            HandleMetadataRenamed(args.OldFullPath, args.FullPath);
            HandleThirdPartyImportOptionsRenamed(args.OldFullPath, args.FullPath);
            TryQueueAutoImportForThirdPartyFile(args.FullPath, reason: "renamed");
            GameFileRenamed?.Invoke(args);
        }
        void OnEngineFileRenamed(object sender, RenamedEventArgs args)
        {
            OnFileRenamed(args);
            EngineFileRenamed?.Invoke(args);
        }

        private void OnFileRenamed(RenamedEventArgs args)
        {
            Debug.Out($"File '{args.OldFullPath}' was renamed to '{args.FullPath}'.");

            if (LoadedAssetsByPathInternal.TryGetValue(args.OldFullPath, out var asset))
            {
                LoadedAssetsByPathInternal.Remove(args.OldFullPath, out _);
                LoadedAssetsByPathInternal.TryAdd(args.FullPath, asset);

                asset.FilePath = args.FullPath;
            }
            if (LoadedAssetsByOriginalPathInternal.TryGetValue(args.OldFullPath, out asset))
            {
                LoadedAssetsByOriginalPathInternal.Remove(args.OldFullPath, out _);
                LoadedAssetsByOriginalPathInternal.TryAdd(args.FullPath, asset);

                asset.OriginalPath = args.FullPath;
                asset.Reload();
            }
        }

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

            asset.SerializeTo(generatedAssetPath, Serializer);

            _recentlySavedPaths[generatedAssetPath] = DateTime.UtcNow;
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
            string prefabFolderName = SanitizeFileName(rootName);
            string prefabFolderPath = Path.Combine(directory, prefabFolderName);

            static string KindFolderName(XRAsset a)
            {
                if (IsModel(a)) return "Models";
                if (IsMesh(a)) return "Meshes";
                if (IsSubMesh(a)) return "SubMeshes";
                if (IsMaterial(a)) return "Materials";
                if (IsTexture(a)) return "Textures";
                return "Assets";
            }

            static bool IsTexture(XRAsset a) => a is XRTexture;
            static bool IsMaterial(XRAsset a) => a is XRMaterialBase;
            static bool IsMesh(XRAsset a) => a is XRMesh;
            static bool IsSubMesh(XRAsset a) => a is SubMesh;
            static bool IsModel(XRAsset a) => a is Model;

            static bool IsRelevant(XRAsset a)
                => IsTexture(a) || IsMaterial(a) || IsMesh(a) || IsSubMesh(a) || IsModel(a);

            static int KindOrder(XRAsset a)
            {
                // Prefer saving higher-level containers first.
                if (IsModel(a)) return 0;
                if (IsMesh(a)) return 1;
                if (IsSubMesh(a)) return 2;
                if (IsMaterial(a)) return 3;
                if (IsTexture(a)) return 4;
                return 10;
            }

            static bool HasExistingAssetFile(XRAsset a)
            {
                string? path = a.FilePath;
                if (string.IsNullOrWhiteSpace(path))
                    return false;
                if (!string.Equals(Path.GetExtension(path), $".{AssetExtension}", StringComparison.OrdinalIgnoreCase))
                    return false;
                return File.Exists(path);
            }

            static string SanitizeFileName(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return "Asset";

                foreach (char c in Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');

                name = name.Trim().TrimEnd('.', ' ');
                return string.IsNullOrWhiteSpace(name) ? "Asset" : name;
            }

            static string EnsureHasName(XRAsset asset, string rootName)
            {
                if (!string.IsNullOrWhiteSpace(asset.Name))
                    return asset.Name!;

                string shortId = asset.ID == Guid.Empty
                    ? Guid.NewGuid().ToString("N")[..8]
                    : asset.ID.ToString("N")[..8];
                asset.Name = $"{rootName}_{asset.GetType().Name}_{shortId}";
                return asset.Name;
            }

            // Export assets that would be embedded into the prefab YAML. In practice, prefab-generated sub-assets
            // are typically tagged as owned by the prefab via SourceAsset == rootAsset.
            //
            // We use EmbeddedAssets when available (asset-graph-derived), but also include prefab-owned
            // reachable assets as a fallback/union because some graphs (e.g., SceneNode trees) may not be
            // traversed by XRAssetGraphUtility depending on its leaf/pruning rules.
            XRAssetGraphUtility.RefreshAssetGraph(rootAsset);

            var graphEmbedded = rootAsset.EmbeddedAssets
                .Where(a => a is not null && !ReferenceEquals(a, rootAsset) && IsRelevant(a))
                .Distinct(XRAssetReferenceEqualityComparer.Instance)
                .ToList();

            // Prefer walking the prefab hierarchy rather than the XRPrefabSource object itself.
            // The prefab asset can indirectly reference lots of editor/runtime state; the scene tree
            // is the stable, serialized portion we actually care about.
            object traversalRoot = rootAsset;
            if (rootAsset is XRPrefabSource prefab && prefab.RootNode is not null)
                traversalRoot = prefab.RootNode;

            var reachableRelevant = CollectReachableAssets(traversalRoot)
                .Where(a => a is not null && !ReferenceEquals(a, rootAsset) && IsRelevant(a))
                .Distinct(XRAssetReferenceEqualityComparer.Instance)
                .ToList();

            var embedded = graphEmbedded
                .Concat(reachableRelevant)
                .Distinct(XRAssetReferenceEqualityComparer.Instance)
                .OrderBy(KindOrder)
                .ToList();

            Debug.Log(ELogCategory.General, "[ExternalizeEmbedded] Relevant embedded assets: {0} (graph: {1}, reachable: {2})", embedded.Count, graphEmbedded.Count, reachableRelevant.Count);
            {
                int textures = embedded.Count(IsTexture);
                int materials = embedded.Count(IsMaterial);
                int meshes = embedded.Count(IsMesh);
                int subMeshes = embedded.Count(IsSubMesh);
                int models = embedded.Count(IsModel);
                Debug.Log(ELogCategory.General, "[ExternalizeEmbedded] Breakdown: Model={0}, Mesh={1}, SubMesh={2}, Material={3}, Texture={4}", models, meshes, subMeshes, materials, textures);

                const int sampleCount = 50;
                for (int i = 0; i < embedded.Count && i < sampleCount; i++)
                {
                    var a = embedded[i];
                    Debug.Log(ELogCategory.General, "[ExternalizeEmbedded]   - {0}: '{1}' (ID: {2})", a.GetType().Name, a.Name ?? "(unnamed)", a.ID);
                }
                if (embedded.Count > sampleCount)
                    Debug.Log(ELogCategory.General, "[ExternalizeEmbedded]   ... ({0} more)", embedded.Count - sampleCount);
            }

            int exportedCount = 0;
            int skippedCount = 0;
            int failedCount = 0;
            foreach (var subAsset in embedded)
            {
                // Important: embedded assets typically have SourceAsset == rootAsset.
                // XRAsset.FilePath then delegates to SourceAsset.FilePath, which makes it look like
                // the sub-asset already has a real .asset file (the prefab's file). Self-root first
                // so FilePath reflects the sub-asset's own path, enabling correct export.
                bool wasRerooted = false;
                if (!ReferenceEquals(subAsset.SourceAsset, subAsset))
                {
                    subAsset.SourceAsset = subAsset;
                    wasRerooted = true;
                }

                if (HasExistingAssetFile(subAsset))
                {
                    skippedCount++;
                    Debug.Log(ELogCategory.General, "[ExternalizeEmbedded] Skipping (already exists): {0} '{1}' at '{2}'", subAsset.GetType().Name, subAsset.Name, subAsset.FilePath);
                    continue;
                }

                string displayName = EnsureHasName(subAsset, rootName);
                string safeName = SanitizeFileName(displayName);

                string kindFolder = KindFolderName(subAsset);
                string kindFolderPath = Path.Combine(prefabFolderPath, kindFolder);

                // Ensure folder structure exists and has stable metadata.
                Directory.CreateDirectory(prefabFolderPath);
                Directory.CreateDirectory(kindFolderPath);
                EnsureMetadataForAssetPath(prefabFolderPath, isDirectory: true);
                EnsureMetadataForAssetPath(kindFolderPath, isDirectory: true);

                string candidatePath = Path.Combine(kindFolderPath, $"{safeName}.{AssetExtension}");
                string targetPath = GetUniqueAssetPath(candidatePath);

                Debug.Log(ELogCategory.General, "[ExternalizeEmbedded] Exporting {0} '{1}' -> '{2}' (was rerooted: {3})", subAsset.GetType().Name, displayName, targetPath, wasRerooted);

                try
                {
                    SaveAssetToPathCore(subAsset, targetPath);
                    EnsureMetadataForAssetPath(targetPath, isDirectory: false);
                    exportedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    Debug.LogException(ex, $"[ExternalizeEmbedded] Failed exporting {subAsset.GetType().Name} '{displayName}' -> '{targetPath}'");
                }
            }

            Debug.Log(ELogCategory.General, "[ExternalizeEmbedded] Externalization complete: {0} exported, {1} skipped (already exist), {2} failed", exportedCount, skippedCount, failedCount);

            // Recompute to ensure the root no longer treats these as embedded.
            XRAssetGraphUtility.RefreshAssetGraph(rootAsset);
        }

        private void SaveAssetToPathCore(XRAsset asset, string filePath)
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            asset.FilePath = filePath;
            XRAssetGraphUtility.RefreshAssetGraph(asset);
            asset.SerializeTo(filePath, Serializer);
            PostSaved(asset, newAsset: true);
        }

        private static IEnumerable<XRAsset> CollectReachableAssets(object? root)
        {
            if (root is null)
            {
                Debug.Log(ELogCategory.General, "[CollectReachable] Root is null, returning empty.");
                yield break;
            }

            Debug.Log(ELogCategory.General, "[CollectReachable] Starting traversal from root type: {0}", root.GetType().Name);

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
                    Debug.Log(ELogCategory.General, "[CollectReachable] Max depth reached for {0}", current.GetType().Name);
                    continue;
                }

                if (current is XRAsset asset)
                {
                    assetsFound++;
                    Debug.Log(ELogCategory.General, "[CollectReachable] Found XRAsset: {0} '{1}' at depth {2}", asset.GetType().Name, asset.Name ?? "(unnamed)", depth);
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
                    if (type.Name.Contains("EventList") || (count > 0 && depth <= 5))
                        Debug.Log(ELogCategory.General, "[CollectReachable] Traversing IEnumerable {0} with {1} items at depth {2}", type.Name, count, depth);
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
                    Debug.Log(ELogCategory.General, "[CollectReachable] Reflecting into {0} at depth {1}, fields: {2}", type.Name, depth, fields.Length);
                    if (isInterestingType)
                    {
                        foreach (var f in fields)
                        {
                            object? val = null;
                            try { val = f.GetValue(current); }
                            catch { }
                            Debug.Log(ELogCategory.General, "[CollectReachable]   field '{0}' ({1}) = {2}", 
                                f.Name, 
                                f.FieldType.Name, 
                                val is null ? "null" : val.GetType().Name);
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
                        Debug.Log(ELogCategory.General, "[CollectReachable] Found _children field on {0} (declared on {1}) with {2} children", type.Name, field.DeclaringType?.Name ?? "<unknown>", childCount);
                    }

                    depths[value] = depth + 1;
                    stack.Push(value);
                }
            }

            Debug.Log(ELogCategory.General, "[CollectReachable] Traversal complete: visited {0} objects, found {1} XRAssets", visitedCount, assetsFound);
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

        private void CacheAsset(XRAsset asset)
        {
            string path = asset.FilePath ?? string.Empty;
            XRAsset UpdatePathDict(string existingPath, XRAsset existingAsset)
            {
                if (existingAsset is not null)
                {
                    if (existingAsset != asset && !existingAsset.EmbeddedAssets.Contains(asset))
                        existingAsset.EmbeddedAssets.Add(asset);
                    return existingAsset;
                }
                else
                    return asset;
            }
            LoadedAssetsByPathInternal.AddOrUpdate(path, asset, UpdatePathDict);

            if (asset.ID == Guid.Empty)
            {
                Debug.LogWarning("An asset was loaded with an empty ID.");
            }

            if (asset.ID != Guid.Empty)
            {
                XRAsset UpdateIDDict(Guid existingID, XRAsset existingAsset)
                {
                    Debug.Out($"An asset with the ID {existingID} already exists in the asset manager. The new asset will be added to the list of assets with the same ID.");
                    return existingAsset;
                }
                LoadedAssetsByIDInternal.AddOrUpdate(asset.ID, asset, UpdateIDDict);
            }

            asset.PropertyChanged += AssetPropertyChanged;
        }

        /// <summary>
        /// Ensures an asset instance is tracked by the asset manager (path/id caches, dirty tracking hooks).
        /// Useful for assets that are created in-memory and assigned a FilePath, without going through Load/SaveTo.
        /// </summary>
        public void EnsureTracked(XRAsset asset)
        {
            ArgumentNullException.ThrowIfNull(asset);

            if (asset.ID != Guid.Empty && LoadedAssetsByIDInternal.TryGetValue(asset.ID, out var byId) && ReferenceEquals(byId, asset))
                return;

            if (!string.IsNullOrWhiteSpace(asset.FilePath) && LoadedAssetsByPathInternal.TryGetValue(asset.FilePath, out var byPath) && ReferenceEquals(byPath, asset))
                return;

            CacheAsset(asset);

            // If the asset was marked dirty before it was tracked (common for in-memory settings objects),
            // it would have missed the IsDirty PropertyChanged hook. Backfill DirtyAssets here.
            if (asset.IsDirty && asset.ID != Guid.Empty)
            {
                DirtyAssets.TryAdd(asset.ID, asset);
                AssetMarkedDirty?.Invoke(asset);
            }
        }

        void AssetPropertyChanged(object? s, Data.Core.IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(XRAsset.FilePath):
                    {
                        if (s is XRAsset asset)
                        {
                            if (e.PreviousValue is string prev)
                                LoadedAssetsByPathInternal.TryRemove(prev, out _);
                            LoadedAssetsByPathInternal.TryAdd(asset.FilePath ?? string.Empty, asset);
                        }
                    }
                    break;
                case nameof(XRAsset.ID):
                    {
                        if (s is XRAsset asset)
                        {
                            if (e.PreviousValue is Guid prev)
                                LoadedAssetsByIDInternal.TryRemove(prev, out _);
                            LoadedAssetsByIDInternal.TryAdd(asset.ID, asset);
                        }
                    }
                    break;
                case nameof(XRAsset.IsDirty):
                    {
                        if (s is XRAsset asset && asset.IsDirty)
                        {
                            DirtyAssets.TryAdd(asset.ID, asset);
                            AssetMarkedDirty?.Invoke(asset);
                        }
                    }
                    break;
            }
        }

        public FileSystemWatcher GameWatcher { get; } = new FileSystemWatcher();
        public FileSystemWatcher EngineWatcher { get; } = new FileSystemWatcher();
        /// <summary>
        /// This is the path to /Build/CommonAssets/ in the root folder of the engine.
        /// </summary>
        public string EngineAssetsPath { get; }

        private string _gameAssetsPath = Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Assets");
        public string GameAssetsPath
        {
            get => _gameAssetsPath;
            set => UpdateGameAssetsPath(value);
        }

        private string? _gameMetadataPath;
        public string? GameMetadataPath
        {
            get => _gameMetadataPath;
            set => _gameMetadataPath = NormalizeOptionalDirectoryPath(value, nameof(GameMetadataPath));
        }

        private string? _gameCachePath;
        public string? GameCachePath
        {
            get => _gameCachePath;
            set => _gameCachePath = NormalizeOptionalDirectoryPath(value, nameof(GameCachePath));
        }

        private string _packagesPath = Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Packages");
        public string PackagesPath
        {
            get => _packagesPath;
            set => _packagesPath = NormalizeDirectoryPath(value, nameof(PackagesPath));
        }

        private string _librariesPath = Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Libraries");
        public string LibrariesPath
        {
            get => _librariesPath;
            set => _librariesPath = NormalizeDirectoryPath(value, nameof(LibrariesPath));
        }

        public ConcurrentDictionary<string, XRAsset> LoadedAssetsByOriginalPathInternal { get; } = [];
        public ConcurrentDictionary<string, XRAsset> LoadedAssetsByPathInternal { get; } = [];
        public ConcurrentDictionary<Guid, XRAsset> LoadedAssetsByIDInternal { get; } = [];
        public ConcurrentDictionary<Guid, XRAsset> DirtyAssets { get; } = [];
        
        // Track recently saved assets to prevent immediate reload from file watcher
        private readonly ConcurrentDictionary<string, DateTime> _recentlySavedPaths = new(StringComparer.OrdinalIgnoreCase);
        private const double RecentlySavedIgnoreSeconds = 2;
        private const double RecentlySavedPruneAfterSeconds = 10;
        private int _recentlySavedPruneCounter;
        private readonly object _metadataLock = new();
        private static readonly Lazy<Dictionary<string, Type>> ThirdPartyExtensionMap = new(CreateThirdPartyExtensionMap);
        private readonly ConcurrentDictionary<string, object> _thirdPartyImportOptionsCache = new(StringComparer.OrdinalIgnoreCase);

        private void MarkRecentlySaved(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            _recentlySavedPaths[path] = DateTime.UtcNow;

            // Opportunistic prune to prevent unbounded growth.
            if (Interlocked.Increment(ref _recentlySavedPruneCounter) % 128 != 0)
                return;

            DateTime now = DateTime.UtcNow;
            foreach (var kvp in _recentlySavedPaths)
            {
                if ((now - kvp.Value).TotalSeconds > RecentlySavedPruneAfterSeconds)
                    _recentlySavedPaths.TryRemove(kvp.Key, out _);
            }
        }

        private bool ShouldIgnoreWatcherEvent(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!_recentlySavedPaths.TryGetValue(path, out var saveTime))
                return false;

            if ((DateTime.UtcNow - saveTime).TotalSeconds <= RecentlySavedIgnoreSeconds)
                return true;

            _recentlySavedPaths.TryRemove(path, out _);
            return false;
        }

        public XRAsset? GetAssetByID(Guid id)
            => LoadedAssetsByIDInternal.TryGetValue(id, out XRAsset? asset) ? asset : null;

        public bool TryGetAssetByID(Guid id, [NotNullWhen(true)] out XRAsset? asset)
            => LoadedAssetsByIDInternal.TryGetValue(id, out asset);

        public XRAsset? GetAssetByPath(string path)
            => LoadedAssetsByPathInternal.TryGetValue(path, out var asset) ? asset : null;

        public bool TryGetAssetByPath(string path, [NotNullWhen(true)] out XRAsset? asset)
            => LoadedAssetsByPathInternal.TryGetValue(path, out asset);

        public XRAsset? GetAssetByOriginalPath(string path)
            => LoadedAssetsByOriginalPathInternal.TryGetValue(path, out var asset) ? asset : null;

        public bool TryGetAssetByOriginalPath(string path, [NotNullWhen(true)] out XRAsset? asset)
            => LoadedAssetsByOriginalPathInternal.TryGetValue(path, out asset);

        public bool TryResolveAssetPathById(Guid assetId, [NotNullWhen(true)] out string? assetPath)
        {
            assetPath = null;

            if (assetId == Guid.Empty)
                return false;

            if (LoadedAssetsByIDInternal.TryGetValue(assetId, out var asset) && !string.IsNullOrWhiteSpace(asset.FilePath) && File.Exists(asset.FilePath))
            {
                assetPath = asset.FilePath;
                return true;
            }

            if (string.IsNullOrWhiteSpace(GameMetadataPath) || !Directory.Exists(GameMetadataPath))
                return false;

            try
            {
                foreach (string metaFile in Directory.EnumerateFiles(GameMetadataPath, "*.meta", SearchOption.AllDirectories))
                {
                    var meta = TryReadMetadata(metaFile);
                    if (meta?.Guid != assetId || string.IsNullOrWhiteSpace(meta.RelativePath))
                        continue;

                    string candidate = Path.Combine(GameAssetsPath, meta.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                    {
                        assetPath = candidate;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to resolve asset id '{assetId}' from metadata: {ex.Message}");
            }

            return false;
        }

        public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
            => LoadEngineAsset<T>(JobPriority.Normal, relativePathFolders);

        public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, bool bypassJobThread, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return Load<T>(path, priority, bypassJobThread) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return Load<T>(path, priority) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
            => LoadEngineAssetAsync<T>(JobPriority.Normal, relativePathFolders);

        public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, bool bypassJobThread, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return await LoadAsync<T>(path, priority, bypassJobThread) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return await LoadAsync<T>(path, priority) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public T LoadEngineAssetRemote<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
            => LoadEngineAssetRemoteAsync<T>(mode, priority, metadata, relativePathFolders).GetAwaiter().GetResult();

        public T? LoadGameAssetRemote<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
            => LoadGameAssetRemoteAsync<T>(mode, priority, metadata, relativePathFolders).GetAwaiter().GetResult();

        public async Task<T> LoadEngineAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return await LoadAssetRemoteAsync<T>(path, mode, priority, CancellationToken.None, metadata).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"Unable to load engine file at {path} via remote path.");
        }

        public T? LoadGameAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveGameAssetPath(relativePathFolders);
            return Load<T>(path);
        }

        public async Task<T?> LoadGameAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveGameAssetPath(relativePathFolders);
            return await LoadAsync<T>(path);
        }

        public async Task<T?> LoadGameAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveGameAssetPath(relativePathFolders);
            return await LoadAssetRemoteAsync<T>(path, mode, priority, CancellationToken.None, metadata).ConfigureAwait(false);
        }

        public T? LoadByIdRemote<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Guid assetId, RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null) where T : XRAsset, new()
            => LoadByIdRemoteAsync<T>(assetId, mode, priority, metadata, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<T?> LoadByIdRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Guid assetId, RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) where T : XRAsset, new()
        {
            if (assetId == Guid.Empty)
                return null;

            if (TryGetAssetByID(assetId, out var existing) && existing is T typed)
                return typed;

            if (TryResolveAssetPathById(assetId, out var localPath) && File.Exists(localPath))
                return await LoadAsync<T>(localPath, priority).ConfigureAwait(false);

            if (mode == RemoteAssetLoadMode.None)
                return null;

            bool downloaded = await TryDownloadAssetFromRemoteByIdAsync(assetId, typeof(T), priority, cancellationToken, metadata).ConfigureAwait(false);
            if (!downloaded)
                return null;

            if (TryResolveAssetPathById(assetId, out var downloadedPath) && File.Exists(downloadedPath))
                return await LoadAsync<T>(downloadedPath, priority).ConfigureAwait(false);

            return null;
        }

        // Immediate load/save helpers that bypass the job thread on demand
        public T LoadEngineAssetImmediate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return Load<T>(path, JobPriority.Normal, bypassJobThread: true) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public T? LoadImmediate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
            => Load<T>(filePath, JobPriority.Normal, bypassJobThread: true);

        public XRAsset? LoadImmediate(string filePath, Type type)
            => Load(filePath, type, JobPriority.Normal, bypassJobThread: true);

        public void SaveImmediate(XRAsset asset)
            => RunOnJobThreadBlocking(() => { SaveExistingAssetCore(asset); return true; }, JobPriority.Normal, bypassJobThread: true);

        public void SaveToImmediate(XRAsset asset, string directory)
            => RunOnJobThreadBlocking(() => { SaveToDirectoryCore(asset, directory); return true; }, JobPriority.Normal, bypassJobThread: true);

        //public async Task<T?> LoadEngine3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => await XR3rdPartyAsset.LoadAsync<T>(Path.Combine(EngineAssetsPath, Path.Combine(relativePathFolders)));

        //public async Task<T?> LoadGame3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => await XR3rdPartyAsset.LoadAsync<T>(Path.Combine(GameAssetsPath, Path.Combine(relativePathFolders)));

        //public T? LoadEngine3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => XR3rdPartyAsset.Load<T>(Path.Combine(EngineAssetsPath, Path.Combine(relativePathFolders)));

        //public T? LoadGame3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => XR3rdPartyAsset.Load<T>(Path.Combine(GameAssetsPath, Path.Combine(relativePathFolders)));

        /// <summary>
        /// Creates a full path to an asset in the engine's asset directory.
        /// </summary>
        /// <param name="relativePathFolders"></param>
        /// <returns></returns>
        public string ResolveEngineAssetPath(params string[] relativePathFolders)
            => Path.Combine(EngineAssetsPath, Path.Combine(relativePathFolders));

        /// <summary>
        /// Creates a full path to an asset in the game's asset directory.
        /// </summary>
        /// <param name="relativePathFolders"></param>
        /// <returns></returns>
        public string ResolveGameAssetPath(params string[] relativePathFolders)
            => Path.Combine(GameAssetsPath, Path.Combine(relativePathFolders));

        public void Dispose()
        {
            foreach (var asset in LoadedAssetsByIDInternal.Values)
                asset.Destroy();
            LoadedAssetsByIDInternal.Clear();
            LoadedAssetsByPathInternal.Clear();
            LoadedAssetsByOriginalPathInternal.Clear();
        }

        public static string VerifyAssetPath(XRAsset asset, string directory)
        {
            VerifyDirectoryExists(directory);
            string fileName = string.IsNullOrWhiteSpace(asset.Name) ? asset.GetType().Name : asset.Name;
            //Add the asset extension for regular assets
            if (!Path.HasExtension(fileName))
                fileName = $"{fileName}.{AssetExtension}";
            return Path.Combine(directory, fileName);
        }

        public Func<string, bool>? AllowOverwriteCallback { get; set; } = path => true;

        public static string GetUniqueAssetPath(string path)
        {
            if (!File.Exists(path))
                return path;

            string? dir = Path.GetDirectoryName(path);
            if (dir is null)
                return path;

            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int i = 1;
            string newPath;
            do
            {
                newPath = Path.Combine(dir, $"{name} ({i++}){ext}");
            } while (File.Exists(newPath));
            return newPath;
        }

        public event Action<XRAsset>? AssetLoaded;
        public event Action<XRAsset>? AssetSaved;
        public event Action<XRAsset>? AssetMarkedDirty;

        private void PostSaved(XRAsset asset, bool newAsset)
        {
            if (newAsset)
                CacheAsset(asset);
            asset.ClearDirty();
            DirtyAssets.TryRemove(asset.ID, out _);

            // If this is the root asset, clear any embedded dirty entries that point to it.
            foreach (var kvp in DirtyAssets.ToArray())
            {
                if (ReferenceEquals(kvp.Value.SourceAsset, asset))
                {
                    kvp.Value.ClearDirty();
                    DirtyAssets.TryRemove(kvp.Key, out _);
                }
            }
            
            // Track save time to prevent file watcher from reloading
            if (!string.IsNullOrEmpty(asset.FilePath))
                _recentlySavedPaths[asset.FilePath] = DateTime.UtcNow;
            
            AssetSaved?.Invoke(asset);
        }

        private void PostLoaded<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, T? file) where T : XRAsset
        {
            if (file is null)
                return;

            file.Name = Path.GetFileNameWithoutExtension(filePath);
            file.FilePath = filePath;

            // Refresh asset graph to populate SourceAsset/EmbeddedAssets relationships
            XRAssetGraphUtility.RefreshAssetGraph(file);

            CacheAsset(file);
            AssetLoaded?.Invoke(file);
        }

        private async Task<T?> LoadAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, RemoteAssetLoadMode mode, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null) where T : XRAsset, new()
        {
            if (mode == RemoteAssetLoadMode.None || Engine.Jobs.RemoteTransport?.IsConnected != true)
                return await LoadAsync<T>(filePath, priority).ConfigureAwait(false);

            var metadata = additionalMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);

            metadata["path"] = filePath;
            metadata["type"] = typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name;

            byte[]? payload = null;
            var transferMode = RemoteJobTransferMode.RequestFromRemote;

            if (mode == RemoteAssetLoadMode.SendLocalCopy)
            {
                transferMode = RemoteJobTransferMode.PushDataToRemote;
                if (File.Exists(filePath))
                    payload = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            }

            var request = new RemoteJobRequest
            {
                Operation = RemoteJobOperations.AssetLoad,
                TransferMode = transferMode,
                Payload = payload,
                Metadata = metadata,
            };

            RemoteJobResponse response;
            try
            {
                response = await Engine.Jobs.ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset load failed for '{filePath}': {ex.Message}");
                return await LoadAsync<T>(filePath, priority).ConfigureAwait(false);
            }

            if (!response.Success)
            {
                Debug.LogWarning($"Remote asset load failed for '{filePath}': {response.Error ?? "Unknown error"}");
                return null;
            }

            if (response.Payload is null || response.Payload.Length == 0)
            {
                Debug.LogWarning($"Remote asset load returned no data for '{filePath}'.");
                return null;
            }

            string contents = Encoding.UTF8.GetString(response.Payload);
            var asset = Deserializer.Deserialize<T>(contents);
            PostLoaded(filePath, asset);
            return asset;
        }

        private static bool ShouldAttemptRemoteAssetDownload()
        {
            if (Engine.Jobs.RemoteTransport?.IsConnected != true)
                return false;

            return Engine.Networking is Engine.ClientNetworkingManager or Engine.PeerToPeerNetworkingManager;
        }

        private async Task<bool> TryDownloadAssetFromRemoteAsync(string filePath, Type assetType, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null)
        {
            if (!ShouldAttemptRemoteAssetDownload())
                return false;

            var metadata = additionalMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);

            metadata["path"] = filePath;
            metadata["type"] = assetType.AssemblyQualifiedName ?? assetType.FullName ?? assetType.Name;

            var request = new RemoteJobRequest
            {
                Operation = RemoteJobOperations.AssetLoad,
                TransferMode = RemoteJobTransferMode.RequestFromRemote,
                Metadata = metadata,
            };

            RemoteJobResponse response;
            try
            {
                response = await Engine.Jobs.ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset download failed for '{filePath}': {ex.Message}");
                return false;
            }

            if (!response.Success)
            {
                Debug.LogWarning($"Remote asset download failed for '{filePath}': {response.Error ?? "Unknown error"}");
                return false;
            }

            if (response.Payload is null || response.Payload.Length == 0)
            {
                Debug.LogWarning($"Remote asset download returned no data for '{filePath}'.");
                return false;
            }

            if (response.Metadata is not null && response.Metadata.TryGetValue("path", out var serverPath) && !string.IsNullOrWhiteSpace(serverPath))
                filePath = serverPath;

            try
            {
                string? directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllBytesAsync(filePath, response.Payload, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to persist remote asset '{filePath}': {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TryDownloadAssetFromRemoteByIdAsync(Guid assetId, Type assetType, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null)
        {
            if (assetId == Guid.Empty || !ShouldAttemptRemoteAssetDownload())
                return false;

            var metadata = additionalMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);

            metadata["id"] = assetId.ToString("D");
            metadata["type"] = assetType.AssemblyQualifiedName ?? assetType.FullName ?? assetType.Name;

            var request = new RemoteJobRequest
            {
                Operation = RemoteJobOperations.AssetLoad,
                TransferMode = RemoteJobTransferMode.RequestFromRemote,
                Metadata = metadata,
            };

            RemoteJobResponse response;
            try
            {
                response = await Engine.Jobs.ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset download failed for id '{assetId}': {ex.Message}");
                return false;
            }

            if (!response.Success)
            {
                Debug.LogWarning($"Remote asset download failed for id '{assetId}': {response.Error ?? "Unknown error"}");
                return false;
            }

            if (response.Payload is null || response.Payload.Length == 0)
            {
                Debug.LogWarning($"Remote asset download returned no data for id '{assetId}'.");
                return false;
            }

            string? targetPath = null;
            if (response.Metadata is not null && response.Metadata.TryGetValue("path", out var serverPath) && !string.IsNullOrWhiteSpace(serverPath))
            {
                targetPath = serverPath;
            }
            else if (TryResolveAssetPathById(assetId, out var resolvedPath))
            {
                targetPath = resolvedPath;
            }
            else
            {
                targetPath = Path.Combine(GameAssetsPath, $"{assetId:D}.{AssetExtension}");
            }

            try
            {
                string? directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllBytesAsync(targetPath, response.Payload, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to persist remote asset '{assetId}' to '{targetPath}': {ex.Message}");
                return false;
            }
        }

        private T? LoadCore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
        {
            T? file;
#if !DEBUG
            try
            {
#endif
                if (TryGetAssetByPath(filePath, out XRAsset? existingAsset))
                    return existingAsset is T tAsset ? tAsset : null;

                if (!File.Exists(filePath))
                {
                    AssetDiagnostics.RecordMissingAsset(filePath, typeof(T).Name, $"{nameof(AssetManager)}.{nameof(Load)}");
                    return null;
                }

                string extension = Path.GetExtension(filePath);
                if (string.IsNullOrWhiteSpace(extension) || extension.Length <= 1)
                {
                    Debug.LogWarning($"Unable to load asset at '{filePath}' because the file has no extension.");
                    return null;
                }

                string normalizedExtension = extension[1..].ToLowerInvariant();
                file = normalizedExtension == AssetExtension
                    ? DeserializeAssetFile<T>(filePath)
                    : Load3rdPartyWithCache<T>(filePath, normalizedExtension);
                PostLoaded(filePath, file);
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
#if !DEBUG
            try
            {
#endif
                if (TryGetAssetByPath(filePath, out XRAsset? existingAsset))
                    return existingAsset.GetType().IsAssignableTo(type) ? existingAsset : null;

                if (!File.Exists(filePath))
                {
                    AssetDiagnostics.RecordMissingAsset(filePath, type.Name, $"{nameof(AssetManager)}.{nameof(Load)}");
                    return null;
                }

                string extension = Path.GetExtension(filePath);
                if (string.IsNullOrWhiteSpace(extension) || extension.Length <= 1)
                {
                    Debug.LogWarning($"Unable to load asset at '{filePath}' because the file has no extension.");
                    return null;
                }

                string normalizedExtension = extension[1..].ToLowerInvariant();
                file = normalizedExtension == AssetExtension
                    ? DeserializeAssetFile(filePath, type)
                    : Load3rdPartyWithCache(filePath, normalizedExtension, type);
                PostLoaded(filePath, file);
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

        private void SaveExistingAssetCore(XRAsset asset)
        {
            if (asset.FilePath is null)
            {
                Debug.LogWarning("Cannot save an asset without a file path.");
                return;
            }
#if !DEBUG
            try
            {
#endif
                using var t = Engine.Profiler.Start($"AssetManager.Save {asset.FilePath}");
                XRAssetGraphUtility.RefreshAssetGraph(asset);
                asset.SerializeTo(asset.FilePath, Serializer);
                PostSaved(asset, false);
#if !DEBUG
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"An error occurred while saving the asset at '{asset.FilePath}'.");
            }
#endif
        }

        private void SaveToDirectoryCore(XRAsset asset, string directory)
        {
#if !DEBUG
            try
            {
#endif
                string path = VerifyAssetPath(asset, directory);
                using var t = Engine.Profiler.Start($"AssetManager.SaveTo {path}");

                if (File.Exists(path) && !(AllowOverwriteCallback?.Invoke(path) ?? true))
                    path = GetUniqueAssetPath(path);

                asset.FilePath = path;
                XRAssetGraphUtility.RefreshAssetGraph(asset);
                asset.SerializeTo(path, Serializer);
                PostSaved(asset, true);
#if !DEBUG
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"An error occurred while saving the asset to '{directory}'.");
            }
#endif
        }

        public async Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false) where T : XRAsset, new()
        {
            if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
                await TryDownloadAssetFromRemoteAsync(filePath, typeof(T), priority, CancellationToken.None, additionalMetadata: null).ConfigureAwait(false);

            return await RunOnJobThreadAsync(() => LoadCore<T>(filePath), priority, bypassJobThread).ConfigureAwait(false);
        }

        public XRAsset? Load(string filePath, Type type, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
        {
            if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
                TryDownloadAssetFromRemoteAsync(filePath, type, priority, CancellationToken.None, additionalMetadata: null).GetAwaiter().GetResult();

            return RunOnJobThreadBlocking(() => LoadCore(filePath, type), priority, bypassJobThread);
        }

        public T? Load<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false) where T : XRAsset, new()
        {
            if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
                TryDownloadAssetFromRemoteAsync(filePath, typeof(T), priority, CancellationToken.None, additionalMetadata: null).GetAwaiter().GetResult();

            return RunOnJobThreadBlocking(() => LoadCore<T>(filePath), priority, bypassJobThread);
        }

        public Task SaveAsync(XRAsset asset, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
            => RunOnJobThreadAsync(() => { SaveExistingAssetCore(asset); return true; }, priority, bypassJobThread);

        public void Save(XRAsset asset, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
            => RunOnJobThreadBlocking(() => { SaveExistingAssetCore(asset); return true; }, priority, bypassJobThread);

        public void SaveTo(XRAsset asset, Environment.SpecialFolder folder, params string[] folderNames)
            => SaveTo(asset, Path.Combine([Environment.GetFolderPath(folder), ..folderNames]));

        public void SaveTo(XRAsset asset, string directory, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
            => RunOnJobThreadBlocking(() => { SaveToDirectoryCore(asset, directory); return true; }, priority, bypassJobThread);

        public Task SaveToAsync(XRAsset asset, Environment.SpecialFolder folder, params string[] folderNames)
            => SaveToAsync(asset, Path.Combine([Environment.GetFolderPath(folder), .. folderNames]));

        public Task SaveToAsync(XRAsset asset, string directory, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
            => RunOnJobThreadAsync(() => { SaveToDirectoryCore(asset, directory); return true; }, priority, bypassJobThread);

        public Task SaveGameAssetToAsync(XRAsset asset, params string[] folderNames)
            => SaveToAsync(asset, Path.Combine(GameAssetsPath, Path.Combine(folderNames)));
        public void SaveGameAssetTo(XRAsset asset, params string[] folderNames)
            => SaveTo(asset, Path.Combine(GameAssetsPath, Path.Combine(folderNames)));

        #region Prefab helpers

        public SceneNode? InstantiatePrefab(XRPrefabSource prefab,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(prefab);
            return SceneNodePrefabService.Instantiate(prefab, world, parent, maintainWorldTransform);
        }

        public SceneNode? InstantiatePrefab(Guid prefabAssetId,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            if (prefabAssetId == Guid.Empty)
                return null;

            return GetAssetByID(prefabAssetId) is XRPrefabSource prefab
                ? InstantiatePrefab(prefab, world, parent, maintainWorldTransform)
                : null;
        }

        public SceneNode? InstantiatePrefab(string assetPath,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var prefab = Load<XRPrefabSource>(assetPath);
            return prefab is null
                ? null
                : InstantiatePrefab(prefab, world, parent, maintainWorldTransform);
        }

        public async Task<SceneNode?> InstantiatePrefabAsync(string assetPath,
                                                             XRWorldInstance? world = null,
                                                             SceneNode? parent = null,
                                                             bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var prefab = await LoadAsync<XRPrefabSource>(assetPath).ConfigureAwait(false);
            return prefab is null
                ? null
                : InstantiatePrefab(prefab, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(XRPrefabVariant variant,
                                             XRWorldInstance? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(variant);
            return SceneNodePrefabService.InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(Guid variantAssetId,
                                             XRWorldInstance? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            if (variantAssetId == Guid.Empty)
                return null;

            return GetAssetByID(variantAssetId) is XRPrefabVariant variant
                ? InstantiateVariant(variant, world, parent, maintainWorldTransform)
                : null;
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(string assetPath,
                                             XRWorldInstance? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var variant = Load<XRPrefabVariant>(assetPath);
            return variant is null
                ? null
                : InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public async Task<SceneNode?> InstantiateVariantAsync(string assetPath,
                                                              XRWorldInstance? world = null,
                                                              SceneNode? parent = null,
                                                              bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var variant = await LoadAsync<XRPrefabVariant>(assetPath).ConfigureAwait(false);
            return variant is null
                ? null
                : InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        #endregion

        private static readonly IReadOnlyList<IYamlTypeConverter> RegisteredYamlTypeConverters = DiscoverYamlTypeConverters();

        internal static IReadOnlyList<IYamlTypeConverter> YamlTypeConverters => RegisteredYamlTypeConverters;

        public static readonly ISerializer Serializer = CreateSerializer();

        public static readonly IDeserializer Deserializer = CreateDeserializer();

        private static ISerializer CreateSerializer()
        {
            var builder = new SerializerBuilder()
                //.IgnoreFields()
                .EnablePrivateConstructors() //TODO: probably avoid using this
                .EnsureRoundtrip()
                .WithEmissionPhaseObjectGraphVisitor(args => new PolymorphicTypeGraphVisitor(args.InnerVisitor))
                .WithEventEmitter(nextEmitter => new DepthTrackingEventEmitter(nextEmitter))
                //.WithTypeConverter(new XRAssetYamlConverter())
                .IncludeNonPublicProperties()
                //.WithTagMapping("!Transform", typeof(Transform))
                //.WithTagMapping("!UIBoundableTransform", typeof(UIBoundableTransform))
                //.WithTagMapping("!UITransform", typeof(UITransform))
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitEmptyCollections);

            foreach (var converter in RegisteredYamlTypeConverters)
                builder.WithTypeConverter(converter);

            builder.WithTypeConverter(new XRAssetYamlConverter());

            return builder.Build();
        }

        private static IDeserializer CreateDeserializer()
        {
            var builder = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .EnablePrivateConstructors()
                .WithEnforceNullability()
                .WithEnforceRequiredMembers()
                .WithDuplicateKeyChecking()
                .WithNodeDeserializer(
                    inner => new DepthTrackingNodeDeserializer(inner),
                    s => s.InsteadOf<ObjectNodeDeserializer>())
                .WithNodeDeserializer(
                    inner => new NotSupportedAnnotatingNodeDeserializer(inner),
                    s => s.InsteadOf<DictionaryNodeDeserializer>())
                //.WithNodeDeserializer(new XRAssetDeserializer(), w => w.OnTop())
                ;

            foreach (var converter in RegisteredYamlTypeConverters)
                builder.WithTypeConverter(converter);

            builder.WithNodeDeserializer(new XRAssetDeserializer(), w => w.OnTop());

            // Must run after XRAssetDeserializer is registered (XRAssetDeserializer ignores non-XRAsset types).
            builder.WithNodeDeserializer(new PolymorphicYamlNodeDeserializer(), w => w.OnTop());

            return builder.Build();
        }

        [RequiresUnreferencedCode("Calls System.Reflection.Assembly.GetTypes()")]
        private static List<IYamlTypeConverter> DiscoverYamlTypeConverters()
        {
            List<IYamlTypeConverter> converters = [];
            HashSet<Type> registeredTypes = [];

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
                    if (type is null || type.IsAbstract || type.IsInterface)
                        continue;

                    if (!typeof(IYamlTypeConverter).IsAssignableFrom(type))
                        continue;

                    if (type.GetCustomAttribute<YamlTypeConverterAttribute>() is null)
                        continue;

                    if (!registeredTypes.Add(type))
                        continue;

                    if (Activator.CreateInstance(type) is IYamlTypeConverter instance)
                        converters.Add(instance);
                }
            }

            return converters;
        }

        private static T? DeserializeAssetFile<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAsset {filePath}");
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return Deserializer.Deserialize<T>(reader);
        }

        public static XRAsset? DeserializeAssetFile(string filePath, Type type)
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAsset {filePath}");
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return Deserializer.Deserialize(reader, type) as XRAsset;
        }

        private static async Task<T?> DeserializeAssetFileAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAssetAsync {filePath}");
            // YamlDotNet deserialization is synchronous; keep async signature by doing IO + parse on background thread.
            return await Task.Run(() =>
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return Deserializer.Deserialize<T>(reader);
            }).ConfigureAwait(false);
        }

        public static async Task<XRAsset?> DeserializeAssetFileAsync(string filePath, Type type)
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAssetAsync {filePath}");
            // YamlDotNet deserialization is synchronous; keep async signature by doing IO + parse on background thread.
            return await Task.Run(() =>
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return Deserializer.Deserialize(reader, type) as XRAsset;
            }).ConfigureAwait(false);
        }

        private T? Load3rdPartyWithCache<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            bool hasTimestamp = TryGetSourceTimestamp(filePath, out DateTime timestampUtc);
            bool hasCachePath = TryResolveCachePath(filePath, typeof(T), out string cachePath);

            if (hasTimestamp && hasCachePath && TryLoadCachedAsset(cachePath, filePath, timestampUtc, out T? cachedAsset))
            {
                cachedAsset!.OriginalLastWriteTimeUtc = timestampUtc;
                return cachedAsset;
            }

            var asset = Load3rdPartyAsset<T>(filePath, ext);
            if (asset is null)
                return null;

            if (hasTimestamp)
                asset.OriginalLastWriteTimeUtc = timestampUtc;

            if (hasTimestamp && hasCachePath)
                TryWriteCacheAsset(cachePath, asset);

            return asset;
        }

        private XRAsset? Load3rdPartyWithCache(string filePath, string ext, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
        {
            bool hasTimestamp = TryGetSourceTimestamp(filePath, out DateTime timestampUtc);
            bool hasCachePath = TryResolveCachePath(filePath, type, out string cachePath);

            if (hasTimestamp && hasCachePath && TryLoadCachedAsset(cachePath, filePath, timestampUtc, type, out XRAsset? cachedAsset))
            {
                cachedAsset!.OriginalLastWriteTimeUtc = timestampUtc;
                return cachedAsset;
            }

            var asset = Load3rdPartyAsset(filePath, ext, type);
            if (asset is null)
                return null;

            if (hasTimestamp)
                asset.OriginalLastWriteTimeUtc = timestampUtc;

            if (hasTimestamp && hasCachePath)
                TryWriteCacheAsset(cachePath, asset);

            return asset;
        }

        private async Task<T?> Load3rdPartyWithCacheAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            bool hasTimestamp = TryGetSourceTimestamp(filePath, out DateTime timestampUtc);
            bool hasCachePath = TryResolveCachePath(filePath, typeof(T), out string cachePath);

            if (hasTimestamp && hasCachePath)
            {
                var cached = await TryLoadCachedAssetAsync<T>(cachePath, filePath, timestampUtc).ConfigureAwait(false);
                if (cached is not null)
                {
                    cached.OriginalLastWriteTimeUtc = timestampUtc;
                    return cached;
                }
            }

            var asset = await Load3rdPartyAssetAsync<T>(filePath, ext).ConfigureAwait(false);
            if (asset is null)
                return null;

            if (hasTimestamp)
                asset.OriginalLastWriteTimeUtc = timestampUtc;

            if (hasTimestamp && hasCachePath)
                await TryWriteCacheAssetAsync(cachePath, asset).ConfigureAwait(false);

            return asset;
        }

        private static bool TryGetSourceTimestamp(string filePath, out DateTime timestampUtc)
        {
            timestampUtc = File.GetLastWriteTimeUtc(filePath);
            return timestampUtc != DateTime.MinValue;
        }

        private bool TryResolveCachePath(string filePath, Type assetType, out string cachePath)
        {
            cachePath = string.Empty;
            if (string.IsNullOrWhiteSpace(GameCachePath) || string.IsNullOrWhiteSpace(GameAssetsPath))
                return false;

            string projectAssetsRoot = GameAssetsPath;
            string normalizedAssets = Path.GetFullPath(projectAssetsRoot);
            string normalizedSource = Path.GetFullPath(filePath);

            if (!normalizedSource.StartsWith(normalizedAssets, StringComparison.OrdinalIgnoreCase))
                return false;

            string relativePath = Path.GetRelativePath(normalizedAssets, normalizedSource);
            if (relativePath.StartsWith(".."))
                return false;

            string? relativeDirectory = Path.GetDirectoryName(relativePath);
            string originalFileName = Path.GetFileName(relativePath);
            string typeSuffix = assetType.FullName ?? assetType.Name;
            string cacheFileName = $"{originalFileName}.{typeSuffix}.{AssetExtension}";
            cachePath = string.IsNullOrWhiteSpace(relativeDirectory)
                ? Path.Combine(GameCachePath!, cacheFileName)
                : Path.Combine(GameCachePath!, relativeDirectory, cacheFileName);
            return true;
        }

        private static bool TryLoadCachedAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string cachePath, string originalPath, DateTime sourceTimestampUtc, out T? asset) where T : XRAsset, new()
        {
            asset = null;
            if (!File.Exists(cachePath))
                return false;

            try
            {
                var cachedAsset = DeserializeAssetFile<T>(cachePath);
                if (cachedAsset?.OriginalLastWriteTimeUtc is null)
                    return false;

                if (cachedAsset.OriginalLastWriteTimeUtc.Value < sourceTimestampUtc)
                    return false;

                cachedAsset.OriginalPath = originalPath;
                asset = cachedAsset;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to read cached asset '{cachePath}'. {ex.Message}");
                return false;
            }
        }

        private static bool TryLoadCachedAsset(string cachePath, string originalPath, DateTime sourceTimestampUtc, Type type, out XRAsset? asset)
        {
            asset = null;
            if (!File.Exists(cachePath))
                return false;

            try
            {
                var cachedAsset = DeserializeAssetFile(cachePath, type);
                if (cachedAsset?.OriginalLastWriteTimeUtc is null)
                    return false;

                if (cachedAsset.OriginalLastWriteTimeUtc.Value < sourceTimestampUtc)
                    return false;

                cachedAsset.OriginalPath = originalPath;
                asset = cachedAsset;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to read cached asset '{cachePath}'. {ex.Message}");
                return false;
            }
        }

        private async Task<T?> TryLoadCachedAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string cachePath, string originalPath, DateTime sourceTimestampUtc) where T : XRAsset, new()
        {
            if (!File.Exists(cachePath))
                return null;

            try
            {
                var cachedAsset = await DeserializeAssetFileAsync<T>(cachePath).ConfigureAwait(false);
                if (cachedAsset?.OriginalLastWriteTimeUtc is null)
                    return null;

                if (cachedAsset.OriginalLastWriteTimeUtc.Value < sourceTimestampUtc)
                    return null;

                cachedAsset.OriginalPath = originalPath;
                return cachedAsset;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to read cached asset '{cachePath}'. {ex.Message}");
                return null;
            }
        }

        private void TryWriteCacheAsset(string cachePath, XRAsset asset)
        {
            try
            {
                string? directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                asset.SerializeTo(cachePath, Serializer);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to write cached asset '{cachePath}'. {ex.Message}");
            }
        }

        private async Task TryWriteCacheAssetAsync(string cachePath, XRAsset asset)
        {
            try
            {
                string? directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                await asset.SerializeToAsync(cachePath, Serializer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to write cached asset '{cachePath}'. {ex.Message}");
            }
        }

        private static T? Load3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            var exts = typeof(T).GetCustomAttribute<XR3rdPartyExtensionsAttribute>()?.Extensions;
            var match = exts?.FirstOrDefault(e => e.ext == ext);
            if (match is not null)
            {
                if (match.Value.staticLoad)
                {
                    var method = typeof(T).GetMethod("Load3rdPartyStatic", BindingFlags.Public | BindingFlags.Static);
                    if (method is not null)
                    {
                        var asset = method.Invoke(null, [filePath]) as T;
                        if (asset is not null)
                        {
                            asset.OriginalPath = filePath;
                            return asset;
                        }
                        else
                        {
                            Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but the static loader method does not return the correct type or returned null.");
                            return null;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but does not have a static load method.");
                        return null;
                    }
                }
                else
                {
                    var asset = new T
                    {
                        OriginalPath = filePath
                    };
                    if (asset.Load3rdParty(filePath))
                        return asset;
                    else
                    {
                        Debug.LogWarning($"Failed to load 3rd party asset '{filePath}' as type '{typeof(T).Name}'.");
                        return null;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"The file extension '{ext}' is not supported by the asset type '{typeof(T).Name}'.");
                return null;
            }
        }

        private static XRAsset? Load3rdPartyAsset(string filePath, string ext, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
        {
            var exts = type.GetCustomAttribute<XR3rdPartyExtensionsAttribute>()?.Extensions;
            var match = exts?.FirstOrDefault(e => e.ext == ext);
            if (match is not null)
            {
                if (match.Value.staticLoad)
                {
                    var method = type.GetMethod("Load3rdPartyStatic", BindingFlags.Public | BindingFlags.Static);
                    if (method is not null)
                    {
                        var asset = method.Invoke(null, [filePath]) as XRAsset;
                        if (asset is not null)
                        {
                            asset.OriginalPath = filePath;
                            return asset;
                        }
                        else
                        {
                            Debug.LogWarning($"The asset type '{type.Name}' has a 3rd party extension '{ext}' but the static loader method does not return the correct type or returned null.");
                            return null;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"The asset type '{type.Name}' has a 3rd party extension '{ext}' but does not have a static load method.");
                        return null;
                    }
                }
                else
                {
                    if (type.CreateInstance() is not XRAsset asset)
                    {
                        Debug.LogWarning($"Failed to construct 3rd party asset '{filePath}' as type '{type.Name}'.");
                        return null;
                    }

                    asset.OriginalPath = filePath;
                    
                    if (asset.Load3rdParty(filePath))
                        return asset;
                    else
                    {
                        Debug.LogWarning($"Failed to load 3rd party asset '{filePath}' as type '{type.Name}'.");
                        return null;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"The file extension '{ext}' is not supported by the asset type '{type.Name}'.");
                return null;
            }
        }

        private static async Task<T?> Load3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            var exts = typeof(T).GetCustomAttribute<XR3rdPartyExtensionsAttribute>()?.Extensions;
            var match = exts?.FirstOrDefault(e => e.ext == ext);
            if (match is not null)
            {
                if (match.Value.staticLoad)
                {
                    var method = typeof(T).GetMethod("Load3rdPartyStaticAsync", BindingFlags.Public | BindingFlags.Static);
                    if (method is not null)
                    {
                        var assetTask = method.Invoke(null, [filePath]) as Task<T?>;
                        if (assetTask is not null)
                        {
                            var asset = await assetTask.ConfigureAwait(false);
                            if (asset is not null)
                            {
                                asset.OriginalPath = filePath;
                                return asset;
                            }
                            else
                            {
                                Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but the static loader method returned null.");
                                return null;
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but the static loader method does not return the correct type.");
                            return null;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but does not have a static loader method.");
                        return null;
                    }
                }
                else
                {
                    var asset = new T
                    {
                        OriginalPath = filePath
                    };
                    if (await asset.Load3rdPartyAsync(filePath).ConfigureAwait(false))
                        return asset;
                    else
                    {
                        Debug.LogWarning($"Failed to load 3rd party asset '{filePath}' as type '{typeof(T).Name}'.");
                        return null;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"The file extension '{ext}' is not supported by the asset type '{typeof(T).Name}'.");
                return null;
            }
        }
        
        public void SaveAll()
        {
            foreach (var kvp in DirtyAssets)
                Save(kvp.Value);
            DirtyAssets.Clear();
        }

        public async Task SaveAllAsync()
        {
            var tasks = DirtyAssets.Values.Select(x => SaveAsync(x));
            await Task.WhenAll(tasks);
            DirtyAssets.Clear();
        }
    }
}
