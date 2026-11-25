using XREngine.Core.Files;
using System;
using System.IO;
using System.Threading.Tasks;
using XREngine.Diagnostics;

namespace XREngine
{
    public partial class AssetManager
    {
        private bool IsCurrentHostWatcherSource(IRuntimeAssetSource? source, int epoch)
            => SupportsSynchronousAssetWork
                && ReferenceEquals(_watcherSourceAtSubscription, source)
                && _watcherEpochAtSubscription == epoch
                && ReferenceEquals(_runtimeAssetSource, source)
                && RuntimeSourceEpoch == epoch;

        private bool IsCurrentHostWatcherSource()
            => IsCurrentHostWatcherSource(_runtimeAssetSource, RuntimeSourceEpoch);

        public event Action<AssetFileChangeEventArgs>? EngineFileCreated;
        public event Action<AssetFileChangeEventArgs>? EngineFileChanged;
        public event Action<AssetFileChangeEventArgs>? EngineFileDeleted;
        public event Action<AssetFileRenameEventArgs>? EngineFileRenamed;

        public event Action<AssetFileChangeEventArgs>? GameFileCreated;
        public event Action<AssetFileChangeEventArgs>? GameFileChanged;
        public event Action<AssetFileChangeEventArgs>? GameFileDeleted;
        public event Action<AssetFileRenameEventArgs>? GameFileRenamed;

        void OnEngineFileCreated(object sender, AssetFileChangeEventArgs args)
        {
            if (!ReferenceEquals(sender, EngineWatcher.CurrentInstallation) ||
                !IsCurrentHostWatcherSource() || ShouldIgnoreWatcherEvent(args.FullPath) ||
                (!File.Exists(args.FullPath) && !Directory.Exists(args.FullPath)))
                return;

            OnFileCreated(args);
            EngineFileCreated?.Invoke(args);
        }
        void OnGameFileCreated(object sender, AssetFileChangeEventArgs args)
        {
            AuthoringContext context;
            lock (_metadataLock)
            {
                if (!TryAdmitGameWatcherEvent(sender, out context) || ShouldIgnoreWatcherEvent(args.FullPath) ||
                    !IsLivePath(context, args.FullPath) ||
                    !TryMapMetadataPath(context, args.FullPath, out _, out _))
                    return;
                HandleMetadataCreated(args.FullPath, context);
            }
            OnFileCreated(args);
            if (!IsAdmittedGameWatcherEvent(sender, context, args.FullPath, shouldExist: true))
                return;
            context.AuthoringServices.QueueAutoImport(CreateAuthoringTicket(context), args.FullPath, "created");
            if (!IsAdmittedGameWatcherEvent(sender, context, args.FullPath, shouldExist: true))
                return;
            GameFileCreated?.Invoke(args);
        }
        private static void OnFileCreated(AssetFileChangeEventArgs args)
        {
            LogFileWatcherEvent(args.FullPath, $"File '{args.FullPath}' was created.");
        }

