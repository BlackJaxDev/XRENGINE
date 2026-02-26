using System;
using System.IO;
using System.Threading.Tasks;
using XREngine.Diagnostics;

namespace XREngine
{
    public partial class AssetManager
    {
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
    }
}
