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
            if (ShouldIgnoreWatcherEvent(args.FullPath))
                return;

            OnFileCreated(args);
            EngineFileCreated?.Invoke(args);
        }
        void OnGameFileCreated(object sender, FileSystemEventArgs args)
        {
            if (ShouldIgnoreWatcherEvent(args.FullPath))
                return;

            OnFileCreated(args);
            HandleMetadataCreated(args.FullPath);
            TryQueueAutoImportForThirdPartyFile(args.FullPath, reason: "created");
            GameFileCreated?.Invoke(args);
        }
        private static void OnFileCreated(FileSystemEventArgs args)
        {
            LogFileWatcherEvent(args.FullPath, $"File '{args.FullPath}' was created.");
        }

        async void OnEngineFileChanged(object sender, FileSystemEventArgs args)
        {
            if (ShouldIgnoreWatcherEvent(args.FullPath))
                return;

            await OnFileChanged(args);
            EngineFileChanged?.Invoke(args);
        }
        async void OnGameFileChanged(object sender, FileSystemEventArgs args)
        {
            if (ShouldIgnoreWatcherEvent(args.FullPath))
                return;

            await OnFileChanged(args);
            HandleMetadataChanged(args.FullPath);
            TryQueueAutoImportForThirdPartyFile(args.FullPath, reason: "changed");
            GameFileChanged?.Invoke(args);
        }
        private async Task OnFileChanged(FileSystemEventArgs args)
        {
            if (ShouldIgnoreWatcherEvent(args.FullPath))
                return;
            
            LogFileWatcherEvent(args.FullPath, $"File '{args.FullPath}' was changed.");
            var asset = GetAssetByPath(args.FullPath);
            if (asset is not null)
                await asset.ReloadAsync(args.FullPath);
        }

        void OnEngineFileDeleted(object sender, FileSystemEventArgs args)
        {
            if (ShouldIgnoreWatcherEvent(args.FullPath))
                return;

            OnFileDeleted(args);
            EngineFileDeleted?.Invoke(args);
        }
        void OnGameFileDeleted(object sender, FileSystemEventArgs args)
        {
            if (ShouldIgnoreWatcherEvent(args.FullPath))
                return;

            OnFileDeleted(args);
            HandleMetadataDeleted(args.FullPath);
            HandleThirdPartyImportOptionsDeleted(args.FullPath);
            GameFileDeleted?.Invoke(args);
        }
        private static void OnFileDeleted(FileSystemEventArgs args)
        {
            LogFileWatcherEvent(args.FullPath, $"File '{args.FullPath}' was deleted.");
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
            if (ShouldIgnoreWatcherRenameEvent(args.OldFullPath, args.FullPath))
                return;

            OnFileRenamed(args);
            HandleMetadataRenamed(args.OldFullPath, args.FullPath);
            HandleThirdPartyImportOptionsRenamed(args.OldFullPath, args.FullPath);
            TryQueueAutoImportForThirdPartyFile(args.FullPath, reason: "renamed");
            GameFileRenamed?.Invoke(args);
        }
        void OnEngineFileRenamed(object sender, RenamedEventArgs args)
        {
            if (ShouldIgnoreWatcherRenameEvent(args.OldFullPath, args.FullPath))
                return;

            OnFileRenamed(args);
            EngineFileRenamed?.Invoke(args);
        }

        private void OnFileRenamed(RenamedEventArgs args)
        {
            LogFileWatcherEvent(args.FullPath, $"File '{args.OldFullPath}' was renamed to '{args.FullPath}'.");

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

        private static void LogFileWatcherEvent(string filePath, string message)
        {
            if (IsTextureFilePath(filePath))
                Debug.Textures(message);
            else
                Debug.Out(message);
        }

        private static bool IsTextureFilePath(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tga", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ktx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ktx2", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".exr", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".hdr", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
        }
    }
}
