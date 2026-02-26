using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Diagnostics;
using XREngine.Serialization;

namespace XREngine
{
    public partial class AssetManager
    {
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

        public void SaveImmediate(XRAsset asset)
            => RunOnJobThreadBlocking(() => { SaveExistingAssetCore(asset); return true; }, JobPriority.Normal, bypassJobThread: true);

        public void SaveToImmediate(XRAsset asset, string directory)
            => RunOnJobThreadBlocking(() => { SaveToDirectoryCore(asset, directory); return true; }, JobPriority.Normal, bypassJobThread: true);

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