        async void OnEngineFileChanged(object sender, AssetFileChangeEventArgs args)
        {
            IRuntimeAssetSource? source = _runtimeAssetSource;
            int epoch = RuntimeSourceEpoch;
            if (!ReferenceEquals(sender, EngineWatcher.CurrentInstallation) ||
                !IsCurrentHostWatcherSource(source, epoch) || ShouldIgnoreWatcherEvent(args.FullPath) ||
                (!File.Exists(args.FullPath) && !Directory.Exists(args.FullPath)))
                return;

            await OnFileChanged(args, source, epoch);
            if (!ReferenceEquals(sender, EngineWatcher.CurrentInstallation) ||
                !IsCurrentHostWatcherSource(source, epoch))
                return;
            EngineFileChanged?.Invoke(args);
        }
        async void OnGameFileChanged(object sender, AssetFileChangeEventArgs args)
        {
            AuthoringContext context;
            lock (_metadataLock)
            {
                if (!TryAdmitGameWatcherEvent(sender, out context) || ShouldIgnoreWatcherEvent(args.FullPath) ||
                    !IsLivePath(context, args.FullPath) ||
                    !TryMapMetadataPath(context, args.FullPath, out _, out _))
                    return;
            }

            await OnFileChanged(args, context.Source, context.SourceEpoch, context);
            lock (_metadataLock)
            {
                if (!TryAdmitGameWatcherEvent(sender, out AuthoringContext current) ||
                    !ReferenceEquals(current, context) || !IsLivePath(context, args.FullPath))
                    return;
                HandleMetadataChanged(args.FullPath, context);
            }
            if (!IsAdmittedGameWatcherEvent(sender, context, args.FullPath, shouldExist: true))
                return;
            context.AuthoringServices.QueueAutoImport(CreateAuthoringTicket(context), args.FullPath, "changed");
            if (!IsAdmittedGameWatcherEvent(sender, context, args.FullPath, shouldExist: true))
                return;
            GameFileChanged?.Invoke(args);
        }
        private async Task OnFileChanged(AssetFileChangeEventArgs args, IRuntimeAssetSource? source, int epoch,
            AuthoringContext? context = null)
        {
            if (!IsCurrentHostWatcherSource(source, epoch) || ShouldIgnoreWatcherEvent(args.FullPath) ||
                (context is not null && !IsCurrentAuthoringContext(context)))
                return;
            
            LogFileWatcherEvent(args.FullPath, $"File '{args.FullPath}' was changed.");
            var asset = GetAssetByPath(args.FullPath);
            if (asset is not null)
                await asset.ReloadAsync(args.FullPath);
        }

        void OnEngineFileDeleted(object sender, AssetFileChangeEventArgs args)
        {
            if (!ReferenceEquals(sender, EngineWatcher.CurrentInstallation) ||
                !IsCurrentHostWatcherSource() || ShouldIgnoreWatcherEvent(args.FullPath) ||
                File.Exists(args.FullPath) || Directory.Exists(args.FullPath))
                return;

            OnFileDeleted(args);
            EngineFileDeleted?.Invoke(args);
        }
        void OnGameFileDeleted(object sender, AssetFileChangeEventArgs args)
        {
            AuthoringContext context;
            lock (_metadataLock)
            {
                if (!TryAdmitGameWatcherEvent(sender, out context) || ShouldIgnoreWatcherEvent(args.FullPath) ||
                    IsLivePath(context, args.FullPath) ||
                    !TryMapMetadataPath(context, args.FullPath, out _, out _))
                    return;
                HandleMetadataDeleted(args.FullPath, context);
            }
            OnFileDeleted(args);
            if (!IsAdmittedGameWatcherEvent(sender, context, args.FullPath, shouldExist: false))
                return;
            context.AuthoringServices.HandleSourceDeleted(CreateAuthoringTicket(context), args.FullPath);
            if (!IsAdmittedGameWatcherEvent(sender, context, args.FullPath, shouldExist: false))
                return;
            GameFileDeleted?.Invoke(args);
        }
        private static void OnFileDeleted(AssetFileChangeEventArgs args)
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

        void OnGameFileError(object sender, AssetMonitorErrorEventArgs args)
        {
            if (!ReferenceEquals(sender, GameWatcher.CurrentInstallation))
                return;
            OnFileError(args);
        }
        void OnEngineFileError(object sender, AssetMonitorErrorEventArgs args)
        {
            if (!ReferenceEquals(sender, EngineWatcher.CurrentInstallation))
                return;
            OnFileError(args);
        }
        private static void OnFileError(AssetMonitorErrorEventArgs args)
        {
            Debug.LogWarning($"An error occurred in the file system watcher: {args.GetException().Message}");
        }

        void OnGameFileRenamed(object sender, AssetFileRenameEventArgs args)
        {
            AuthoringContext context;
            bool metadataConflict;
            XRAsset? fileAsset = null;
            XRAsset? originalAsset = null;
            lock (_metadataLock)
            {
                if (!TryAdmitGameWatcherEvent(sender, out context) ||
                    ShouldIgnoreWatcherRenameEvent(args.OldFullPath, args.FullPath) ||
                    IsLivePath(context, args.OldFullPath) || !IsLivePath(context, args.FullPath) ||
                    !TryMapMetadataPath(context, args.OldFullPath, out _, out _) ||
                    !TryMapMetadataPath(context, args.FullPath, out _, out _) ||
                    LoadedAssetsByPathInternal.ContainsKey(args.FullPath) ||
                    LoadedAssetsByOriginalPathInternal.ContainsKey(args.FullPath))
                    return;
                metadataConflict = !HandleMetadataRenamed(args.OldFullPath, args.FullPath, context);
                if (!metadataConflict)
                    (fileAsset, originalAsset) = RelocateLoadedBindings(args);
            }
            if (metadataConflict)
            {
                Debug.LogWarning($"Asset metadata rename was skipped because destination metadata exists: '{args.FullPath}'.");
                return;
            }
            ApplyLoadedBindingRelocation(args, fileAsset, originalAsset, context);
            if (!IsAdmittedGameWatcherRename(sender, context, args.OldFullPath, args.FullPath))
                return;
            context.AuthoringServices.HandleSourceRenamed(CreateAuthoringTicket(context), args.OldFullPath, args.FullPath);
            if (!IsAdmittedGameWatcherRename(sender, context, args.OldFullPath, args.FullPath))
                return;
            context.AuthoringServices.QueueAutoImport(CreateAuthoringTicket(context), args.FullPath, "renamed");
            if (!IsAdmittedGameWatcherRename(sender, context, args.OldFullPath, args.FullPath))
                return;
            GameFileRenamed?.Invoke(args);
        }
        void OnEngineFileRenamed(object sender, AssetFileRenameEventArgs args)
        {
            if (!ReferenceEquals(sender, EngineWatcher.CurrentInstallation) ||
                !IsCurrentHostWatcherSource() || ShouldIgnoreWatcherRenameEvent(args.OldFullPath, args.FullPath) ||
                File.Exists(args.OldFullPath) || Directory.Exists(args.OldFullPath) ||
                (!File.Exists(args.FullPath) && !Directory.Exists(args.FullPath)))
                return;

            OnFileRenamed(args);
            EngineFileRenamed?.Invoke(args);
        }

        private void OnFileRenamed(AssetFileRenameEventArgs args)
        {
            if (!IsCurrentHostWatcherSource())
                return;
            XRAsset? fileAsset;
            XRAsset? originalAsset;
            lock (_metadataLock)
                (fileAsset, originalAsset) = RelocateLoadedBindings(args);
            ApplyLoadedBindingRelocation(args, fileAsset, originalAsset, null);
        }

        private (XRAsset? FileAsset, XRAsset? OriginalAsset) RelocateLoadedBindings(AssetFileRenameEventArgs args)
        {
            XRAsset? fileAsset = null;
            XRAsset? originalAsset = null;
            if (LoadedAssetsByPathInternal.TryGetValue(args.OldFullPath, out var asset))
            {
                if (LoadedAssetsByPathInternal.TryAdd(args.FullPath, asset))
                {
                    if (LoadedAssetsByPathInternal.TryRemove(args.OldFullPath, out _))
                        fileAsset = asset;
                    else
                        LoadedAssetsByPathInternal.TryRemove(args.FullPath, out _);
                }
            }
            if (LoadedAssetsByOriginalPathInternal.TryGetValue(args.OldFullPath, out asset))
            {
                if (LoadedAssetsByOriginalPathInternal.TryAdd(args.FullPath, asset))
                {
                    if (LoadedAssetsByOriginalPathInternal.TryRemove(args.OldFullPath, out _))
                        originalAsset = asset;
                    else
                        LoadedAssetsByOriginalPathInternal.TryRemove(args.FullPath, out _);
                }
            }
            return (fileAsset, originalAsset);
        }

        private void ApplyLoadedBindingRelocation(AssetFileRenameEventArgs args,
            XRAsset? fileAsset, XRAsset? originalAsset, AuthoringContext? context)
        {
            if (context is not null && !IsCurrentAuthoringContext(context))
                return;
            LogFileWatcherEvent(args.FullPath, $"File '{args.OldFullPath}' was renamed to '{args.FullPath}'.");
            if (fileAsset is not null)
                fileAsset.FilePath = args.FullPath;
            if (originalAsset is not null && (context is null || IsCurrentAuthoringContext(context)))
            {
                originalAsset.OriginalPath = args.FullPath;
                if (context is null || IsCurrentAuthoringContext(context))
                    originalAsset.Reload();
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
